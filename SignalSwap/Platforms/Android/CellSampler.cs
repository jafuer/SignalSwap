using System.Runtime.Versioning;
using Android.Content;
using Android.Telephony;
using SignalSwap.Core;

namespace SignalSwap;

public sealed record CellSnapshot(string Headline, QualityLevel Level, string Details);

public static class CellSampler
{
    public static CellSnapshot Read()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            return new CellSnapshot("Needs Android 10 or newer", QualityLevel.None, "");

        try
        {
            return ReadModern(global::Android.App.Application.Context);
        }
        catch (Java.Lang.Exception ex)
        {
            return new CellSnapshot("Unavailable", QualityLevel.None, ex.Message ?? "");
        }
    }

    [SupportedOSPlatform("android29.0")]
    static CellSnapshot ReadModern(Context context)
    {
        if (context.GetSystemService(Context.TelephonyService) is not TelephonyManager phone)
            return new CellSnapshot("No cellular hardware", QualityLevel.None, "");

        var carrier = string.IsNullOrWhiteSpace(phone.NetworkOperatorName) ? "Unknown carrier" : phone.NetworkOperatorName;
        var strengths = phone.SignalStrength?.CellSignalStrengths;
        if (strengths == null || strengths.Count == 0)
            return new CellSnapshot("No signal", QualityLevel.Poor, carrier);

        var primary = strengths[0];
        var level = Rate(primary);
        var lines = new List<string> { carrier };
        lines.AddRange(strengths.Select(Describe));
        string dbm = Valid(primary.Dbm) is int value ? $"{value} dBm" : "Signal";
        return new CellSnapshot(dbm, level, string.Join("\n", lines));
    }

    [SupportedOSPlatform("android29.0")]
    static string Describe(CellSignalStrength s) => s switch
    {
        CellSignalStrengthNr nr => $"5G: RSRP {Show(nr.SsRsrp)} dBm, RSRQ {Show(nr.SsRsrq)} dB, SINR {Show(nr.SsSinr)} dB",
        CellSignalStrengthLte lte => $"LTE: RSRP {Show(lte.Rsrp)} dBm, RSRQ {Show(lte.Rsrq)} dB, SNR {Show(lte.Rssnr)} dB",
        CellSignalStrengthWcdma w => $"3G: {Show(w.Dbm)} dBm",
        CellSignalStrengthGsm g => $"2G: {Show(g.Dbm)} dBm",
        _ => $"{s.GetType().Name}: {Show(s.Dbm)} dBm"
    };

    [SupportedOSPlatform("android29.0")]
    static QualityLevel Rate(CellSignalStrength s)
    {
        int? rsrp = s switch
        {
            CellSignalStrengthLte lte => Valid(lte.Rsrp),
            CellSignalStrengthNr nr => Valid(nr.SsRsrp),
            _ => null
        };

        if (rsrp is int r)
            return r switch
            {
                >= -80 => QualityLevel.Excellent,
                >= -90 => QualityLevel.Good,
                >= -100 => QualityLevel.Fair,
                >= -110 => QualityLevel.Weak,
                _ => QualityLevel.Poor
            };

        return s.Level switch
        {
            >= 4 => QualityLevel.Excellent,
            3 => QualityLevel.Good,
            2 => QualityLevel.Fair,
            1 => QualityLevel.Weak,
            _ => QualityLevel.Poor
        };
    }

    static int? Valid(int value) => value == int.MaxValue || value == int.MinValue ? null : value;

    static string Show(int value) => Valid(value)?.ToString() ?? "n/a";
}

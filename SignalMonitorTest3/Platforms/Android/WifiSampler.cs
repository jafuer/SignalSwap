using Android.Content;
using Android.Net.Wifi;

namespace SignalMonitorTest3;

public sealed record WifiSnapshot(
    bool Enabled,
    bool Connected,
    string? Ssid,
    string? Bssid,
    string? SecurityType,
    int? Rssi,
    int? FrequencyMhz,
    int? LinkSpeedMbps);

public static class WifiSampler
{
    static readonly WifiSnapshot Off = new(false, false, null, null, null, null, null, null);
    static readonly WifiSnapshot Disconnected = new(true, false, null, null, null, null, null, null);
    static readonly Dictionary<string, string> SecurityByBssid = new(StringComparer.OrdinalIgnoreCase);

    public static WifiSnapshot Read()
    {
        var wifi = global::Android.App.Application.Context.GetSystemService(Context.WifiService) as WifiManager;
        if (wifi == null || !wifi.IsWifiEnabled)
            return Off;

        var info = wifi.ConnectionInfo;
        if (info == null || info.Rssi <= -127)
            return Disconnected;

        var ssid = info.SSID?.Trim('"');
        if (string.IsNullOrEmpty(ssid) || ssid == "<unknown ssid>")
            ssid = null;

        var bssid = info.BSSID;
        if (string.IsNullOrEmpty(bssid) || bssid == "02:00:00:00:00:00")
            bssid = null;

        return new WifiSnapshot(
            true,
            true,
            ssid,
            bssid,
            SecurityOf(wifi, bssid),
            info.Rssi,
            info.Frequency > 0 ? info.Frequency : (int?)null,
            info.LinkSpeed > 0 ? info.LinkSpeed : (int?)null);
    }

    static string? SecurityOf(WifiManager wifi, string? bssid)
    {
        if (bssid == null)
            return null;
        if (SecurityByBssid.TryGetValue(bssid, out var cached))
            return cached;

        try
        {
            var match = wifi.ScanResults?.FirstOrDefault(r => string.Equals(r.Bssid, bssid, StringComparison.OrdinalIgnoreCase));
            if (match?.Capabilities is not { } capabilities)
                return null;

            var type = Classify(capabilities);
            SecurityByBssid[bssid] = type;
            return type;
        }
        catch (Java.Lang.SecurityException)
        {
            return null;
        }
    }

    static string Classify(string capabilities)
    {
        var caps = capabilities.ToUpperInvariant();
        bool sae = caps.Contains("SAE");
        bool psk = caps.Contains("PSK");

        if (caps.Contains("EAP")) return "WPA-Enterprise";
        if (sae && psk) return "WPA2/WPA3";
        if (sae) return "WPA3";
        if (psk) return caps.Contains("RSN") || caps.Contains("WPA2") ? "WPA2" : "WPA";
        if (caps.Contains("OWE")) return "OWE";
        if (caps.Contains("WEP")) return "WEP";
        return "Open";
    }
}

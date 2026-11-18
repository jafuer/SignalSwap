namespace SignalSwap.Core;

public sealed class SignalMonitor
{
    static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(10);
    static readonly TimeSpan FastProbeEvery = TimeSpan.FromSeconds(3);
    static readonly TimeSpan FirstProbeDelay = TimeSpan.FromSeconds(2);

    readonly AppSettings settings;
    readonly Func<double?> probe;
    readonly SignalRater rater = new();
    Task<double?>? probing;
    string? probingBssid;
    DateTimeOffset nextProbe;
    string? bssid;

    public SignalMonitor(AppSettings settings, Func<double?> probe)
    {
        this.settings = settings;
        this.probe = probe;
    }

    public SignalStatus Current { get; private set; } = SignalStatus.Empty;

    public SignalStatus Update(WifiSnapshot wifi, DateTimeOffset now, bool fast)
    {
        if (!wifi.Connected || wifi.Rssi is not int rssi)
        {
            rater.Reset();
            bssid = null;
            return Current = new SignalStatus(wifi, QualityReport.None);
        }

        if (wifi.Bssid != bssid)
        {
            rater.Reset();
            bssid = wifi.Bssid;
            nextProbe = now + FirstProbeDelay;
        }

        if (probing is { IsCompleted: true } done)
        {
            if (probingBssid == bssid)
                rater.AddProbe(now, done.Status == TaskStatus.RanToCompletion ? done.Result : null);
            probing = null;
        }

        var interval = fast ? FastProbeEvery : ProbeEvery;
        if (nextProbe - now > interval)
            nextProbe = now + interval;

        if (settings.ConnectionTest && probing == null && now >= nextProbe)
        {
            probingBssid = bssid;
            probing = Task.Run(probe);
            nextProbe = now + interval;
        }

        rater.AddSample(now, rssi, wifi.FrequencyMhz, wifi.DownloadLinkMbps, wifi.Validated);
        return Current = new SignalStatus(wifi, rater.Evaluate(now));
    }
}

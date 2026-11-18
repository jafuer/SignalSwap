namespace SignalSwap.Core;

public sealed record WifiSnapshot(
    bool Enabled,
    bool Connected,
    string? Ssid,
    string? Bssid,
    string? SecurityType,
    int? Rssi,
    int? FrequencyMhz,
    int? LinkSpeedMbps,
    int? RxLinkMbps = null,
    int? TxLinkMbps = null,
    int? WifiGeneration = null,
    bool? Validated = null)
{
    public static WifiSnapshot Off { get; } = new(false, false, null, null, null, null, null, null);

    public static WifiSnapshot Disconnected { get; } = new(true, false, null, null, null, null, null, null);

    public int? DownloadLinkMbps => RxLinkMbps ?? LinkSpeedMbps;

    public string Band => FrequencyMhz switch
    {
        >= 5925 => "6 GHz",
        >= 4900 => "5 GHz",
        > 0 => "2.4 GHz",
        _ => "Unknown band"
    };

    public int? Channel => FrequencyMhz switch
    {
        2484 => 14,
        >= 2412 and < 2484 => (FrequencyMhz - 2407) / 5,
        >= 5955 => (FrequencyMhz - 5950) / 5,
        >= 5000 => (FrequencyMhz - 5000) / 5,
        _ => null
    };
}

public sealed record SignalStatus(WifiSnapshot Wifi, QualityReport Report)
{
    public static SignalStatus Empty { get; } = new(WifiSnapshot.Off, QualityReport.None);
}

public sealed class ScoreHistory
{
    readonly int capacity;
    readonly List<(DateTimeOffset At, int? Score)> points = new();

    public ScoreHistory(int capacity) => this.capacity = capacity;

    public IReadOnlyList<(DateTimeOffset At, int? Score)> Points => points;

    public void Add(DateTimeOffset at, QualityReport report)
    {
        points.Add((at, report.Level == QualityLevel.None ? null : report.Score));
        if (points.Count > capacity)
            points.RemoveAt(0);
    }
}

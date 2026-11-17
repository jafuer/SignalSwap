namespace SignalMonitorTest3.Mapping;

public sealed record SignalReading(
    Guid ClientId,
    DateTimeOffset RecordedAt,
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    bool ManualPosition,
    string Ssid,
    string Bssid,
    string? SecurityType,
    int? SignalStrength,
    int? FrequencyMhz,
    int? LinkSpeedMbps,
    int? Floor = null);

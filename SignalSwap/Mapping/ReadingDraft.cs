namespace SignalSwap.Mapping;

public sealed record ReadingDraft(
    Guid ClientId,
    DateTimeOffset RecordedAt,
    string Ssid,
    string Bssid,
    string? SecurityType,
    int? SignalStrength,
    int? FrequencyMhz,
    int? LinkSpeedMbps,
    int Floor)
{
    public SignalReading Place(double latitude, double longitude, double? accuracyMeters, bool manualPosition) => new(
        ClientId,
        RecordedAt,
        latitude,
        longitude,
        accuracyMeters,
        manualPosition,
        Ssid,
        Bssid,
        SecurityType,
        SignalStrength,
        FrequencyMhz,
        LinkSpeedMbps,
        Floor);
}

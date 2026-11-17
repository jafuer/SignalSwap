namespace SignalSwap.Api.Models;

public sealed record RegisterRequest(string? Username, string? Email, string? Password);

public sealed record LoginRequest(string? Login, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record AuthResponse(int UserId, string Username, string Token, DateTimeOffset ExpiresAt);

public sealed record ReadingDto(
    Guid ClientId,
    DateTimeOffset RecordedAt,
    double Latitude,
    double Longitude,
    double? AccuracyMeters,
    bool ManualPosition,
    string? Ssid,
    string? Bssid,
    string? SecurityType,
    int? SignalStrength,
    int? FrequencyMhz,
    int? LinkSpeedMbps,
    int? Floor);

public sealed record Rejection(Guid ClientId, string Reason);

public sealed record UploadResult(IReadOnlyList<Guid> Synced, IReadOnlyList<Rejection> Rejected, int DeadZonesChanged);

public sealed record ClearResult(int ReadingsDeleted, int DeadZonesDeleted);

public sealed record NetworkSummary(string Ssid, int Readings, int Floors, DateTimeOffset LastRecordedAt);

public sealed record DeadZoneDto(
    int Id,
    string? Ssid,
    double Latitude,
    double Longitude,
    int Floor,
    string? Notes,
    string Source,
    DateTimeOffset ReportedAt,
    DateTimeOffset? ResolvedAt);

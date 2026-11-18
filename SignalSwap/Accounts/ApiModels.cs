using System.Text.Json;
using System.Text.Json.Serialization;
using SignalSwap.Mapping;

namespace SignalSwap.Accounts;

public sealed record RegisterRequest(string Username, string Email, string Password);

public sealed record LoginRequest(string Login, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AuthResponse(int UserId, string Username, string Token, DateTimeOffset ExpiresAt);

public sealed record Rejection(Guid ClientId, string Reason);

public sealed record UploadResult(List<Guid> Synced, List<Rejection> Rejected, int DeadZonesChanged);

public sealed record ClearResult(int ReadingsDeleted, int DeadZonesDeleted);

public sealed record NetworkSummary(string Ssid, int Readings, int Floors, DateTimeOffset LastRecordedAt);

public sealed record ProblemResponse(string? Title, Dictionary<string, string[]>? Errors);

public sealed record Session(int UserId, string Username, string Token, DateTimeOffset ExpiresAt);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(AuthResponse))]
[JsonSerializable(typeof(UploadResult))]
[JsonSerializable(typeof(ClearResult))]
[JsonSerializable(typeof(List<NetworkSummary>))]
[JsonSerializable(typeof(ProblemResponse))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(SignalReading))]
[JsonSerializable(typeof(List<SignalReading>))]
[JsonSerializable(typeof(IReadOnlyList<SignalReading>))]
public partial class ApiJson : JsonSerializerContext
{
}

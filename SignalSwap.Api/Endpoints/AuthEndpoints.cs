using System.Net.Mail;
using System.Security.Claims;
using Dapper;
using SignalSwap.Api.Auth;
using SignalSwap.Api.Data;
using SignalSwap.Api.Models;

namespace SignalSwap.Api.Endpoints;

public static class AuthEndpoints
{
    const int MinPasswordLength = 8;
    const int MaxPasswordLength = 256;
    static readonly Lazy<string> DummyHash = new(() => PasswordHasher.Hash(Guid.NewGuid().ToString()));

    const string FindUserSql = """
        SELECT id AS Id, username AS Username, password_hash AS PasswordHash,
               failed_logins AS FailedLogins, locked_until AS LockedUntil, token_version AS TokenVersion
        FROM dbo.app_user
        WHERE username = @Login OR email = @Email
        """;

    const string FindUserByIdSql = """
        SELECT id AS Id, username AS Username, password_hash AS PasswordHash,
               failed_logins AS FailedLogins, locked_until AS LockedUntil, token_version AS TokenVersion
        FROM dbo.app_user
        WHERE id = @Id
        """;

    const string RecordFailureSql = """
        UPDATE dbo.app_user
        SET failed_logins = CASE WHEN failed_logins + 1 >= @Limit THEN 0 ELSE failed_logins + 1 END,
            locked_until = CASE WHEN failed_logins + 1 >= @Limit THEN DATEADD(second, @LockSeconds, SYSUTCDATETIME()) ELSE locked_until END
        WHERE id = @Id
        """;

    const string RecordSuccessSql = """
        UPDATE dbo.app_user
        SET failed_logins = 0, locked_until = NULL
        WHERE id = @Id AND (failed_logins <> 0 OR locked_until IS NOT NULL)
        """;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");
        group.MapPost("/register", RegisterAsync).RequireRateLimiting(AuthLimits.Policy);
        group.MapPost("/login", LoginAsync).RequireRateLimiting(AuthLimits.Policy);
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization().RequireRateLimiting(AuthLimits.Policy);
        group.MapPost("/logout-everywhere", LogoutEverywhereAsync).RequireAuthorization();
        group.MapGet("/me", (ClaimsPrincipal user) =>
            Results.Ok(new { userId = user.UserId(), username = user.Identity?.Name }))
            .RequireAuthorization();
    }

    static async Task<IResult> RegisterAsync(RegisterRequest request, Db db, TokenService tokens, CancellationToken ct)
    {
        string username = request.Username?.Trim() ?? "";
        string email = request.Email?.Trim().ToLowerInvariant() ?? "";
        string password = request.Password ?? "";

        var errors = new Dictionary<string, string[]>();
        if (username.Length is < 3 or > 50 || username.Contains('@'))
            errors["username"] = new[] { "Use 3 to 50 characters, without an @." };
        if (email.Length > 255 || !MailAddress.TryCreate(email, out _))
            errors["email"] = new[] { "Enter a valid email address." };
        if (PasswordProblem(password) is { } problem)
            errors["password"] = new[] { problem };
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        await using var connection = await db.OpenAsync(ct);
        string? taken = await connection.QueryFirstOrDefaultAsync<string>("""
            SELECT TOP 1 CASE WHEN username = @Username THEN 'username' ELSE 'email' END
            FROM dbo.app_user
            WHERE username = @Username OR email = @Email
            """, new { Username = username, Email = email });
        if (taken != null)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"That {taken} is already registered.");

        int id = await connection.ExecuteScalarAsync<int>("""
            INSERT INTO dbo.app_user (username, email, password_hash, created_at)
            OUTPUT INSERTED.id
            VALUES (@Username, @Email, @PasswordHash, SYSUTCDATETIME())
            """, new { Username = username, Email = email, PasswordHash = PasswordHasher.Hash(password) });

        var (token, expires) = tokens.Create(id, username, 0);
        return Results.Ok(new AuthResponse(id, username, token, expires));
    }

    static async Task<IResult> LoginAsync(LoginRequest request, Db db, TokenService tokens, CancellationToken ct)
    {
        string login = request.Login?.Trim() ?? "";
        string password = request.Password ?? "";
        if (login.Length == 0 || password.Length == 0 || password.Length > MaxPasswordLength)
            return WrongLogin();

        await using var connection = await db.OpenAsync(ct);
        var user = await connection.QueryFirstOrDefaultAsync<UserRow>(FindUserSql, new { Login = login, Email = login.ToLowerInvariant() });
        if (user == null)
        {
            PasswordHasher.Verify(password, DummyHash.Value);
            return WrongLogin();
        }

        if (Locked(user) is { } locked)
            return locked;

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            await RecordFailureAsync(connection, user.Id);
            return WrongLogin();
        }

        await connection.ExecuteAsync(RecordSuccessSql, new { user.Id });
        var (token, expires) = tokens.Create(user.Id, user.Username, user.TokenVersion);
        return Results.Ok(new AuthResponse(user.Id, user.Username, token, expires));
    }

    static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, ClaimsPrincipal principal, Db db, TokenService tokens, CancellationToken ct)
    {
        string current = request.CurrentPassword ?? "";
        string replacement = request.NewPassword ?? "";
        if (PasswordProblem(replacement) is { } problem)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = new[] { problem } });

        await using var connection = await db.OpenAsync(ct);
        var user = await connection.QueryFirstOrDefaultAsync<UserRow>(FindUserByIdSql, new { Id = principal.UserId() });
        if (user == null)
            return Results.Unauthorized();
        if (Locked(user) is { } locked)
            return locked;

        if (current.Length == 0 || current.Length > MaxPasswordLength || !PasswordHasher.Verify(current, user.PasswordHash))
        {
            await RecordFailureAsync(connection, user.Id);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Your current password is wrong.");
        }

        int version = await connection.ExecuteScalarAsync<int>("""
            UPDATE dbo.app_user
            SET password_hash = @PasswordHash, token_version = token_version + 1, failed_logins = 0, locked_until = NULL
            OUTPUT INSERTED.token_version
            WHERE id = @Id
            """, new { user.Id, PasswordHash = PasswordHasher.Hash(replacement) });

        var (token, expires) = tokens.Create(user.Id, user.Username, version);
        return Results.Ok(new AuthResponse(user.Id, user.Username, token, expires));
    }

    static async Task<IResult> LogoutEverywhereAsync(ClaimsPrincipal principal, Db db, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(
            "UPDATE dbo.app_user SET token_version = token_version + 1 WHERE id = @Id",
            new { Id = principal.UserId() });
        return Results.NoContent();
    }

    static string? PasswordProblem(string password)
    {
        if (password.Length < MinPasswordLength)
            return $"Use at least {MinPasswordLength} characters.";
        if (password.Length > MaxPasswordLength)
            return $"Use at most {MaxPasswordLength} characters.";
        return null;
    }

    static IResult? Locked(UserRow user)
    {
        if (user.LockedUntil is not DateTime until)
            return null;

        var remaining = DateTime.SpecifyKind(until, DateTimeKind.Utc) - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return null;

        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: $"Too many wrong passwords. Try again in {minutes} minute(s).");
    }

    static Task RecordFailureAsync(System.Data.IDbConnection connection, int userId) =>
        connection.ExecuteAsync(RecordFailureSql, new
        {
            Id = userId,
            Limit = AuthLimits.FailuresBeforeLock,
            LockSeconds = (int)AuthLimits.LockFor.TotalSeconds
        });

    static IResult WrongLogin() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Wrong username, email or password.");

    sealed class UserRow
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public int FailedLogins { get; set; }
        public DateTime? LockedUntil { get; set; }
        public int TokenVersion { get; set; }
    }
}

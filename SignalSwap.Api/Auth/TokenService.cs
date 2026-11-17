using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SignalSwap.Api.Auth;

public sealed class TokenService(JwtSettings settings)
{
    public const string UserIdClaim = "sub";
    public const string UsernameClaim = "unique_name";
    public const string VersionClaim = "ver";

    readonly JsonWebTokenHandler handler = new();

    public (string Token, DateTimeOffset ExpiresAt) Create(int userId, string username, int tokenVersion)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(settings.TokenDays);
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(UserIdClaim, userId.ToString()),
                new Claim(UsernameClaim, username),
                new Claim(VersionClaim, tokenVersion.ToString())
            }),
            SigningCredentials = new SigningCredentials(settings.SigningKey, SecurityAlgorithms.HmacSha256)
        });
        return (token, expires);
    }
}

public static class UserExtensions
{
    public static int UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst(TokenService.UserIdClaim)?.Value, out int id)
            ? id
            : throw new UnauthorizedAccessException("The token has no user id.");

    public static int TokenVersion(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst(TokenService.VersionClaim)?.Value, out int version) ? version : -1;
}

using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SignalSwap.Api.Auth;

public sealed class JwtSettings
{
    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "SignalSwap";
    public string Audience { get; set; } = "SignalSwap.App";
    public int TokenDays { get; set; } = 30;

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));

    public void Validate()
    {
        if (Encoding.UTF8.GetByteCount(Key) < 32)
            throw new InvalidOperationException(
                "Jwt:Key is missing or shorter than 32 characters. On the server, Server\\install-service.ps1 sets it for the service. Anywhere else, set the Jwt__Key environment variable.");
        if (TokenDays < 1)
            throw new InvalidOperationException("Jwt:TokenDays must be at least 1.");
    }
}

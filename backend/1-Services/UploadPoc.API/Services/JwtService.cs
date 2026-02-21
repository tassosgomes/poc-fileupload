using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace UploadPoc.API.Services;

public sealed class JwtService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly string _audience;

    public JwtService(string secret, string issuer, string audience, int expirationHours)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("JWT secret cannot be empty.", nameof(secret));
        }

        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new ArgumentException("JWT issuer cannot be empty.", nameof(issuer));
        }

        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new ArgumentException("JWT audience cannot be empty.", nameof(audience));
        }

        if (secret.Length < 32)
        {
            throw new ArgumentException("JWT secret must have at least 32 characters.", nameof(secret));
        }

        if (expirationHours <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expirationHours), "JWT expiration hours must be greater than zero.");
        }

        _secret = secret;
        _issuer = issuer;
        _audience = audience;
        ExpirationHours = expirationHours;
    }

    public int ExpirationHours { get; }

    public string GenerateToken(string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddHours(ExpirationHours);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var tokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            ValidIssuer = _issuer,
            ValidAudience = _audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.Zero
        };

        try
        {
            var principal = new JwtSecurityTokenHandler()
                .ValidateToken(token, tokenValidationParameters, out _);

            return principal;
        }
        catch
        {
            return null;
        }
    }
}

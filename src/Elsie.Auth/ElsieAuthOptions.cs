using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Elsie.Auth;

public sealed class ElsieAuthOptions
{
    /// <summary>Cookie authentication configuration. Null disables cookies.</summary>
    public ElsieCookieAuthOptions? Cookie { get; set; }

    /// <summary>JWT bearer configuration. Null disables JWT.</summary>
    public ElsieJwtBearerOptions? JwtBearer { get; set; }

    /// <summary>When both cookie and JWT are enabled, cookie is tried first unless this is set.</summary>
    public string? DefaultScheme { get; set; }
}

public sealed class ElsieCookieAuthOptions
{
    public string Scheme { get; set; } = "Cookies";
    public string CookieName { get; set; } = "elsie-auth";
    public string? CookiePath { get; set; } = "/";
    public string? CookieDomain { get; set; }
    public bool HttpOnly { get; set; } = true;
    public bool Secure { get; set; }
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;
    public TimeSpan ExpireTimeSpan { get; set; } = TimeSpan.FromHours(8);
    public bool SlidingExpiration { get; set; } = true;

    /// <summary>
    /// 32-byte secret used to encrypt/sign tickets. Required for cookie auth.
    /// Prefer a stable app secret (config/env).
    /// </summary>
    public byte[]? TicketKey { get; set; }

    /// <summary>
    /// When true and <see cref="TicketKey"/> is unset, a well-known development key is used.
    /// Never enable this in production.
    /// </summary>
    public bool AllowInsecureDevelopmentKey { get; set; }

    /// <summary>
    /// Set ticket key from a UTF-8 secret (hashed to 32 bytes via SHA-256).
    /// Secret must be at least 16 characters.
    /// </summary>
    public void TicketKeyFromString(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (secret.Length < 16)
        {
            throw new ArgumentException(
                "Ticket secret must be at least 16 characters. Use a long random string or env-based secret.",
                nameof(secret));
        }

        TicketKey = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }
}

public sealed class ElsieJwtBearerOptions
{
    public string Scheme { get; set; } = "Bearer";
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public string? Issuer { get; set; }
    public SecurityKey? SigningKey { get; set; }
    public bool ValidateIssuer { get; set; } = true;
    public bool ValidateAudience { get; set; } = true;
    public bool ValidateLifetime { get; set; } = true;
    public bool ValidateIssuerSigningKey { get; set; } = true;

    public TokenValidationParameters CreateValidationParameters()
    {
        var p = new TokenValidationParameters
        {
            ValidateIssuer = ValidateIssuer,
            ValidateAudience = ValidateAudience,
            ValidateLifetime = ValidateLifetime,
            ValidateIssuerSigningKey = ValidateIssuerSigningKey,
            ValidIssuer = Issuer ?? Authority,
            ValidAudience = Audience,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        if (SigningKey is not null)
        {
            p.IssuerSigningKey = SigningKey;
        }

        return p;
    }
}

public enum SameSiteMode
{
    None = 0,
    Lax = 1,
    Strict = 2
}

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

    /// <summary>
    /// Optional server-side session store. When set, cookies become opaque v2 session ids
    /// (≥128-bit) and the principal is read from the store with sliding renewal.
    /// When null (default), the client-side encrypted ticket (v1) is used.
    /// </summary>
    public IElsieSessionStore? SessionStore { get; set; }

    /// <summary>Named authorization policies usable with <see cref="ElsieAuthGates.RequirePolicy(string)"/>.</summary>
    public Dictionary<string, ElsieAuthorizationPolicy> Policies { get; } = new(StringComparer.Ordinal);

    /// <summary>Most recently configured options instance (set by AddElsieAuth) — used for eager policy-name validation at gate creation time.</summary>
    internal static ElsieAuthOptions? LastConfigured { get; private set; }

    internal void MarkConfigured() => LastConfigured = this;

    /// <summary>Cookie 302 redirect target returned by <see cref="ElsieAuthResultExtensions.Challenge(ElsieContext)"/> when cookie auth is configured.</summary>
    public string? ChallengeLoginPath { get; set; }

    /// <summary>Cookie 302 redirect target returned by <see cref="ElsieAuthResultExtensions.Forbid(ElsieContext)"/> when cookie auth is configured.</summary>
    public string? ForbidAccessDeniedPath { get; set; }
}

public sealed class ElsieCookieAuthOptions
{
    public string Scheme { get; set; } = "Cookies";
    public string CookieName { get; set; } = "elsie-auth";
    public string? CookiePath { get; set; } = "/";
    public string? CookieDomain { get; set; }
    public bool HttpOnly { get; set; } = true;

    /// <summary>
    /// Emit <c>Secure</c> on the cookie. Defaults to <b>true</b> (strict). Set to false
    /// explicitly for plain-HTTP local development only.
    /// </summary>
    public bool Secure { get; set; } = true;

    public ElsieSameSite SameSite { get; set; } = ElsieSameSite.Lax;

    /// <summary>
    /// Session/ticket lifetime; also drives sliding expiration. Defaults to 8 hours.
    /// </summary>
    public TimeSpan ExpireTimeSpan { get; set; } = TimeSpan.FromHours(8);

    /// <summary>
    /// <c>Max-Age</c> emitted in <c>Set-Cookie</c>. Defaults to 8 hours.
    /// <see cref="ExpireTimeSpan"/> still drives the actual ticket/session lifetime.
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(8);

    public bool SlidingExpiration { get; set; } = true;

    /// <summary>
    /// Required cookie-name prefix, e.g. <c>__Host-</c>. When set, cookie setup validates
    /// that <see cref="CookieName"/> starts with the prefix; <c>__Host-</c> additionally
    /// requires <see cref="Secure"/> and <c>Path=/</c> and forbids <see cref="CookieDomain"/>.
    /// </summary>
    public string? CookiePrefix { get; set; }

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

    /// <summary>
    /// OIDC authority used for JWKS discovery (<c>/.well-known/openid-configuration</c>)
    /// when <see cref="SigningKey"/> is unset. May be combined with <see cref="JwksUrl"/>.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>Explicit JWKS URL (e.g. <c>https://issuer/.well-known/jwks.json</c>).</summary>
    public string? JwksUrl { get; set; }

    /// <summary>
    /// Allow plain-HTTP OIDC metadata/JWKS endpoints. Defaults to false (strict HTTPS).
    /// Enable only for local development and tests.
    /// </summary>
    public bool AllowHttpMetadata { get; set; }

    public string? Audience { get; set; }
    public string? Issuer { get; set; }
    public SecurityKey? SigningKey { get; set; }
    public bool ValidateIssuer { get; set; } = true;
    public bool ValidateAudience { get; set; } = true;
    public bool ValidateLifetime { get; set; } = true;
    public bool ValidateIssuerSigningKey { get; set; } = true;

    /// <summary>
    /// How often the JWKS metadata is re-fetched from the authority. Defaults to 24 hours.
    /// Keys from previous sets are kept during rollover so old tokens keep validating.
    /// </summary>
    public TimeSpan JwksRefreshInterval { get; set; } = TimeSpan.FromHours(24);

    public TokenValidationParameters CreateValidationParameters(IEnumerable<SecurityKey>? signingKeys = null)
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

        if (signingKeys is not null)
        {
            p.IssuerSigningKeys = signingKeys.ToArray();
        }
        else if (SigningKey is not null)
        {
            p.IssuerSigningKey = SigningKey;
        }

        return p;
    }
}

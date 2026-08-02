using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Elsie.Auth;

internal static class JwtTokenValidator
{
    private static readonly JwtSecurityTokenHandler Handler = new()
    {
        MapInboundClaims = false
    };

    /// <summary>
    /// Synchronous validation against a statically configured <see cref="ElsieJwtBearerOptions.SigningKey"/>.
    /// When only an authority/JWKS URL is configured this returns false (no network calls in the sync path) —
    /// request-time validation must use <see cref="TryValidateAsync"/>.
    /// </summary>
    public static bool TryValidate(string token, ElsieJwtBearerOptions options, out ClaimsPrincipal? principal)
    {
        principal = null;
        if (options.SigningKey is null)
        {
            return false;
        }

        try
        {
            principal = Handler.ValidateToken(token, options.CreateValidationParameters(), out _);
            return principal.Identity?.IsAuthenticated == true;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates a bearer token. Uses the static <see cref="ElsieJwtBearerOptions.SigningKey"/> when set;
    /// otherwise resolves signing keys through <paramref name="jwks"/> (JWKS discovery). Never throws —
    /// a failed lookup or unreachable authority yields null (→ 401).
    /// </summary>
    public static async Task<ClaimsPrincipal?> TryValidateAsync(
        string token,
        ElsieJwtBearerOptions options,
        JwksResolver? jwks,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options.SigningKey is not null)
            {
                var principal = Handler.ValidateToken(token, options.CreateValidationParameters(), out _);
                return principal.Identity?.IsAuthenticated == true ? principal : null;
            }

            if (jwks is null)
            {
                return null;
            }

            var keys = await jwks.GetSigningKeysAsync(cancellationToken).ConfigureAwait(false);
            if (keys.Count == 0)
            {
                return null;
            }

            var resolved = Handler.ValidateToken(token, options.CreateValidationParameters(keys), out _);
            return resolved.Identity?.IsAuthenticated == true ? resolved : null;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

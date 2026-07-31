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

    public static bool TryValidate(string token, ElsieJwtBearerOptions options, out ClaimsPrincipal? principal)
    {
        principal = null;
        try
        {
            var parameters = options.CreateValidationParameters();
            if (parameters.IssuerSigningKey is null && string.IsNullOrEmpty(options.Authority))
            {
                // Without a key or authority we cannot validate signatures.
                return false;
            }

            if (parameters.IssuerSigningKey is null)
            {
                // Authority-only (OIDC metadata) not implemented in v1 — require SigningKey.
                return false;
            }

            principal = Handler.ValidateToken(token, parameters, out _);
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
}

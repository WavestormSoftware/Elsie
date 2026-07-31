using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Elsie.Auth;

/// <summary>Minimal OAuth2/OIDC authorization-code helpers (no ASP.NET handler).</summary>
public sealed class ElsieOidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string RedirectUri { get; set; } = "";
    public string Scope { get; set; } = "openid profile email";
    public string? Audience { get; set; }

    /// <summary>Optional path under authority for authorize (default /authorize).</summary>
    public string AuthorizePath { get; set; } = "/authorize";

    /// <summary>Optional token endpoint path (default /oauth/token; discovery later).</summary>
    public string TokenPath { get; set; } = "/oauth/token";
}

public static class ElsieOidc
{
    /// <summary>Cryptographic random state (Base64Url, URL-safe).</summary>
    public static string CreateState() => Base64UrlEncode(RandomNumberGenerator.GetBytes(24));

    /// <summary>Cryptographic random nonce (Base64Url, URL-safe).</summary>
    public static string CreateNonce() => Base64UrlEncode(RandomNumberGenerator.GetBytes(24));

    /// <summary>PKCE code_verifier (43–128 chars, unreserved).</summary>
    public static string CreateCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>PKCE S256 code_challenge from <paramref name="codeVerifier"/>.</summary>
    public static string CreateCodeChallenge(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>Build the browser redirect URL for the authorization code flow.</summary>
    public static string BuildAuthorizeUrl(
        ElsieOidcOptions options,
        string state,
        string? nonce = null,
        string? codeChallenge = null,
        string codeChallengeMethod = "S256",
        string responseType = "code")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RedirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var authority = options.Authority.TrimEnd('/');
        var path = options.AuthorizePath.StartsWith('/') ? options.AuthorizePath : "/" + options.AuthorizePath;
        var qs = new StringBuilder();
        qs.Append("response_type=").Append(Uri.EscapeDataString(responseType));
        qs.Append("&client_id=").Append(Uri.EscapeDataString(options.ClientId));
        qs.Append("&redirect_uri=").Append(Uri.EscapeDataString(options.RedirectUri));
        qs.Append("&scope=").Append(Uri.EscapeDataString(options.Scope));
        qs.Append("&state=").Append(Uri.EscapeDataString(state));
        if (!string.IsNullOrEmpty(nonce))
        {
            qs.Append("&nonce=").Append(Uri.EscapeDataString(nonce));
        }

        if (!string.IsNullOrEmpty(codeChallenge))
        {
            qs.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
            qs.Append("&code_challenge_method=").Append(Uri.EscapeDataString(codeChallengeMethod));
        }

        return $"{authority}{path}?{qs}";
    }

    /// <summary>Exchange authorization code for tokens.</summary>
    public static async Task<ElsieOidcTokenResponse> ExchangeCodeAsync(
        HttpClient http,
        ElsieOidcOptions options,
        string code,
        string? codeVerifier = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var authority = options.Authority.TrimEnd('/');
        var path = options.TokenPath.StartsWith('/') ? options.TokenPath : "/" + options.TokenPath;
        var url = authority + path;

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = options.RedirectUri,
            ["client_id"] = options.ClientId
        };
        if (!string.IsNullOrEmpty(options.ClientSecret))
        {
            form["client_secret"] = options.ClientSecret!;
        }

        if (!string.IsNullOrEmpty(codeVerifier))
        {
            form["code_verifier"] = codeVerifier;
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OIDC token endpoint failed ({(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new ElsieOidcTokenResponse
        {
            AccessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null,
            IdToken = root.TryGetProperty("id_token", out var id) ? id.GetString() : null,
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : null,
            ExpiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var secs) ? secs : null,
            RawJson = body
        };
    }

    /// <summary>
    /// Build a principal from an id_token. Signature validation runs when <paramref name="jwt"/> has a signing key.
    /// Unvalidated payload parse requires <paramref name="allowUnvalidated"/> = true (dev only).
    /// When <paramref name="expectedNonce"/> is set, the id_token <c>nonce</c> claim must match.
    /// </summary>
    public static ClaimsPrincipal? PrincipalFromIdToken(
        string? idToken,
        ElsieJwtBearerOptions? jwt,
        bool allowUnvalidated = false,
        string? expectedNonce = null)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        if (jwt is not null && JwtTokenValidator.TryValidate(idToken, jwt, out var principal) && principal is not null)
        {
            return NonceMatches(principal, expectedNonce) ? principal : null;
        }

        if (!allowUnvalidated)
        {
            return null;
        }

        // Unvalidated parse of payload for non-prod fallback only
        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(json);
            var claims = new List<Claim>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    claims.Add(new Claim(prop.Name, prop.Value.GetString() ?? ""));
                }
            }

            if (claims.Count == 0)
            {
                return null;
            }

            var identity = new ClaimsIdentity(claims, authenticationType: "oidc");
            var unvalidated = new ClaimsPrincipal(identity);
            return NonceMatches(unvalidated, expectedNonce) ? unvalidated : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool NonceMatches(ClaimsPrincipal principal, string? expectedNonce)
    {
        if (string.IsNullOrEmpty(expectedNonce))
        {
            return true;
        }

        var actual = principal.FindFirst("nonce")?.Value;
        return string.Equals(actual, expectedNonce, StringComparison.Ordinal);
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}

public sealed class ElsieOidcTokenResponse
{
    public string? AccessToken { get; init; }
    public string? IdToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? TokenType { get; init; }
    public int? ExpiresIn { get; init; }
    public string? RawJson { get; init; }
}

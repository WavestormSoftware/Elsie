using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Elsie.Auth.Tests;

public class OidcHelperTests
{
    [Fact]
    public void CreateState_and_nonce_are_url_safe()
    {
        var state = ElsieOidc.CreateState();
        var nonce = ElsieOidc.CreateNonce();
        Assert.DoesNotContain('+', state);
        Assert.DoesNotContain('/', state);
        Assert.DoesNotContain('=', state);
        Assert.DoesNotContain('+', nonce);
        Assert.DoesNotContain('/', nonce);
        Assert.DoesNotContain('=', nonce);
        Assert.True(state.Length >= 32);
        Assert.True(nonce.Length >= 32);
    }

    [Fact]
    public void Pkce_challenge_is_s256_base64url()
    {
        var verifier = ElsieOidc.CreateCodeVerifier();
        var challenge = ElsieOidc.CreateCodeChallenge(verifier);
        var expected = ElsieOidc.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, challenge);
        Assert.DoesNotContain('+', challenge);
        Assert.DoesNotContain('/', challenge);
        Assert.DoesNotContain('=', challenge);
    }

    [Fact]
    public void BuildAuthorizeUrl_includes_pkce_and_nonce()
    {
        var options = new ElsieOidcOptions
        {
            Authority = "https://idp.example",
            ClientId = "app",
            RedirectUri = "https://app.example/callback",
            Scope = "openid profile"
        };

        var url = ElsieOidc.BuildAuthorizeUrl(
            options,
            state: "st",
            nonce: "n1",
            codeChallenge: "ch");

        Assert.StartsWith("https://idp.example/authorize?", url, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url, StringComparison.Ordinal);
        Assert.Contains("client_id=app", url, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(options.RedirectUri), url, StringComparison.Ordinal);
        Assert.Contains("scope=" + Uri.EscapeDataString("openid profile"), url, StringComparison.Ordinal);
        Assert.Contains("state=st", url, StringComparison.Ordinal);
        Assert.Contains("nonce=n1", url, StringComparison.Ordinal);
        Assert.Contains("code_challenge=ch", url, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeCodeAsync_posts_code_and_verifier()
    {
        var handler = new RecordingHandler
        {
            Response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"at","id_token":"idt","token_type":"Bearer","expires_in":3600}""",
                    Encoding.UTF8,
                    "application/json")
            }
        };
        using var http = new HttpClient(handler);
        var options = new ElsieOidcOptions
        {
            Authority = "https://idp.example/",
            ClientId = "app",
            ClientSecret = "sec",
            RedirectUri = "https://app.example/cb",
            TokenPath = "/oauth/token"
        };

        var tokens = await ElsieOidc.ExchangeCodeAsync(http, options, code: "abc", codeVerifier: "ver");

        Assert.Equal("at", tokens.AccessToken);
        Assert.Equal("idt", tokens.IdToken);
        Assert.Equal(3600, tokens.ExpiresIn);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://idp.example/oauth/token", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(
            "grant_type=authorization_code&code=abc&redirect_uri=https%3A%2F%2Fapp.example%2Fcb&client_id=app&client_secret=sec&code_verifier=ver",
            handler.LastBody);
    }

    [Fact]
    public void PrincipalFromIdToken_requires_validation_by_default()
    {
        var token = BuildUnsignedPayloadJwt(new Dictionary<string, object>
        {
            ["sub"] = "user-1",
            ["name"] = "Ada"
        });

        Assert.Null(ElsieOidc.PrincipalFromIdToken(token, jwt: null));
        Assert.Null(ElsieOidc.PrincipalFromIdToken(token, jwt: null, allowUnvalidated: false));

        var principal = ElsieOidc.PrincipalFromIdToken(token, jwt: null, allowUnvalidated: true);
        Assert.NotNull(principal);
        Assert.Equal("user-1", principal!.FindFirst("sub")?.Value);
    }

    [Fact]
    public void PrincipalFromIdToken_validates_signature_and_nonce()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };

        var jwt = handler.CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = "https://idp.example",
            Audience = "api",
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "user-1"),
                new Claim("nonce", "expected-nonce")
            ]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = creds
        });

        var options = new ElsieJwtBearerOptions
        {
            Issuer = "https://idp.example",
            Audience = "api",
            SigningKey = key
        };

        var ok = ElsieOidc.PrincipalFromIdToken(jwt, options, expectedNonce: "expected-nonce");
        Assert.NotNull(ok);
        Assert.Equal("user-1", ok!.FindFirst("sub")?.Value);

        Assert.Null(ElsieOidc.PrincipalFromIdToken(jwt, options, expectedNonce: "wrong"));
    }

    private static string BuildUnsignedPayloadJwt(IReadOnlyDictionary<string, object> payload)
    {
        static string B64(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = B64("""{"alg":"none","typ":"JWT"}""");
        var body = B64(JsonSerializer.Serialize(payload));
        return $"{header}.{body}.sig";
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return Response;
        }
    }
}

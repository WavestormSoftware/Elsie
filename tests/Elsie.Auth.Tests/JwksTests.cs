using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Elsie.Auth;
using Elsie.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Elsie.Auth.Tests;

public class JwksTests
{
    private sealed class JwtSecureModule : ElsieModule
    {
        public JwtSecureModule()
        {
            Before(ElsieAuthGates.RequireAuthenticated());
            Get("/jwt-secure", ctx => ctx.Json(new { sub = ctx.GetUser().FindFirst("sub")?.Value }));
        }
    }

    [Fact]
    public async Task Token_signed_by_discovered_key_validates()
    {
        using var rsa = RSA.Create(2048);
        await using var idp = new FakeIdpServer();
        idp.Map("/.well-known/openid-configuration", OidcMetadata(idp.BaseUrl));
        idp.Map("/jwks", BuildJwks(("key-1", rsa)));

        var options = new ElsieJwtBearerOptions
        {
            Authority = idp.BaseUrl,
            Audience = "api",
            AllowHttpMetadata = true
        };
        var resolver = JwksResolver.TryCreate(options);
        Assert.NotNull(resolver);

        var token = SignToken(rsa, "key-1", idp.BaseUrl, "api");
        var principal = await JwtTokenValidator.TryValidateAsync(token, options, resolver, CancellationToken.None);
        Assert.NotNull(principal);
        Assert.Equal("user-1", principal!.FindFirst("sub")?.Value);
    }

    [Fact]
    public async Task Rotated_keys_keep_previous_keys_valid()
    {
        using var rsa1 = RSA.Create(2048);
        using var rsa2 = RSA.Create(2048);
        await using var idp = new FakeIdpServer();
        idp.Map("/.well-known/openid-configuration", OidcMetadata(idp.BaseUrl));
        idp.Map("/jwks", BuildJwks(("key-1", rsa1)));

        var options = new ElsieJwtBearerOptions
        {
            Authority = idp.BaseUrl,
            Audience = "api",
            AllowHttpMetadata = true,
            JwksRefreshInterval = TimeSpan.FromHours(24)
        };
        var resolver = JwksResolver.TryCreate(options)!;
        var token1 = SignToken(rsa1, "key-1", idp.BaseUrl, "api");
        Assert.NotNull(await JwtTokenValidator.TryValidateAsync(token1, options, resolver, CancellationToken.None));
        Assert.Equal(1, resolver.KeyCount);

        // Rotate: the authority now serves key-2 only.
        idp.Map("/jwks", BuildJwks(("key-2", rsa2)));
        resolver.RequestRefresh();

        // The refresh completes in the background; poll until the new key is visible.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (resolver.KeyCount < 2 && DateTime.UtcNow < deadline)
        {
            await resolver.GetSigningKeysAsync(CancellationToken.None);
            await Task.Delay(100);
        }

        var keys = await resolver.GetSigningKeysAsync(CancellationToken.None);

        // Rollover keeps the previous key, so old tokens still validate.
        Assert.Equal(2, resolver.KeyCount);
        Assert.Equal(2, keys.Count);
        Assert.NotNull(await JwtTokenValidator.TryValidateAsync(token1, options, resolver, CancellationToken.None));

        var token2 = SignToken(rsa2, "key-2", idp.BaseUrl, "api");
        Assert.NotNull(await JwtTokenValidator.TryValidateAsync(token2, options, resolver, CancellationToken.None));

        // A token signed with an unknown key (unknown kid) is rejected.
        using var rsa3 = RSA.Create(2048);
        var token3 = SignToken(rsa3, "key-3", idp.BaseUrl, "api");
        Assert.Null(await JwtTokenValidator.TryValidateAsync(token3, options, resolver, CancellationToken.None));
    }

    [Fact]
    public async Task Unreachable_authority_never_crashes_and_fails_validation()
    {
        var options = new ElsieJwtBearerOptions
        {
            Authority = "http://127.0.0.1:1",
            Audience = "api",
            AllowHttpMetadata = true
        };
        var resolver = JwksResolver.TryCreate(options)!;

        using var rsa = RSA.Create(2048);
        var token = SignToken(rsa, "key-1", options.Authority!, "api");

        // No keys could ever be resolved → validation fails cleanly (401 path), no throw.
        var principal = await JwtTokenValidator.TryValidateAsync(token, options, resolver, CancellationToken.None);
        Assert.Null(principal);
    }

    [Fact]
    public async Task Unreachable_authority_end_to_end_returns_401_not_500()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o => o.JwtBearer = new ElsieJwtBearerOptions
            {
                Authority = "http://127.0.0.1:1",
                Audience = "api",
                AllowHttpMetadata = true
            });
            s.AddElsieModule<JwtSecureModule>();
        });

        using var rsa = RSA.Create(2048);
        var token = SignToken(rsa, "key-1", "http://127.0.0.1:1", "api");
        using var req = new HttpRequestMessage(HttpMethod.Get, "/jwt-secure");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Bearer_token_attaches_principal_via_jwks()
    {
        using var rsa = RSA.Create(2048);
        await using var idp = new FakeIdpServer();
        idp.Map("/.well-known/openid-configuration", OidcMetadata(idp.BaseUrl));
        idp.Map("/jwks", BuildJwks(("key-1", rsa)));

        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o => o.JwtBearer = new ElsieJwtBearerOptions
            {
                Authority = idp.BaseUrl,
                Audience = "api",
                AllowHttpMetadata = true
            });
            s.AddElsieModule<JwtSecureModule>();
        });

        var token = SignToken(rsa, "key-1", idp.BaseUrl, "api");
        using var req = new HttpRequestMessage(HttpMethod.Get, "/jwt-secure");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await host.Client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("user-1", doc.RootElement.GetProperty("sub").GetString());
    }

    [Fact]
    public async Task Anonymous_jwt_request_challenges_with_401_and_bearer_header()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o => o.JwtBearer = new ElsieJwtBearerOptions
            {
                Authority = "https://idp.example",
                Audience = "api"
            });
            s.AddElsieModule<JwtSecureModule>();
        });

        var res = await host.GetAsync("/jwt-secure");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var www = Assert.Single(res.Headers.GetValues("WWW-Authenticate"));
        Assert.Equal("Bearer", www);
    }

    private static string OidcMetadata(string issuer) =>
        $$"""{"issuer":"{{issuer}}","jwks_uri":"{{issuer}}/jwks"}""";

    private static string BuildJwks(params (string Kid, RSA Rsa)[] keys)
    {
        var keyObjects = keys.Select(k =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(new RsaSecurityKey(k.Rsa) { KeyId = k.Kid });
            return new { kty = jwk.Kty, kid = jwk.Kid, use = jwk.Use, alg = jwk.Alg, n = jwk.N, e = jwk.E };
        }).ToArray();
        return JsonSerializer.Serialize(new { keys = keyObjects });
    }

    private static string SignToken(RSA rsa, string kid, string issuer, string audience)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        return handler.CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("name", "Ada")]),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(rsa) { KeyId = kid },
                SecurityAlgorithms.RsaSha256)
        });
    }
}

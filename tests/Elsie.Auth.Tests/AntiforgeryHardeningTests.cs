using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Auth.Tests;

public class AntiforgeryHardeningTests
{
    private static (IServiceProvider Services, ElsieAntiforgeryService Service) Build(
        Action<ElsieAntiforgeryOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddElsieAntiforgery(o =>
        {
            o.SigningKey = new byte[32];
            configure?.Invoke(o);
        });
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<ElsieAntiforgeryService>());
    }

    private static ElsieContext Context(IServiceProvider sp, string? userName = null, ElsieResponse? response = null)
    {
        var request = new ElsieRequest("POST", "/x", requestServices: sp);
        if (userName is not null)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], "Test");
            ElsiePrincipal.SetUser(request, new ClaimsPrincipal(identity));
        }

        return new ElsieContext(request, response ?? new ElsieResponse(), new Dictionary<string, string>());
    }

    [Fact]
    public void Token_is_minted_once_per_request()
    {
        var (sp, service) = Build();
        var response = new ElsieResponse();
        var ctx = Context(sp, response: response);

        var first = service.GetAndStoreToken(ctx);
        var second = service.GetAndStoreToken(ctx);

        Assert.Equal(first, second);
        Assert.Single(response.SetCookies);
    }

    [Fact]
    public void Cookie_is_secure_by_default()
    {
        var (sp, service) = Build();
        var response = new ElsieResponse();
        _ = service.GetAndStoreToken(Context(sp, response: response));

        Assert.Contains("Secure", response.SetCookies[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Secure_can_be_disabled_explicitly()
    {
        var (sp, service) = Build(o => o.Secure = false);
        var response = new ElsieResponse();
        _ = service.GetAndStoreToken(Context(sp, response: response));

        Assert.DoesNotContain("Secure", response.SetCookies[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Token_is_bound_to_the_principal()
    {
        var (sp, service) = Build();
        var token = service.GetAndStoreToken(Context(sp, userName: "ada"));

        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = $"elsie-csrf={Uri.EscapeDataString(token)}",
            ["X-CSRF-TOKEN"] = token
        };

        var sameUser = new ElsieContext(
            AuthenticatedRequest(sp, "ada", headers),
            new ElsieResponse(),
            new Dictionary<string, string>());
        var otherUser = new ElsieContext(
            AuthenticatedRequest(sp, "mallory", headers),
            new ElsieResponse(),
            new Dictionary<string, string>());

        Assert.True(service.IsValid(sameUser));
        Assert.False(service.IsValid(otherUser));
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var (sp, service) = Build(o => o.TokenLifetime = TimeSpan.Zero);
        var token = service.GetAndStoreToken(Context(sp, userName: "ada"));

        var headers = new Dictionary<string, string>
        {
            ["Cookie"] = $"elsie-csrf={Uri.EscapeDataString(token)}",
            ["X-CSRF-TOKEN"] = token
        };
        var ctx = new ElsieContext(
            AuthenticatedRequest(sp, "ada", headers),
            new ElsieResponse(),
            new Dictionary<string, string>());

        Assert.False(service.IsValid(ctx));
    }

    private static ElsieRequest AuthenticatedRequest(
        IServiceProvider services,
        string userName,
        IReadOnlyDictionary<string, string> headers)
    {
        var request = new ElsieRequest("POST", "/x", headers: headers, requestServices: services);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], "Test");
        ElsiePrincipal.SetUser(request, new ClaimsPrincipal(identity));
        return request;
    }
}

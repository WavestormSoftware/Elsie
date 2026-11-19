using Elsie.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class SecurityHeadersTests
{
    [Fact]
    public void Apply_sets_baseline_headers()
    {
        var result = ElsieSecurityHeaders.Apply(ElsieResult.Text("ok"), new ElsieSecurityHeadersOptions(), isHttps: false);

        Assert.Equal("nosniff", result.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", result.Headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", result.Headers["Referrer-Policy"]);
        Assert.Equal("same-origin", result.Headers["Cross-Origin-Opener-Policy"]);
        Assert.Equal("same-origin", result.Headers["Cross-Origin-Resource-Policy"]);
        Assert.Contains("geolocation=()", result.Headers["Permissions-Policy"], StringComparison.Ordinal);
    }

    [Fact]
    public void Hsts_only_on_https()
    {
        var options = new ElsieSecurityHeadersOptions();

        var http = ElsieSecurityHeaders.Apply(ElsieResult.Text("ok"), options, isHttps: false);
        Assert.Empty(http.Headers.GetValues("Strict-Transport-Security"));

        var https = ElsieSecurityHeaders.Apply(ElsieResult.Text("ok"), options, isHttps: true);
        Assert.Equal("max-age=31536000; includeSubDomains", https.Headers["Strict-Transport-Security"]);

        var preload = ElsieSecurityHeaders.Apply(
            ElsieResult.Text("ok"),
            new ElsieSecurityHeadersOptions { HstsPreload = true },
            isHttps: true);
        Assert.EndsWith("preload", preload.Headers["Strict-Transport-Security"], StringComparison.Ordinal);
    }

    [Fact]
    public void Options_can_disable_headers()
    {
        var options = new ElsieSecurityHeadersOptions
        {
            NoSniff = false,
            FrameOptions = null,
            ReferrerPolicy = null,
            PermissionsPolicy = null,
            CrossOriginOpenerPolicy = null,
            CrossOriginResourcePolicy = null,
            StrictTransportSecurity = null
        };

        var result = ElsieSecurityHeaders.Apply(ElsieResult.Text("ok"), options, isHttps: true);

        Assert.Empty(result.Headers.GetValues("X-Content-Type-Options"));
        Assert.Empty(result.Headers.GetValues("X-Frame-Options"));
        Assert.Empty(result.Headers.GetValues("Strict-Transport-Security"));
    }

    [Fact]
    public void Csp_is_emitted_when_configured()
    {
        var options = new ElsieSecurityHeadersOptions { ContentSecurityPolicy = "default-src 'self'" };
        var result = ElsieSecurityHeaders.Apply(ElsieResult.Text("ok"), options, isHttps: false);

        Assert.Equal("default-src 'self'", result.Headers["Content-Security-Policy"]);
    }

    [Fact]
    public async Task Middleware_applies_headers_to_dispatched_responses()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieSecurityHeaders(o => o.ContentSecurityPolicy = "default-src 'none'");
        services.AddElsieEndpoints(app => app.MapGet("/page", () => "body"), o => o.ScanEntryAssembly = false);
        await using var sp = services.BuildServiceProvider();

        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        var outcome = await dispatcher.DispatchAsync(new ElsieRequest(
            "GET",
            "/page",
            scheme: "https",
            requestServices: sp));
        var response = ElsieHttpResponse.FromDispatch(outcome)!;

        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("default-src 'none'", response.Headers["Content-Security-Policy"]);
        Assert.Equal("max-age=31536000; includeSubDomains", response.Headers["Strict-Transport-Security"]);
    }
}

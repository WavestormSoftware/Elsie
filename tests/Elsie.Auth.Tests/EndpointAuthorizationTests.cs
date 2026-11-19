using System.Security.Claims;
using Elsie.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Auth.Tests;

public class EndpointAuthorizationTests
{
    private static ServiceProvider Build(Action<IElsieEndpointRouteBuilder> map)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ElsieAuthOptions());
        services.AddElsieEndpointAuthorization();
        services.AddElsieEndpoints(map, o => o.ScanEntryAssembly = false);
        return services.BuildServiceProvider();
    }

    private static ElsieRequest Request(string path, bool authenticated, IServiceProvider services)
    {
        var request = new ElsieRequest("GET", path, requestServices: services);
        var identity = authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "ada")], "Test")
            : new ClaimsIdentity();
        ElsiePrincipal.SetUser(request, new ClaimsPrincipal(identity));
        return request;
    }

    [Fact]
    public async Task Anonymous_request_to_required_endpoint_is_challenged()
    {
        await using var sp = Build(app => app.MapGet("/private", () => "secret").RequireAuthorization());
        using var scope = sp.CreateScope();

        var outcome = await sp.GetRequiredService<ElsieDispatcher>()
            .DispatchAsync(Request("/private", authenticated: false, scope.ServiceProvider));

        Assert.Equal(401, outcome.Result!.StatusCode);
    }

    [Fact]
    public async Task Authenticated_request_passes()
    {
        await using var sp = Build(app => app.MapGet("/private", () => "secret").RequireAuthorization());
        using var scope = sp.CreateScope();

        var outcome = await sp.GetRequiredService<ElsieDispatcher>()
            .DispatchAsync(Request("/private", authenticated: true, scope.ServiceProvider));

        Assert.Equal(200, outcome.Result!.StatusCode);
    }

    [Fact]
    public async Task Group_requirement_covers_children_but_allow_anonymous_wins()
    {
        await using var sp = Build(app =>
        {
            var group = app.MapGroup("/admin").RequireAuthorization();
            group.MapGet("/stats", () => "stats");
            group.MapGet("/public", () => "public").AllowAnonymous();
        });

        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        using var scope = sp.CreateScope();

        var stats = await dispatcher.DispatchAsync(Request("/admin/stats", authenticated: false, scope.ServiceProvider));
        var open = await dispatcher.DispatchAsync(Request("/admin/public", authenticated: false, scope.ServiceProvider));

        Assert.Equal(401, stats.Result!.StatusCode);
        Assert.Equal(200, open.Result!.StatusCode);
    }

    [Fact]
    public async Task Endpoints_without_metadata_are_not_guarded()
    {
        await using var sp = Build(app => app.MapGet("/open", () => "open"));
        using var scope = sp.CreateScope();

        var outcome = await sp.GetRequiredService<ElsieDispatcher>()
            .DispatchAsync(Request("/open", authenticated: false, scope.ServiceProvider));

        Assert.Equal(200, outcome.Result!.StatusCode);
    }
}

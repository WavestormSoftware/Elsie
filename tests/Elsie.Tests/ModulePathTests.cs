using Elsie.Routing;
using Xunit;

namespace Elsie.Tests;

public class ModulePathTests
{
    private sealed class PrefixedModule : ElsieModule
    {
        public PrefixedModule()
        {
            Path("/api");
            Get("/ping", () => ElsieResult.Text("pong"));
            Group("/v1", () =>
            {
                Get("/items", () => ElsieResult.Text("items"));
                Group("/admin", () =>
                {
                    Get("/stats", () => ElsieResult.Text("stats"));
                });
            });
            Get("/rootish", () => ElsieResult.Text("rootish"));
        }
    }

    [Fact]
    public void Path_and_Group_prefix_routes()
    {
        var module = new PrefixedModule();
        var templates = module.Routes.Select(r => r.Template).OrderBy(t => t).ToArray();
        Assert.Equal(
            ["/api/ping", "/api/rootish", "/api/v1/admin/stats", "/api/v1/items"],
            templates);
    }

    [Fact]
    public void Prefixed_routes_match()
    {
        var table = RouteTable.FromModules([new PrefixedModule()]);
        var matchLookup = table.Lookup("GET", "/api/v1/admin/stats");
        Assert.Equal(RouteLookupStatus.Matched, matchLookup.Status);
        var match = matchLookup.Match;
        Assert.Equal("/api/v1/admin/stats", match!.Route.Template);
    }
}

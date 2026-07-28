using Elsie.Routing;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Elsie.Tests;

public class RouteMatcherTests
{
    private sealed class SampleModule : ElsieModule
    {
        public SampleModule()
        {
            Get("/", () => ElsieResult.Text("root"));
            Get("/hello/{name}", ctx => ElsieResult.Text(ctx.RouteValues["name"]));
            Get("/items/{id:int}", ctx => ElsieResult.Text(ctx.RouteValues["id"]));
            Post("/items", () => ElsieResult.Status(201));
            Get("/files/{*path}", ctx => ElsieResult.Text(ctx.RouteValues["path"]));
            Get("/files/readme", () => ElsieResult.Text("readme"));
        }
    }

    private static IRouteMatcher CreateMatcher()
    {
        var table = RouteTable.FromModules([new SampleModule()]);
        return new RouteMatcher(table);
    }

    [Fact]
    public void Matches_static_root()
    {
        var matcher = CreateMatcher();
        Assert.True(matcher.TryMatch("GET", new PathString("/"), out var match));
        Assert.NotNull(match);
        Assert.Equal("GET", match!.Route.Method);
    }

    [Fact]
    public void Extracts_route_parameter()
    {
        var matcher = CreateMatcher();
        Assert.True(matcher.TryMatch("GET", new PathString("/hello/Ada"), out var match));
        Assert.Equal("Ada", match!.RouteValues["name"]);
    }

    [Fact]
    public void Int_constraint_accepts_digits()
    {
        var matcher = CreateMatcher();
        Assert.True(matcher.TryMatch("GET", new PathString("/items/42"), out var match));
        Assert.Equal("42", match!.RouteValues["id"]);
    }

    [Fact]
    public void Int_constraint_rejects_non_numeric()
    {
        var matcher = CreateMatcher();
        Assert.False(matcher.TryMatch("GET", new PathString("/items/abc"), out _));
    }

    [Fact]
    public void Method_mismatch_does_not_match()
    {
        var matcher = CreateMatcher();
        Assert.False(matcher.TryMatch("GET", new PathString("/items"), out _));
    }

    [Fact]
    public void Lookup_reports_method_not_allowed()
    {
        var matcher = CreateMatcher();
        var lookup = matcher.Lookup("GET", new PathString("/items"));
        Assert.Equal(RouteLookupStatus.MethodNotAllowed, lookup.Status);
        Assert.Contains("POST", lookup.AllowedMethods);
    }

    [Fact]
    public void Catch_all_captures_remaining_segments()
    {
        var matcher = CreateMatcher();
        Assert.True(matcher.TryMatch("GET", new PathString("/files/a/b/c"), out var match));
        Assert.Equal("a/b/c", match!.RouteValues["path"]);
    }

    [Fact]
    public void Catch_all_allows_empty_remainder()
    {
        var matcher = CreateMatcher();
        Assert.True(matcher.TryMatch("GET", new PathString("/files"), out var match));
        Assert.Equal(string.Empty, match!.RouteValues["path"]);
    }

    [Fact]
    public void Concrete_route_wins_over_catch_all()
    {
        var matcher = CreateMatcher();
        Assert.True(matcher.TryMatch("GET", new PathString("/files/readme"), out var match));
        Assert.Equal("/files/readme", match!.Route.Template);
    }

    [Fact]
    public void Catch_all_not_final_throws_at_compile()
    {
        var module = new BadCatchAllModule();
        var table = RouteTable.FromModules([module]);
        Assert.Throws<InvalidOperationException>(() => new RouteMatcher(table));
    }

    [Fact]
    public void Duplicate_routes_throw()
    {
        var module = new DupModule();
        Assert.Throws<InvalidOperationException>(() => RouteTable.FromModules([module]));
    }

    private sealed class DupModule : ElsieModule
    {
        public DupModule()
        {
            Get("/x", () => ElsieResult.Text("a"));
            Get("/x", () => ElsieResult.Text("b"));
        }
    }

    private sealed class BadCatchAllModule : ElsieModule
    {
        public BadCatchAllModule()
        {
            Get("/{*path}/tail", ctx => ElsieResult.Text(ctx.RouteValues["path"]));
        }
    }
}

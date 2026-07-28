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
}

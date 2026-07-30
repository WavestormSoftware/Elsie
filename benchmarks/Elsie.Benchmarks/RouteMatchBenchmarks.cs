using BenchmarkDotNet.Attributes;
using Elsie.Routing;

namespace Elsie.Benchmarks;

[MemoryDiagnoser]
public class RouteMatchBenchmarks
{
    private RouteTable _table = null!;

    [GlobalSetup]
    public void Setup()
    {
        _table = RouteTable.FromModules([new BenchModule()]);
    }

    [Benchmark]
    public RouteLookup Static() => _table.Lookup("GET", "/");

    [Benchmark]
    public RouteLookup StaticDeep() => _table.Lookup("GET", "/api/v1/status");

    [Benchmark]
    public RouteLookup Constrained() => _table.Lookup("GET", "/items/42");

    [Benchmark]
    public RouteLookup Parameter() => _table.Lookup("GET", "/hello/Ada");

    [Benchmark]
    public RouteLookup CatchAll() => _table.Lookup("GET", "/docs/a/b/c");

    [Benchmark]
    public RouteLookup Miss() => _table.Lookup("GET", "/no/such/route");

    private sealed class BenchModule : ElsieModule
    {
        public BenchModule()
        {
            // ~60 routes: static, constrained, param, catch-all, multi-method mix
            Get("/", () => ElsieResult.Text("ok"));
            Get("/health", () => ElsieResult.Text("ok"));
            Get("/ready", () => ElsieResult.Text("ok"));
            Get("/api", () => ElsieResult.Text("ok"));
            Get("/api/v1", () => ElsieResult.Text("ok"));
            Get("/api/v1/status", () => ElsieResult.Text("ok"));
            Get("/api/v1/version", () => ElsieResult.Text("ok"));
            Get("/api/v1/metrics", () => ElsieResult.Text("ok"));
            Get("/api/v2/status", () => ElsieResult.Text("ok"));
            Get("/about", () => ElsieResult.Text("ok"));
            Get("/contact", () => ElsieResult.Text("ok"));
            Get("/login", () => ElsieResult.Text("ok"));
            Get("/logout", () => ElsieResult.Text("ok"));
            Get("/settings", () => ElsieResult.Text("ok"));
            Get("/settings/profile", () => ElsieResult.Text("ok"));
            Get("/settings/security", () => ElsieResult.Text("ok"));
            Get("/admin", () => ElsieResult.Text("ok"));
            Get("/admin/users", () => ElsieResult.Text("ok"));
            Get("/admin/roles", () => ElsieResult.Text("ok"));
            Get("/admin/audit", () => ElsieResult.Text("ok"));
            Get("/blog", () => ElsieResult.Text("ok"));
            Get("/blog/latest", () => ElsieResult.Text("ok"));
            Get("/blog/archive", () => ElsieResult.Text("ok"));
            Get("/assets/manifest", () => ElsieResult.Text("ok"));
            Get("/openapi.json", () => ElsieResult.Text("ok"));

            Get("/hello/{name}", () => ElsieResult.Text("ok"));
            Get("/users/{id}", () => ElsieResult.Text("ok"));
            Get("/users/{id}/profile", () => ElsieResult.Text("ok"));
            Get("/users/{id}/posts", () => ElsieResult.Text("ok"));
            Get("/orgs/{org}/repos/{repo}", () => ElsieResult.Text("ok"));
            Get("/teams/{team}/members/{member}", () => ElsieResult.Text("ok"));
            Get("/shops/{shop}/products/{product}", () => ElsieResult.Text("ok"));
            Get("/files/{name}", () => ElsieResult.Text("ok"));
            Get("/tags/{tag}", () => ElsieResult.Text("ok"));
            Get("/search/{query}", () => ElsieResult.Text("ok"));

            Get("/items/{id:int}", () => ElsieResult.Text("ok"));
            Get("/orders/{id:long}", () => ElsieResult.Text("ok"));
            Get("/accounts/{id:guid}", () => ElsieResult.Text("ok"));
            Get("/flags/{on:bool}", () => ElsieResult.Text("ok"));
            Get("/slugs/{s:alpha}", () => ElsieResult.Text("ok"));
            Get("/codes/{c:length(4)}", () => ElsieResult.Text("ok"));
            Get("/pages/{n:range(1,100)}", () => ElsieResult.Text("ok"));
            Get("/sku/{s:regex(^[A-Z]{3}[0-9]{3}$)}", () => ElsieResult.Text("ok"));
            Get("/posts/{year:int}/{month:int}", () => ElsieResult.Text("ok"));
            Get("/posts/{year:int}/{month:int}/{day:int}", () => ElsieResult.Text("ok"));

            Get("/docs/{*path}", () => ElsieResult.Text("ok"));
            Get("/static/{*path}", () => ElsieResult.Text("ok"));
            Get("/media/{*path}", () => ElsieResult.Text("ok"));
            Get("/legacy/{*path}", () => ElsieResult.Text("ok"));

            Post("/items", () => ElsieResult.NoContent());
            Post("/users", () => ElsieResult.NoContent());
            Post("/orders", () => ElsieResult.NoContent());
            Post("/login", () => ElsieResult.NoContent());
            Put("/users/{id}", () => ElsieResult.NoContent());
            Put("/items/{id:int}", () => ElsieResult.NoContent());
            Patch("/users/{id}", () => ElsieResult.NoContent());
            Delete("/users/{id}", () => ElsieResult.NoContent());
            Delete("/items/{id:int}", () => ElsieResult.NoContent());
            Get("/optional/{id?}", () => ElsieResult.Text("ok"));
            Get("/defaults/{id=default}", () => ElsieResult.Text("ok"));
            Get("/api/v1/widgets/{id:int}/parts/{part}", () => ElsieResult.Text("ok"));
            Get("/api/v1/widgets/{id:int}/parts", () => ElsieResult.Text("ok"));
            Get("/catalog/{category}/{item}", () => ElsieResult.Text("ok"));
            Get("/catalog/{category}", () => ElsieResult.Text("ok"));
            Get("/reports/{year:int}/{quarter:range(1,4)}", () => ElsieResult.Text("ok"));
            Head("/ping-head", () => ElsieResult.NoContent());
        }
    }
}

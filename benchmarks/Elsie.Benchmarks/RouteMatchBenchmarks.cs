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
            Get("/", () => ElsieResult.Text("ok"));
            Get("/hello/{name}", () => ElsieResult.Text("ok"));
            Get("/items/{id:int}", () => ElsieResult.Text("ok"));
            Get("/docs/{*path}", () => ElsieResult.Text("ok"));
            Post("/items", () => ElsieResult.NoContent());
        }
    }
}

using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Benchmarks;

[MemoryDiagnoser]
public class DispatchBenchmarks
{
    private ServiceProvider _sp = null!;
    private ElsieDispatcher _dispatcher = null!;
    private ElsieRequest _ping = null!;
    private ElsieRequest _item = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<BenchModule>();
        _sp = services.BuildServiceProvider();
        _dispatcher = _sp.GetRequiredService<ElsieDispatcher>();
        _ping = new ElsieRequest("GET", "/ping");
        _item = new ElsieRequest("GET", "/items/7");
    }

    [GlobalCleanup]
    public void Cleanup() => _sp.Dispose();

    [Benchmark]
    public Task<ElsieDispatchResult> Ping() => _dispatcher.DispatchAsync(_ping);

    [Benchmark]
    public Task<ElsieDispatchResult> Constrained() => _dispatcher.DispatchAsync(_item);

    private sealed class BenchModule : ElsieModule
    {
        public BenchModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
            Get("/items/{id:int}", ctx =>
            {
                _ = ctx.Route<int>("id");
                return ElsieResult.Json(new { ok = true });
            });
        }
    }
}

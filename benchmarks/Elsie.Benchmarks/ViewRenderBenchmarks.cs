using BenchmarkDotNet.Attributes;
using Elsie.Views;

namespace Elsie.Benchmarks;

[MemoryDiagnoser]
public class ViewRenderBenchmarks
{
    private string _root = null!;
    private FluidElsieViewEngine _engine = null!;
    private ElsieViewAmbient _ambient = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "elsie-bench-views-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "_Layout.liquid"),
            "<html><body>{% renderbody %}</body></html>");
        File.WriteAllText(
            Path.Combine(_root, "home.liquid"),
            "{% layout '_Layout.liquid' %}<h1>Hello {{ Name }}!</h1><p>{{ Request.Path }}</p>");

        _engine = new FluidElsieViewEngine(new ElsieViewOptions
        {
            ContentRoot = _root,
            RootPath = "",
            ReloadOnChange = false
        });
        _ambient = new ElsieViewAmbient { Path = "/", Method = "GET" };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    [Benchmark]
    public Task<string> RenderHome() =>
        _engine.RenderAsync("home", new { Name = "Ada", Title = "Bench" }, _ambient);
}

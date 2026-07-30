using Elsie.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Views.Tests;

public class ViewEngineTests
{
    [Fact]
    public async Task Renders_encoded_token()
    {
        using var dir = TempViews.Create(("home.liquid", "<p>{{ Name }}</p>"));
        var engine = CreateEngine(dir.Path);
        var html = await engine.RenderAsync("home", new { Name = "A<b>" });
        Assert.Equal("<p>A&lt;b&gt;</p>", html);
    }

    [Fact]
    public async Task Renders_raw_filter()
    {
        using var dir = TempViews.Create(("home.liquid", "<p>{{ Html | raw }}</p>"));
        var engine = CreateEngine(dir.Path);
        var html = await engine.RenderAsync("home", new { Html = "<b>x</b>" });
        Assert.Equal("<p><b>x</b></p>", html);
    }

    [Fact]
    public async Task Applies_layout_and_partial()
    {
        using var dir = TempViews.Create(
            ("home.liquid", "{% layout '_Layout.liquid' %}\n<h1>{{ Name }}</h1>\n{% partial 'part' %}"),
            ("_Layout.liquid", "<html><title>{{ Title }}</title>{% renderbody %}</html>"),
            ("part.liquid", "<p>p-{{ Name }}</p>"));
        var engine = CreateEngine(dir.Path);
        var html = await engine.RenderAsync("home", new { Title = "Hi", Name = "Ada" });
        Assert.Equal("<html><title>Hi</title>\n<h1>Ada</h1>\n<p>p-Ada</p></html>", html);
    }

    [Fact]
    public async Task Exposes_request_ambient()
    {
        using var dir = TempViews.Create(("home.liquid", "{{ Request.Path }}|{{ Request.Method }}"));
        var engine = CreateEngine(dir.Path);
        var html = await engine.RenderAsync(
            "home",
            model: null,
            ambient: new ElsieViewAmbient { Path = "/hi", Method = "GET" });
        Assert.Equal("/hi|GET", html);
    }

    [Fact]
    public async Task Missing_view_throws()
    {
        using var dir = TempViews.Create();
        var engine = CreateEngine(dir.Path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.RenderAsync("nope", null));
    }

    [Fact]
    public async Task Path_traversal_rejected()
    {
        using var dir = TempViews.Create(("home.liquid", "x"));
        var engine = CreateEngine(dir.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RenderAsync("../secret", null));
    }

    [Fact]
    public async Task Missing_property_is_empty()
    {
        using var dir = TempViews.Create(("home.liquid", "Hi {{ Missing }}!"));
        var engine = CreateEngine(dir.Path);
        var html = await engine.RenderAsync("home", new { Name = "Ada" });
        Assert.Equal("Hi !", html);
    }

    [Fact]
    public async Task Reload_on_change_picks_up_edits()
    {
        using var dir = TempViews.Create(("home.liquid", "v1"));
        var engine = CreateEngine(dir.Path, reload: true);
        Assert.Equal("v1", await engine.RenderAsync("home", null));

        var path = Path.Combine(dir.Path, "Views", "home.liquid");
        await File.WriteAllTextAsync(path, "v2");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("v2", await engine.RenderAsync("home", null));
    }

    [Fact]
    public async Task Reload_disabled_keeps_cached_content()
    {
        using var dir = TempViews.Create(("home.liquid", "v1"));
        var engine = CreateEngine(dir.Path, reload: false);
        Assert.Equal("v1", await engine.RenderAsync("home", null));

        var path = Path.Combine(dir.Path, "Views", "home.liquid");
        await File.WriteAllTextAsync(path, "v2");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("v1", await engine.RenderAsync("home", null));
    }

    [Fact]
    public async Task ViewAsync_returns_html_result()
    {
        using var dir = TempViews.Create(("home.liquid", "<b>{{ Name }}</b>"));
        var services = new ServiceCollection();
        services.AddElsieViews(o => o.ContentRoot = dir.Path);
        await using var sp = services.BuildServiceProvider();

        var request = new ElsieRequest("GET", "/", requestServices: sp);
        var ctx = new ElsieContext(request, new ElsieResponse(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var result = await ctx.ViewAsync("home", new { Name = "z" });
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
        Assert.Equal("<b>z</b>", System.Text.Encoding.UTF8.GetString(result.Body!.Value.Span));
    }

    private static FluidElsieViewEngine CreateEngine(string contentRoot, bool reload = true) =>
        new(new ElsieViewOptions { ContentRoot = contentRoot, ReloadOnChange = reload });

    private sealed class TempViews : IDisposable
    {
        public string Path { get; }

        private TempViews(string path) => Path = path;

        public static TempViews Create(params (string Name, string Content)[] files)
        {
            var root = Directory.CreateTempSubdirectory("elsie-views-");
            var views = System.IO.Path.Combine(root.FullName, "Views");
            Directory.CreateDirectory(views);
            foreach (var (name, content) in files)
            {
                var full = System.IO.Path.Combine(views, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            return new TempViews(root.FullName);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}

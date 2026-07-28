using Elsie.Views;
using Xunit;

namespace Elsie.Views.Tests;

public class ViewEngineTests
{
    [Fact]
    public async Task Renders_encoded_token()
    {
        using var dir = TempViews.Create(("home.html", "<p>{{Name}}</p>"));
        var engine = new ElsieFileViewEngine(new ElsieViewOptions { ContentRoot = dir.Path });
        var html = await engine.RenderAsync("home", new { Name = "A<b>" });
        Assert.Equal("<p>A&lt;b&gt;</p>", html);
    }

    [Fact]
    public async Task Renders_raw_token()
    {
        using var dir = TempViews.Create(("home.html", "<p>{{{Html}}}</p>"));
        var engine = new ElsieFileViewEngine(new ElsieViewOptions { ContentRoot = dir.Path });
        var html = await engine.RenderAsync("home", new { Html = "<b>x</b>" });
        Assert.Equal("<p><b>x</b></p>", html);
    }

    [Fact]
    public async Task Applies_layout_body()
    {
        using var dir = TempViews.Create(
            ("home.html", "@layout _Layout\n<h1>{{Name}}</h1>"),
            ("_Layout.html", "<html><title>{{Title}}</title>{{body}}</html>"));
        var engine = new ElsieFileViewEngine(new ElsieViewOptions { ContentRoot = dir.Path });
        var html = await engine.RenderAsync("home", new { Title = "Hi", Name = "Ada" });
        Assert.Equal("<html><title>Hi</title><h1>Ada</h1></html>", html);
    }

    [Fact]
    public async Task Missing_view_throws()
    {
        using var dir = TempViews.Create();
        var engine = new ElsieFileViewEngine(new ElsieViewOptions { ContentRoot = dir.Path });
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.RenderAsync("nope", null));
    }

    [Fact]
    public async Task Path_traversal_rejected()
    {
        using var dir = TempViews.Create(("home.html", "x"));
        var engine = new ElsieFileViewEngine(new ElsieViewOptions { ContentRoot = dir.Path });
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RenderAsync("../secret", null));
    }

    [Fact]
    public async Task Missing_property_is_empty()
    {
        using var dir = TempViews.Create(("home.html", "Hi {{Missing}}!"));
        var engine = new ElsieFileViewEngine(new ElsieViewOptions { ContentRoot = dir.Path });
        var html = await engine.RenderAsync("home", new { Name = "Ada" });
        Assert.Equal("Hi !", html);
    }

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
                File.WriteAllText(System.IO.Path.Combine(views, name), content);
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

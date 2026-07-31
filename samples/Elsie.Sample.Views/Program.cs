using Microsoft.Extensions.DependencyInjection;
using Elsie;
using Elsie.Web;
using Elsie.Views;

// Views sample — Fluid/Liquid templates + layout.
//   dotnet run --project samples/Elsie.Sample.Views
//   GET /

var contentRoot = ResolveContentRoot();

static string ResolveContentRoot()
{
    var cwd = Directory.GetCurrentDirectory();
    if (Directory.Exists(Path.Combine(cwd, "Views")))
    {
        return cwd;
    }

    var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    return Directory.Exists(Path.Combine(project, "Views")) ? project : cwd;
}

ElsieApp.Create(args)
    .ContentRoot(contentRoot)
    .Configure(o => o.ScanEntryAssembly = false)
    .Module<HomeModule>()
    .Services(s =>
    {
        s.AddElsieViews(o =>
        {
            o.ContentRoot = contentRoot;
            o.ReloadOnChange = true;
        });
    })
    .Run();

public sealed class HomeModule : ElsieModule
{
    public HomeModule()
    {
        Get("/", async (ctx, ct) =>
            await ctx.ViewAsync(
                "home",
                new { Title = "Elsie", Name = "world" },
                cancellationToken: ct))
            .WithSummary("Home")
            .WithTags("pages");
    }
}

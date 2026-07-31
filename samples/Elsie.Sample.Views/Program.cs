using Microsoft.Extensions.DependencyInjection;
using Elsie;
using Elsie.Web;
using Elsie.Views;

// Views sample — Fluid/Liquid templates + layout.
//   dotnet run --project samples/Elsie.Sample.Views
//   GET /

ElsieApp.Create(args)
    .ContentRoot(Directory.GetCurrentDirectory())
    .Configure(o => o.ScanEntryAssembly = false)
    .Module<HomeModule>()
    .Services(s =>
    {
        s.AddElsieViews(o =>
        {
            o.ContentRoot = Directory.GetCurrentDirectory();
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

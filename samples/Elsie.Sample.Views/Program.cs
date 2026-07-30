using Elsie;
using Elsie.AspNetCore;
using Elsie.Views;

// Views sample — Fluid/Liquid templates + layout.
//   dotnet run --project samples/Elsie.Sample.Views
//   GET /

var builder = WebApplication.CreateBuilder(args);
builder.AddElsie(o => o.ScanEntryAssembly = false);
builder.Services.AddElsieViews(o =>
{
    o.ContentRoot = builder.Environment.ContentRootPath;
    o.ReloadOnChange = builder.Environment.IsDevelopment();
});
builder.Services.AddElsieModule<HomeModule>();

var app = builder.Build();
app.MapElsie();
app.Run();

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

using Elsie;
using Elsie.AspNetCore;
using Elsie.Views;

// Views sample — file templates + layout.
//   dotnet run --project samples/Elsie.Sample.Views
//   GET /

var builder = WebApplication.CreateBuilder(args);
builder.AddElsie();
builder.Services.AddElsieViews(o => o.ContentRoot = builder.Environment.ContentRootPath);
builder.Services.AddElsieModule<HomeModule>();

var app = builder.Build();
app.MapElsie();
app.Run();

public sealed class HomeModule : ElsieModule
{
    public HomeModule()
    {
        Get("/", async (ctx, ct) => await ctx.ViewAsync("home", new { Title = "Elsie", Name = "world" }, cancellationToken: ct));
    }
}

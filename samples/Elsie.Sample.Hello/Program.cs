using Elsie;
using Elsie.AspNetCore;

// Minimal Elsie app — one module, one route family.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddElsie();
builder.Services.AddElsieModule<HelloModule>();

var app = builder.Build();
app.MapElsie();
app.Run();

public sealed class HelloModule : ElsieModule
{
    public HelloModule()
    {
        Get("/", () => ElsieResult.Text("Elsie says hello. Try GET /hello/Ada"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
        Get("/health", () => ElsieResult.Json(new { status = "ok" }));
    }
}

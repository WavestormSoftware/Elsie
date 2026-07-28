using Elsie;
using Elsie.AspNetCore;

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
        Get("/", () => ElsieResult.Text("Elsie is running."));
        Get("/hello/{name}", ctx => ElsieResult.Text($"Hello {ctx.RouteValues["name"]}!"));
        Get("/health", () => ElsieResult.Json(new { status = "ok" }));
    }
}

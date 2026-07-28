using Elsie;
using Elsie.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddElsie();
builder.Services.AddElsieModule<HelloModule>();
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddAfter((ctx, _) => ctx.Response.Headers["X-Elsie"] = "1");
});

var app = builder.Build();
app.MapElsie();
app.Run();

public sealed class HelloModule : ElsieModule
{
    public HelloModule()
    {
        Get("/", () => ElsieResult.Text("Elsie is running."));
        Get("/hello/{name}", ctx => ElsieResult.Text($"Hello {ctx.RouteValues["name"]}!"));
        Get("/items/{id:int}", ctx => ElsieResult.Json(new { id = ctx.RouteValues["id"] }));
        Get("/health", () => ElsieResult.Json(new { status = "ok" }));
        Get("/docs", () => ElsieResult.Redirect("/hello/docs"));
    }
}

using Elsie;
using Elsie.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddElsie();
// Prefer explicit registration. Entry-assembly scan is on by default; disable with:
// builder.Services.AddElsie(o => o.ScanEntryAssembly = false);
builder.Services.AddSingleton<IGreeter, Greeter>();
builder.Services.AddElsieModule<HelloModule>();
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddAfter((ctx, _) => ctx.Response.Headers["X-Elsie"] = "1");
});

var app = builder.Build();
app.MapElsie();
app.Run();

public interface IGreeter
{
    string Greet(string name);
}

public sealed class Greeter : IGreeter
{
    public string Greet(string name) => $"Hello {name}!";
}

public sealed class HelloModule : ElsieModule
{
    // Modules are singletons — inject singleton/transient app services via ctor.
    public HelloModule(IGreeter greeter)
    {
        Get("/", () => ElsieResult.Text("Elsie is running."));

        Get("/hello/{name}", ctx =>
            ElsieResult.Text(greeter.Greet(ctx.RouteValues["name"])));

        // Request-scoped services: resolve per request via ctx.
        Get("/hello-di/{name}", ctx =>
        {
            var g = ctx.GetRequiredService<IGreeter>();
            return ElsieResult.Text(g.Greet(ctx.RouteValues["name"]));
        });

        Get("/items/{id:int}", ctx => ElsieResult.Json(new { id = ctx.RouteValues["id"] }));
        Get("/health", () => ElsieResult.Json(new { status = "ok" }));
        Get("/docs", () => ElsieResult.Redirect("/hello/docs"));
    }
}

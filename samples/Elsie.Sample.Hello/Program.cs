using Elsie;
using Elsie.AspNetCore;

// -----------------------------------------------------------------------------
// Easy sample — smallest useful Elsie app on ASP.NET Core.
// Try:  GET /  |  GET /hello/Ada  |  GET /hello/Ada?shout=true  |  GET /health
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IGreeter, Greeter>();
builder.Services.AddElsie();
builder.Services.AddElsieModule<HelloModule>();
builder.Services.ConfigureElsiePipelines(p =>
{
    // App-wide after hook → host-agnostic response header bag
    p.AddAfter((ctx, _) => ctx.Response.Headers["X-Elsie-Sample"] = "hello");
});

var app = builder.Build();
app.MapElsie();
app.Run();

public interface IGreeter
{
    string Greet(string name, bool shout);
}

public sealed class Greeter : IGreeter
{
    public string Greet(string name, bool shout)
    {
        var msg = $"Hello {name}!";
        return shout ? msg.ToUpperInvariant() : msg;
    }
}

public sealed class HelloModule : ElsieModule
{
    // Modules are singletons — inject singleton-safe services via ctor.
    public HelloModule(IGreeter greeter)
    {
        Get("/", () => ElsieResult.Text(
            "Elsie Hello sample. Try GET /hello/Ada , /hello/Ada?shout=true , /health , /go"));

        Get("/hello/{name}", ctx =>
        {
            var name = ctx.RouteOrDefault("name") ?? "world";
            var shout = ctx.TryGetQueryBool("shout", out var s) && s;
            // Prefer ctor-injected services; request scope also works:
            // var greeter = ctx.GetRequiredService<IGreeter>();
            return ElsieResult.Text(greeter.Greet(name, shout));
        });

        Get("/health", () => ElsieResult.Json(new { status = "ok", sample = "hello" }));

        // Redirect helper
        Get("/go", () => ElsieResult.Redirect("/hello/redirected"));

        // Constraint demo — non-integers 404 at the router
        Get("/items/{id:int}", ctx =>
        {
            if (!ctx.RequireRouteInt("id", out var id, out var error))
            {
                return error!;
            }

            return ctx.Json(new { id, source = ctx.Request.Path });
        });
    }
}

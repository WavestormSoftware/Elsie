using Elsie;
using Elsie.Web;

// Easy sample — DI, typed route/query, constraints, pipelines (after HelloWorld).
// Try:  GET /  |  GET /hello/Ada  |  GET /hello/Ada?shout=true  |  GET /health  |  GET /items/42

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IGreeter, Greeter>();
builder.AddElsie(o => o.ScanEntryAssembly = false);
builder.Services.AddElsieModule<HelloModule>();
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddAfter((ctx, result) =>
    {
        ctx.Response.Headers["X-Elsie-Sample"] = "hello";
        return result;
    });
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
    public HelloModule(IGreeter greeter)
    {
        Get("/", () => ElsieResult.Text(
            "Elsie Hello sample. Try GET /hello/Ada , /hello/Ada?shout=true , /health , /go , /items/42"));

        Get("/hello/{name}", ctx =>
        {
            var name = ctx.RouteOrDefault("name") ?? "world";
            var shout = ctx.Query<bool>("shout");
            return ElsieResult.Text(greeter.Greet(name, shout));
        }).Named("hello").WithSummary("Greet someone");

        Get("/health", () => ElsieResult.Json(new { status = "ok", sample = "hello" }));

        Get("/go", ctx => ElsieResult.Redirect(ctx.UrlFor("hello", new { name = "redirected" })));

        Get("/items/{id:int}", ctx =>
        {
            if (!ctx.RequireRoute("id", out int id, out var error))
            {
                return error!;
            }

            return ctx.Json(new { id, source = ctx.Request.Path });
        });
    }
}

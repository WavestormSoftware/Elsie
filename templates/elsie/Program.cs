using Elsie;
using Elsie.AspNetCore;

// Minimal Elsie app — GET /  |  GET /hello/{name}
ElsieWeb.Run<AppModule>(args);

public sealed class AppModule : ElsieModule
{
    public AppModule()
    {
        Get("/", () => ElsieResult.Text("Hello, Elsie!"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
    }
}

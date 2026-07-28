using Elsie;
using Elsie.AspNetCore;

// Smallest Elsie app.
//   dotnet run --project samples/Elsie.Sample.HelloWorld
//   GET /  |  GET /hello/Ada

ElsieWeb.Run<HelloModule>(args);

public sealed class HelloModule : ElsieModule
{
    public HelloModule()
    {
        Get("/", () => ElsieResult.Text("Hello, world!"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
    }
}

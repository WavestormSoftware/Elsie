using Elsie;
using Elsie.Web;

// Smallest Elsie app.
//   dotnet run --project samples/Elsie.Sample.HelloWorld
//   GET /  |  GET /hello/Ada

ElsieApp.Run<HelloModule>(args);

public sealed class HelloModule : ElsieModule
{
    public HelloModule()
    {
        Get("/", () => ElsieResult.Text("Hello, world!"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
    }
}

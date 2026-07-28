using Elsie;
using Elsie.AspNetCore;

// Smallest Elsie app. Run: dotnet run --project samples/Elsie.Sample.HelloWorld
// Try: GET /  |  GET /hello/Ada

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
        Get("/", () => ElsieResult.Text("Hello, world!"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
    }
}

# Elsie

Lightweight, low-ceremony HTTP modules for **ASP.NET Core** (.NET 8 / .NET 9).

Elsie is inspired by the developer experience of Sinatra-style frameworks (small modules, explicit routes, minimal wiring). It is an **original** WavestormSoftware project under the MIT license — not a fork of any other framework.

## Quick start

```csharp
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
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteValues["name"]}"));
    }
}
```

## Packages

| Package | Purpose |
|---------|---------|
| `Elsie` | Modules, routing, context, results |
| `Elsie.AspNetCore` | DI + `MapElsie` / middleware |
| `Elsie.Testing` | In-process test host |

## Build

```bash
dotnet restore Elsie.sln
dotnet build Elsie.sln -c Release
dotnet test Elsie.sln -c Release
dotnet run --project samples/Elsie.Sample.Hello
```

## Docs

- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — design
- [`docs/PLAN.md`](docs/PLAN.md) — implementation plan
- [`AGENTS.md`](AGENTS.md) — notes for coding agents

## License

MIT © WavestormSoftware — see [LICENSE](LICENSE).

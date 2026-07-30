# Getting started

Elsie is a Sinatra-style HTTP module framework for **.NET 8** and **.NET 10**. Core is host-agnostic; **`Elsie.AspNetCore`** is the default host.

## Install

```bash
dotnet new web -n HelloElsie
cd HelloElsie
dotnet add package Elsie.AspNetCore   # 0.2.0-alpha.1
```

Or from a local pack:

```bash
dotnet pack templates/Elsie.Templates.csproj -c Release -o artifacts/nuget
dotnet new install artifacts/nuget/Elsie.Templates.0.2.0-alpha.1.nupkg
dotnet new elsie -n HelloElsie
dotnet new elsie-api -n TodosApi
```

## Smallest app

```csharp
using Elsie;
using Elsie.AspNetCore;

ElsieWeb.Run<App>(args);

public sealed class App : ElsieModule
{
    public App()
    {
        Get("/", () => ElsieResult.Text("Hello, Elsie!"));
        Get("/hello/{name}", ctx =>
            ElsieResult.Text($"Hello {ctx.RouteOrDefault("name")}!"));
    }
}
```

```bash
dotnet run
# GET /  →  Hello, Elsie!
# GET /hello/Ada  →  Hello Ada!
```

## Builder form

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddElsie(); // registers core DI + quiets console logs by default
builder.Services.AddElsieModule<App>();

var app = builder.Build();
app.MapElsie();
app.Run();
```

## Samples in this repo

| Project | Focus |
|---------|--------|
| `samples/Elsie.Sample.HelloWorld` | One-liner |
| `samples/Elsie.Sample.Hello` | DI, query, constraints, pipelines |
| `samples/Elsie.Sample.Api` | CRUD + API key + OpenAPI |
| `samples/Elsie.Sample.Views` | Fluid/Liquid |
| `samples/Elsie.Sample.Full` | Auth, CORS, rate limit, health, static, views |

```bash
dotnet run --project samples/Elsie.Sample.HelloWorld
dotnet run --project samples/Elsie.Sample.Full
```

## Next

- [modules.md](modules.md) — registration, DI lifetimes
- [routing.md](routing.md) — templates, constraints, precedence
- [results.md](results.md) — response factories
- [testing.md](testing.md) — in-memory and TestServer hosts

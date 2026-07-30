# Getting started

Elsie is a Sinatra-style HTTP module framework for **.NET 8** and **.NET 10**. Core is host-agnostic; the **`Elsie`** package pulls the ASP.NET Core host.

## Install

```bash
dotnet new web -n HelloElsie
cd HelloElsie
dotnet add package Elsie
```

Or scaffold:

```bash
dotnet new install Elsie.Templates
dotnet new elsie -n HelloElsie
dotnet new elsie-api -n TodosApi
```

## Smallest app

```csharp
using Elsie;
using Elsie.Web;

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
| `samples/Elsie.Sample.Dashboard` | Multi-page views, login/register, cookie dashboard |
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

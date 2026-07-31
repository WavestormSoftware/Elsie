# Minimal APIs → Elsie

| ASP.NET Minimal | Elsie |
|-----------------|-------|
| `WebApplication.CreateBuilder` | `ElsieApp.Create(args)` |
| `app.MapGet("/x", ...)` | `Get("/x", ...)` inside `ElsieModule` |
| `Results.Ok(obj)` | `ElsieResult.Json(obj)` or `ctx.Json(obj)` |
| `Results.Problem` | `ElsieResult.Problem` / `ctx.Problem` |
| `Results.File` | `ElsieResult.File` |
| `[FromBody] T` | `await ctx.BindJsonAsync<T>()` |
| `[FromQuery] T` | `ctx.BindQuery<T>()` / `ctx.Query<T>("q")` |
| `[FromRoute]` | `ctx.Route<T>("id")` |
| `builder.Services.Add…` | `.Services(s => s.Add…)` |
| `app.UseAuthentication` | `AddElsieAuth` / `.Auth(...)` |
| `app.UseCors` | `AddElsieCors` |
| `app.UseRateLimiter` | `ElsieRateLimit.FixedWindow` before-hook |
| `IFormFile` | `ctx.ReadFormFilesAsync()` / `ElsieFormFile` |
| `TypedResults` | plain `ElsieResult` factories |
| `MapGroup` | `Group("/api", () => { ... })` |
| `WithName` / OpenAPI | `.Named` / `.Accepts` / `.Produces` / `.OpenApi(...)` |
| TestServer | `ElsieInMemoryHost` / `ElsieTestHost` / `StartAsync` |

Elsie has **no** middleware pipeline order. Cross-cutting = before/after hooks, principal attachers, request filters, or host features.

# Endpoints (minimal-API mapping)

Elsie has two registration styles that share one route table:

| Style | Best for |
|-------|----------|
| `ElsieModule` + `Get`/`Post`/... | feature modules, DI-constructed, module-scoped middleware |
| `app.MapGet`/`MapPost`/`MapGroup` | small apps, prototype handlers, lambda-style code |

Both feed the same `RouteTable`, so you can mix them in one app. Duplicate
`METHOD /template` registrations, ambiguous templates, unknown constraints and duplicate route
names still fail at startup.

## Mapping endpoints

```csharp
ElsieApp.Create(args)
    .Services(s => s.AddSingleton<IUserStore, InMemoryUserStore>())
    .Use((app, _) =>
    {
        app.MapGet("/", () => "hello");

        app.MapGet("/users/{id:int}", (int id, IUserStore store) =>
            store.Find(id) is { } user ? ElsieResults.Ok(user) : ElsieResults.NotFound());

        app.MapPost("/users", (CreateUser body) => ElsieResults.Created($"/users/1"));

        app.MapGroup("/api")
            .WithTags("Api")
            .MapGet("/health", () => new { status = "ok" });
    })
    .Listen("http://127.0.0.1:5000")
    .Run();
```

`Map*` works on `ElsieApp` itself (`IElsieEndpointRouteBuilder`), on a route group, and on a
standalone `ElsieEndpointRegistry` (register it with `services.AddElsieEndpoints(...)` for custom
hosts and in-memory tests).

## Parameter binding

| Parameter | Binding |
|-----------|---------|
| `ElsieContext`, `ElsieRequest`, `ElsieResponse`, `CancellationToken`, `IServiceProvider` | supplied by the framework |
| `[FromServices]` / `[ElsieService]` | request-scoped DI |
| `[FromRoute]`, `[FromRoute("name")]` | route value |
| `[FromQuery]`, `[FromHeader]` | query string / header (arrays bind from repeated keys) |
| `[FromBody]` | JSON body, or form fields for urlencoded/multipart |
| simple types (`int`, `string`, `Guid`, `DateTime`, enums, ...) | route value when the template names the parameter, otherwise the query string |
| complex types (POCO/record) | JSON body when the method can carry one, otherwise a query object (needs a parameterless constructor) |

Missing required values, conversion failures and invalid JSON produce `application/problem+json`
(400) or 415 responses instead of exceptions. `ElsieOptions.MaxBindBodySize` still caps bodies.
If an `IElsieValidator<T>` is registered (`Elsie.Validation`), bound bodies and query objects are
validated automatically and failures return a 400 validation problem.

## Return values

| Return | Response |
|--------|----------|
| `ElsieResult` | used as-is |
| `IElsieResult` (`ElsieResults.*`) | executed against the request (app JSON options, problem `instance`/`traceId`) |
| `string` | `text/plain` 200 |
| POCO / record | JSON 200 |
| `null` | 204 (or whatever the context already holds) |
| `Task` / `ValueTask` / `Task<T>` / `ValueTask<T>` | awaited first |

## Groups, filters and metadata

```csharp
var api = app.MapGroup("/api")
    .WithTags("Api")
    .RequireAuthorization("api")
    .AddEndpointFilter(new TimingFilter());

api.MapGet("/orders/{id:guid}", (Guid id) => ElsieResults.Ok(id))
   .WithName("GetOrder")
   .Produces<Order>(200)
   .AllowAnonymous();
```

- Group conventions (prefix, filters, metadata) are copied into child groups and applied to every
  endpoint mapped afterwards.
- `IElsieEndpointFilter` runs around the handler. Call `next(context)` to continue, return a value
  (or `ElsieResults.*`) to short-circuit, or inspect/rewrite the handler's result. Filters run
  innermost-last; exceptions still reach the application exception handler.
- Metadata is read back with `RouteDescriptor.GetMetadata<T>()` or `GetMetadataOfType<T>()`, and is
  available to middleware via `ElsieContext.Endpoint`. Built-in metadata:
  `ElsieAuthorizationRequirements`, `ElsieAllowAnonymousMetadata`, `ElsieCorsMetadata`,
  `ElsieRateLimitingMetadata`, `ElsieEndpointDisplayNameMetadata`.
- `RequireAuthorization`, `AllowAnonymous`, `RequireCors`, `RequireRateLimiting` attach metadata;
  the matching middleware in `Elsie.Auth` / `Elsie.Cors` / rate limiting enforces it.

## OpenAPI

Mapping endpoints infers OpenAPI metadata where it can: a body parameter becomes the request
schema and a complex return type becomes the 200 response schema. `Accepts`/`Produces`/`WithTags`/
`WithName`/`WithSummary` still override or extend inference. Handlers that return `ElsieResult` or
`IElsieResult` declare their responses explicitly with `Produces<T>(statusCode)`.

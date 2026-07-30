# OpenAPI

Core builds an OpenAPI 3 document from the **`RouteTable`** + route metadata. The ASP.NET host serves it.

## Map the document

```csharp
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "My API";
    o.Info.Version = "v1";
    o.Info.Description = "…";
    o.DocumentPath = "/openapi.json"; // default
    o.UiPath = "/scalar";             // optional Scalar CDN HTML page

    o.Info.SecuritySchemes["ApiKey"] = ElsieOpenApiSecurityScheme.ApiKeyHeader();
    o.Info.SecuritySchemes["Bearer"] = ElsieOpenApiSecurityScheme.BearerJwt();
});
// Call before or alongside MapElsie — OpenAPI is a separate ASP.NET endpoint
app.MapElsie();
```

## Route metadata

```csharp
Get("/todos/{id:guid}", handler)
    .Named("getTodo")                 // operationId
    .WithSummary("Get todo")
    .WithDescription("…")
    .WithTags("todos")
    .Produces<Todo>()
    .Produces<ProblemDto>(404)
    .WithSecurity("ApiKey");

Post("/todos", handler)
    .Accepts<CreateTodo>()            // requestBody schema
    .AcceptsQuery<TodoListQuery>()    // optional on GET list routes
    .Produces<Todo>(201);
```

## Schema generation

Reflection subset:

- primitives, enums, arrays, nested DTOs
- NRT-driven `required`
- **Cycles throw** at document build with a clear type name

```csharp
// Produces<object>() → free-form inline object schema
```

Self-referential DTOs are unsupported by design — project a non-recursive shape.

## UI

Setting `UiPath` maps a small HTML page that loads Scalar from a CDN and points at `DocumentPath`. No extra NuGet UI package.

## See also

- [routing.md](routing.md)
- [results.md](results.md)

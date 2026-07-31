# OpenAPI

Core builds an OpenAPI 3 document from the **`RouteTable`** + route metadata. The host serves it.

## Host

```csharp
ElsieApp.Create(args)
    .Module<TodosModule>()
    .OpenApi(o =>
    {
        o.Info.Title = "My API";
        o.Info.Description = "…";
        o.Info.Version = "v1";
        o.DocumentPath = "/openapi.json"; // default
        o.UiPath = "/scalar";             // optional UI page
        o.UseScalarCdn = true;            // false → minimal embedded HTML (no CDN)
    })
    .Run();
```

Document JSON is baked once when the server starts (unless prebuilt — below).

## Route metadata

```csharp
Get("/todos/{id:guid}", …)
    .Named("getTodo")
    .AcceptsQuery<SearchQuery>()
    .Produces<Todo>()
    .WithSummary("Get a todo")
    .WithTags("todos")
    .WithSecurity("ApiKey")
    .WithExample(new Todo(/* … */));

Post("/todos", …)
    .Accepts<CreateTodo>()
    .Produces<Todo>(201);
```

## Prebuilt document (trim / AOT friendly)

Skip reflection at runtime:

```csharp
// offline / CI:
await ElsieOpenApiDocument.WriteToFileAsync("openapi.json", routeTable, info);

// host:
.OpenApi(o =>
{
    o.PrebuiltDocumentPath = "openapi.json";
    // or: o.PrebuiltDocumentUtf8 = File.ReadAllBytes("openapi.json");
    o.UiPath = "/scalar";
})
```

## See also

- [routing.md](routing.md)
- [modules.md](modules.md)
- [hosting-and-aot.md](hosting-and-aot.md)

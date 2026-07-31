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
        o.UiPath = "/scalar";             // optional Scalar CDN page
    })
    .Run();
```

Document JSON is baked once when the server starts.

## Route metadata

```csharp
Get("/todos/{id:guid}", …)
    .Named("getTodo")
    .AcceptsQuery<SearchQuery>()
    .Produces<Todo>()
    .WithSummary("Get a todo")
    .WithTags("todos")
    .WithSecurity("ApiKey");

Post("/todos", …)
    .Accepts<CreateTodo>()
    .Produces<Todo>(201);
```

## See also

- [routing.md](routing.md)
- [modules.md](modules.md)

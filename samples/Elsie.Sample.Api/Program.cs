using System.Collections.Concurrent;
using Elsie;
using Elsie.Web;
using Elsie.Sample.Api;

// -----------------------------------------------------------------------------
// Advanced ASP.NET sample — multi-module API.
//
//   GET  /                         catalog
//   GET  /health
//   GET  /docs/{*path}             catch-all
//   GET  /api/todos?q=&done=
//   GET  /api/todos/{id:guid}
//   POST /api/todos                JSON { "title": "..." }  + header X-Api-Key: dev-secret
//   PUT  /api/todos/{id}           JSON { "title": "...", "done": true }
//   PATCH /api/todos/{id}          JSON { "done": true }    (partial)
//   DELETE /api/todos/{id}
//
// Path/Group, DI, BindJsonAsync, MapException, module Before gate,
// app After headers, OpenAPI (+ optional Scalar UI).
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ITodoStore, InMemoryTodoStore>();
builder.Services.AddSingleton<IRequestClock, SystemRequestClock>();
builder.AddElsie(o =>
{
    o.ScanEntryAssembly = false; // explicit modules only
    o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
    o.MapException<ArgumentException>((_, ex) => ElsieResult.BadRequest(ex.Message));
    o.ExceptionHandler = (ctx, ex, _) =>
    {
        ctx.Response.Headers["X-Elsie-Error"] = ex.GetType().Name;
        return Task.FromResult(ElsieResult.Problem(500, "Internal Server Error"));
    };
});
builder.Services.AddElsieModule<HomeModule>();
builder.Services.AddElsieModule<TodosModule>();
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddBefore((ctx, _) =>
    {
        if (string.IsNullOrEmpty(ctx.Request.GetHeader("X-Request-Id")))
        {
            ctx.Response.Headers["X-Request-Id"] = Guid.NewGuid().ToString("n");
        }
        else
        {
            ctx.Response.Headers["X-Request-Id"] = ctx.Request.GetHeader("X-Request-Id")!;
        }

        return Task.FromResult<ElsieResult?>(null);
    });
    p.AddAfter((ctx, result) =>
    {
        ctx.Response.Headers["X-Elsie-Sample"] = "api";
        ctx.Response.Headers["X-Elsie-Status"] = result.StatusCode.ToString();
        return result;
    });
});

var app = builder.Build();

var store = app.Services.GetRequiredService<ITodoStore>();
store.Add("Try Elsie BindJsonAsync");
store.Add("Ship host-agnostic core");

app.MapElsieOpenApi(o =>
{
    o.Info.Title = "Elsie Sample API";
    o.Info.Description = "Todos demo — mutating routes need X-Api-Key: dev-secret";
    o.Info.Version = "v1";
    o.UiPath = "/scalar";
});
app.MapElsie();
app.Run();

namespace Elsie.Sample.Api
{
    public sealed record Todo(Guid Id, string Title, bool Done, DateTimeOffset UpdatedAt);
    public sealed record CreateTodo(string Title);
    public sealed record UpdateTodo(string Title, bool Done);
    public sealed record PatchTodo(bool? Done, string? Title);
    public sealed record TodoListQuery(string? Q, bool? Done);
    public sealed record TodoList(IReadOnlyList<Todo> Items, TodoListQuery Filter);

    public interface ITodoStore
    {
        IReadOnlyList<Todo> List(string? q, bool? done);
        Todo Get(Guid id);
        Todo Add(string title);
        Todo Update(Guid id, string title, bool done);
        Todo Patch(Guid id, string? title, bool? done);
        bool Delete(Guid id);
    }

    public interface IRequestClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemRequestClock : IRequestClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public sealed class InMemoryTodoStore : ITodoStore
    {
        private readonly ConcurrentDictionary<Guid, Todo> _items = new();
        private readonly IRequestClock _clock;

        public InMemoryTodoStore(IRequestClock clock) => _clock = clock;

        public IReadOnlyList<Todo> List(string? q, bool? done) =>
            _items.Values
                .Where(t => q is null || t.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Where(t => done is null || t.Done == done)
                .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        public Todo Get(Guid id) =>
            _items.TryGetValue(id, out var todo)
                ? todo
                : throw new KeyNotFoundException($"Todo '{id}' was not found.");

        public Todo Add(string title)
        {
            var todo = new Todo(Guid.NewGuid(), title, Done: false, _clock.UtcNow);
            _items[todo.Id] = todo;
            return todo;
        }

        public Todo Update(Guid id, string title, bool done)
        {
            if (!_items.ContainsKey(id))
            {
                throw new KeyNotFoundException($"Todo '{id}' was not found.");
            }

            var todo = new Todo(id, title, done, _clock.UtcNow);
            _items[id] = todo;
            return todo;
        }

        public Todo Patch(Guid id, string? title, bool? done)
        {
            var current = Get(id);
            var next = new Todo(
                id,
                title is null ? current.Title : title,
                done ?? current.Done,
                _clock.UtcNow);
            _items[id] = next;
            return next;
        }

        public bool Delete(Guid id) => _items.TryRemove(id, out _);
    }

    public sealed class HomeModule : ElsieModule
    {
        public HomeModule()
        {
            Get("/", ctx => ElsieResult.Json(new
            {
                name = "Elsie Sample API",
                path = ctx.Request.Path,
                method = ctx.Request.Method,
                links = new
                {
                    health = "/health",
                    todos = "/api/todos",
                    docs = "/docs/getting-started",
                    openapi = "/openapi.json",
                    scalar = "/scalar",
                    note = "Mutating /api/todos/* requires header X-Api-Key: dev-secret"
                }
            })).WithTags("catalog");

            Get("/health", ctx =>
            {
                var clock = ctx.GetRequiredService<IRequestClock>();
                return ctx.Json(new { status = "ok", at = clock.UtcNow });
            }).WithTags("health");

            Get("/docs/{*path}", ctx =>
                ElsieResult.Text($"Doc path: '{ctx.RouteOrDefault("path")}' (method {ctx.Request.Method})"))
                .WithTags("docs");
        }
    }

    public sealed class TodosModule : ElsieModule
    {
        public TodosModule(ITodoStore store)
        {
            Path("/api");

            // Sample keeps mutating-only gate; RequireApiKey defaults to all methods.
            Before(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));

            Group("/todos", () =>
            {
                Get("/", ctx =>
                {
                    var q = ctx.QueryOrDefault("q");
                    bool? done = ctx.TryQuery<bool>("done", out var d) ? d : null;
                    var filter = new TodoListQuery(q, done);
                    return ctx.Json(new TodoList(store.List(q, done), filter));
                })
                .Named("listTodos")
                .AcceptsQuery<TodoListQuery>()
                .Produces<TodoList>()
                .WithSummary("List todos")
                .WithTags("todos");

                Get("/{id:guid}", ctx =>
                {
                    if (!ctx.RequireRoute("id", out Guid id, out var error))
                    {
                        return error!;
                    }

                    return ctx.Json(store.Get(id));
                })
                .Named("getTodo")
                .Produces<Todo>()
                .WithTags("todos");

                Post("/", async (ctx, ct) =>
                {
                    var bind = await ctx.BindJsonAsync<CreateTodo>(ct);
                    if (!bind.IsSuccess)
                    {
                        return bind.Error!;
                    }

                    var title = bind.Value!.Title?.Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        return ElsieResult.BadRequest("Title is required.");
                    }

                    var created = store.Add(title);
                    return ElsieResult.Created(ctx.UrlFor("getTodo", new { id = created.Id }), created);
                })
                .Accepts<CreateTodo>()
                .Produces<Todo>(201)
                .WithSecurity("ApiKey")
                .WithTags("todos");

                Put("/{id:guid}", async (ctx, ct) =>
                {
                    if (!ctx.RequireRoute("id", out Guid id, out var error))
                    {
                        return error!;
                    }

                    var bind = await ctx.BindJsonAsync<UpdateTodo>(ct);
                    if (!bind.IsSuccess)
                    {
                        return bind.Error!;
                    }

                    var title = bind.Value!.Title?.Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        return ElsieResult.BadRequest("Title is required.");
                    }

                    return ctx.Json(store.Update(id, title, bind.Value.Done));
                })
                .Accepts<UpdateTodo>()
                .Produces<Todo>()
                .WithSecurity("ApiKey")
                .WithTags("todos");

                Patch("/{id:guid}", async (ctx, ct) =>
                {
                    if (!ctx.RequireRoute("id", out Guid id, out var error))
                    {
                        return error!;
                    }

                    var bind = await ctx.BindJsonAsync<PatchTodo>(ct);
                    if (!bind.IsSuccess)
                    {
                        return bind.Error!;
                    }

                    var body = bind.Value!;
                    if (body.Done is null && body.Title is null)
                    {
                        return ElsieResult.BadRequest("Provide at least one of: title, done.");
                    }

                    var title = body.Title?.Trim();
                    if (body.Title is not null && string.IsNullOrWhiteSpace(title))
                    {
                        return ElsieResult.BadRequest("Title must not be empty.");
                    }

                    return ctx.Json(store.Patch(id, title, body.Done));
                })
                .Accepts<PatchTodo>()
                .Produces<Todo>()
                .WithSecurity("ApiKey")
                .WithTags("todos");

                Delete("/{id:guid}", ctx =>
                {
                    if (!ctx.RequireRoute("id", out Guid id, out var error))
                    {
                        return error!;
                    }

                    return store.Delete(id)
                        ? ElsieResult.NoContent()
                        : ElsieResult.NotFound($"Todo '{id}' was not found.");
                })
                .WithSecurity("ApiKey")
                .WithTags("todos");

                Get("/_boom", _ => throw new InvalidOperationException("Demonstrating ExceptionHandler"))
                    .WithTags("debug");
            });
        }
    }
}

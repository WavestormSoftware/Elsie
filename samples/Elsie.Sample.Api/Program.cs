using System.Collections.Concurrent;
using Elsie;
using Elsie.AspNetCore;
using Elsie.Sample.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ITodoStore, InMemoryTodoStore>();
builder.Services.AddElsie(o =>
{
    // Prefer explicit modules in real apps; scan is fine for small samples.
    o.ScanEntryAssembly = false;
    o.ExceptionHandler = (ctx, ex, _) =>
    {
        if (ex is KeyNotFoundException)
        {
            return Task.FromResult(ElsieResult.NotFound(ex.Message));
        }

        return Task.FromResult(ElsieResult.Problem(
            statusCode: 500,
            title: "Server Error",
            detail: builder.Environment.IsDevelopment() ? ex.Message : "An unexpected error occurred."));
    };
});
builder.Services.AddElsieModule<HomeModule>();
builder.Services.AddElsieModule<TodosModule>();
builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddAfter((ctx, _) => ctx.Response.Headers["X-Elsie-Sample"] = "api");
});

var app = builder.Build();
app.MapElsie();
app.Run();

namespace Elsie.Sample.Api
{
    public sealed record Todo(Guid Id, string Title, bool Done);
    public sealed record CreateTodo(string Title);
    public sealed record UpdateTodo(string Title, bool Done);

    public interface ITodoStore
    {
        IReadOnlyList<Todo> List(string? q, bool? done);
        Todo Get(Guid id);
        Todo Add(string title);
        Todo Update(Guid id, string title, bool done);
        bool Delete(Guid id);
    }

    public sealed class InMemoryTodoStore : ITodoStore
    {
        private readonly ConcurrentDictionary<Guid, Todo> _items = new();

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
            var todo = new Todo(Guid.NewGuid(), title, Done: false);
            _items[todo.Id] = todo;
            return todo;
        }

        public Todo Update(Guid id, string title, bool done)
        {
            if (!_items.ContainsKey(id))
            {
                throw new KeyNotFoundException($"Todo '{id}' was not found.");
            }

            var todo = new Todo(id, title, done);
            _items[id] = todo;
            return todo;
        }

        public bool Delete(Guid id) => _items.TryRemove(id, out _);
    }

    /// <summary>Public unauthenticated routes.</summary>
    public sealed class HomeModule : ElsieModule
    {
        public HomeModule()
        {
            Get("/", () => ElsieResult.Json(new
            {
                name = "Elsie Sample API",
                links = new
                {
                    health = "/health",
                    todos = "/api/todos",
                    note = "Write routes require header X-Api-Key: dev-secret"
                }
            }));

            Get("/health", () => ElsieResult.Json(new { status = "ok" }));

            // Catch-all demo for static-ish paths under /docs
            Get("/docs/{*path}", ctx =>
                ElsieResult.Text($"Doc path: {ctx.RouteOrDefault("path") ?? ""}"));
        }
    }

    /// <summary>
    /// Advanced module: path prefix, groups, DI, JSON bind, query filters, module Before gate.
    /// </summary>
    public sealed class TodosModule : ElsieModule
    {
        public TodosModule(ITodoStore store)
        {
            Path("/api");

            // Module-level gate for mutating verbs under /api/todos
            Before(ctx =>
            {
                if (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                {
                    return null;
                }

                if (!ctx.Request.Path.StartsWithSegments("/api/todos"))
                {
                    return null;
                }

                var key = ctx.Request.Headers["X-Api-Key"].ToString();
                return key == "dev-secret"
                    ? null
                    : ElsieResult.Unauthorized("Provide header X-Api-Key: dev-secret");
            });

            Group("/todos", () =>
            {
                Get("/", ctx =>
                {
                    var q = ctx.QueryOrDefault("q");
                    bool? done = ctx.TryGetQueryBool("done", out var d) ? d : null;
                    return ctx.Json(store.List(q, done));
                });

                Get("/{id:guid}", ctx =>
                {
                    if (!ctx.RequireRouteGuid("id", out var id, out var error))
                    {
                        return error!;
                    }

                    return ctx.Json(store.Get(id));
                });

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
                    return ctx.Json(created, statusCode: 201)
                        .WithHeader("Location", $"/api/todos/{created.Id}");
                });

                Put("/{id:guid}", async (ctx, ct) =>
                {
                    if (!ctx.RequireRouteGuid("id", out var id, out var error))
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
                });

                Delete("/{id:guid}", ctx =>
                {
                    if (!ctx.RequireRouteGuid("id", out var id, out var error))
                    {
                        return error!;
                    }

                    return store.Delete(id)
                        ? ElsieResult.NoContent()
                        : ElsieResult.NotFound($"Todo '{id}' was not found.");
                });
            });
        }
    }
}

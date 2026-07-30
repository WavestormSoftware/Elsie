using System.Collections.Concurrent;
using Elsie;
using Elsie.AspNetCore;
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
// Path/Group, DI, BindJsonAsync, problem results, ExceptionHandler,
// module Before gate, app After headers, OpenAPI (/openapi.json).
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ITodoStore, InMemoryTodoStore>();
builder.Services.AddSingleton<IRequestClock, SystemRequestClock>();
builder.AddElsie(o =>
{
    o.ScanEntryAssembly = false; // explicit modules only
    o.ExceptionHandler = (ctx, ex, _) =>
    {
        ctx.Response.Headers["X-Elsie-Error"] = ex.GetType().Name;

        if (ex is KeyNotFoundException)
        {
            return Task.FromResult(ElsieResult.NotFound(ex.Message));
        }

        if (ex is ArgumentException arg)
        {
            return Task.FromResult(ElsieResult.BadRequest(arg.Message));
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
    p.AddBefore((ctx, _) =>
    {
        // Correlation id on every request (before hooks can short-circuit by returning a result).
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
    });
});

var app = builder.Build();

// Seed a couple todos so GET /api/todos is interesting on first run.
var store = app.Services.GetRequiredService<ITodoStore>();
store.Add("Try Elsie BindJsonAsync");
store.Add("Ship host-agnostic core");

// OpenAPI before MapElsie so /openapi.json is not swallowed.
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "Elsie Sample API";
    o.Info.Description = "Todos demo — mutating routes need X-Api-Key: dev-secret";
});
app.MapElsie();
app.Run();

namespace Elsie.Sample.Api
{
    public sealed record Todo(Guid Id, string Title, bool Done, DateTimeOffset UpdatedAt);
    public sealed record CreateTodo(string Title);
    public sealed record UpdateTodo(string Title, bool Done);
    public sealed record PatchTodo(bool? Done, string? Title);

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

    /// <summary>Public unauthenticated routes.</summary>
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
                    note = "Mutating /api/todos/* requires header X-Api-Key: dev-secret"
                }
            }));

            Get("/health", ctx =>
            {
                var clock = ctx.GetRequiredService<IRequestClock>();
                return ctx.Json(new { status = "ok", at = clock.UtcNow });
            });

            // Catch-all — concrete routes win when both match.
            Get("/docs/{*path}", ctx =>
                ElsieResult.Text($"Doc path: '{ctx.RouteOrDefault("path")}' (method {ctx.Request.Method})"));
        }
    }

    /// <summary>
    /// Path/Group prefixes, ctor DI, JSON bind, query filters, API-key Before gate, PATCH.
    /// </summary>
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
                    var q = ctx.QueryOrDefault("q") ?? ctx.Request.GetQuery("q");
                    bool? done = ctx.TryGetQueryBool("done", out var d) ? d : null;
                    return ctx.Json(new
                    {
                        items = store.List(q, done),
                        filter = new { q, done }
                    });
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

                Patch("/{id:guid}", async (ctx, ct) =>
                {
                    if (!ctx.RequireRouteGuid("id", out var id, out var error))
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

                // Deliberate boom — ExceptionHandler maps to problem+json
                Get("/_boom", _ => throw new InvalidOperationException("Demonstrating ExceptionHandler"));
            });
        }
    }
}

using System.Collections.Concurrent;
using Elsie;
using Elsie.AspNetCore;
using Elsie.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;

// Elsie API template — CRUD + cookie auth + OpenAPI
//   GET  /                  catalog
//   POST /login             { "user":"ada", "password":"pass" }
//   POST /logout
//   GET  /api/todos         requires auth
//   POST /api/todos         requires auth
//   GET  /openapi.json

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ITodoStore, InMemoryTodoStore>();
builder.AddElsie(o => o.ScanEntryAssembly = false);
builder.Services.AddElsieAuth(o =>
{
    o.Cookie = c =>
    {
        c.Cookie.Name = "elsie-auth";
        c.Cookie.HttpOnly = true;
        c.SlidingExpiration = true;
    };
});
builder.Services.AddElsieModule<PublicModule>();
builder.Services.AddElsieModule<TodosModule>();

var app = builder.Build();

app.UseElsieAuth();
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "ElsieApi";
    o.Info.Description = "CRUD + cookie auth sample from dotnet new elsie-api";
});
app.MapElsie();
app.Run();

sealed record Todo(Guid Id, string Title, bool Done);
sealed record CreateTodo(string Title);
sealed record LoginBody(string User, string Password);

interface ITodoStore
{
    IReadOnlyList<Todo> List();
    Todo Add(string title);
}

sealed class InMemoryTodoStore : ITodoStore
{
    private readonly ConcurrentDictionary<Guid, Todo> _items = new();

    public IReadOnlyList<Todo> List() =>
        _items.Values.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToArray();

    public Todo Add(string title)
    {
        var todo = new Todo(Guid.NewGuid(), title, Done: false);
        _items[todo.Id] = todo;
        return todo;
    }
}

sealed class PublicModule : ElsieModule
{
    public PublicModule()
    {
        Get("/", () => ElsieResult.Json(new
        {
            name = "ElsieApi",
            links = new { login = "/login", todos = "/api/todos", openapi = "/openapi.json" }
        }));

        Post("/login", async (ctx, ct) =>
        {
            var bind = await ctx.BindJsonAsync<LoginBody>(ct);
            if (!bind.IsSuccess)
            {
                return bind.Error!;
            }

            var body = bind.Value!;
            if (body.User != "ada" || body.Password != "pass")
            {
                return ElsieResult.Unauthorized("Invalid credentials.");
            }

            await ctx.SignInCookieAsync(body.User, roles: ["user"]);
            return ElsieResult.NoContent();
        });

        Post("/logout", async (ctx, _) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return ElsieResult.NoContent();
        });
    }
}

sealed class TodosModule : ElsieModule
{
    public TodosModule(ITodoStore store)
    {
        Path("/api");
        Before(ElsieAuthGates.RequireAuthenticated());

        Group("/todos", () =>
        {
            Get("/", ctx =>
            {
                var user = ctx.GetUser();
                return ctx.Json(new
                {
                    user = user.Identity?.Name,
                    items = store.List()
                });
            }).WithSummary("List todos").WithTags("todos");

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
            }).Accepts<CreateTodo>().Produces<Todo>(201).WithTags("todos");
        });
    }
}

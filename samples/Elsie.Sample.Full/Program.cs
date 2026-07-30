using System.Collections.Concurrent;
using System.Security.Claims;
using Elsie;
using Elsie.Web;
using Elsie.Auth;
using Elsie.Cors;
using Elsie.HealthChecks;
using Elsie.RateLimiting;
using Elsie.Sample.Full;
using Elsie.Views;
using Microsoft.AspNetCore.Authentication.Cookies;

// -----------------------------------------------------------------------------
// Kitchen-sink sample — auth + cors + rate limit + health + static + views.
//
//   GET  /                     Liquid home
//   GET  /assets/app.css       static files
//   GET  /healthz[/live|/ready]
//   POST /login                { "user":"ada", "password":"pass" }
//   POST /logout
//   GET  /me                   requires cookie auth
//   GET  /api/notes            requires auth + rate limit
//   POST /api/notes            requires auth + rate limit
//   GET  /openapi.json  |  /scalar
//
//   dotnet run --project samples/Elsie.Sample.Full
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<INoteStore, InMemoryNoteStore>();

builder.AddElsie(o =>
{
    o.ScanEntryAssembly = false;
    o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
    o.MapException<ArgumentException>((_, ex) => ElsieResult.BadRequest(ex.Message));
});

builder.Services.AddElsieAuth(o =>
{
    o.Cookie = c =>
    {
        c.Cookie.Name = "elsie-full";
        c.Cookie.HttpOnly = true;
        c.SlidingExpiration = true;
        c.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    };
});

builder.Services.AddElsieCors(o =>
{
    o.AddDefaultPolicy(p => p
        .AllowOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
        .AllowHeaders("Content-Type", "Authorization", "X-Request-Id")
        .AllowCredentials()
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
});

builder.Services.AddElsieHealthChecks(o =>
{
    o.AddCheck("self", () => ElsieHealthCheckResult.Healthy("process up"), ElsieHealthCheckTags.Live);
    o.AddCheck("notes", () => ElsieHealthCheckResult.Healthy("in-memory store"), ElsieHealthCheckTags.Ready);
});

builder.Services.AddElsieViews(o =>
{
    o.ContentRoot = builder.Environment.ContentRootPath;
    o.ReloadOnChange = builder.Environment.IsDevelopment();
});

builder.Services.AddElsieModule<HomeModule>();
builder.Services.AddElsieModule<AuthModule>();
builder.Services.AddElsieModule<MeModule>();
builder.Services.AddElsieModule<NotesModule>();

builder.Services.ConfigureElsiePipelines(p =>
{
    p.AddBefore((ctx, _) =>
    {
        var id = ctx.Request.GetHeader("X-Request-Id");
        ctx.Response.Headers["X-Request-Id"] = string.IsNullOrEmpty(id)
            ? Guid.NewGuid().ToString("n")
            : id!;
        return Task.FromResult<ElsieResult?>(null);
    });
    p.AddAfter((ctx, result) =>
    {
        ctx.Response.Headers["X-Elsie-Sample"] = "full";
        return result;
    });
});

var app = builder.Build();

app.UseElsieCors();
app.UseElsieAuth();

app.MapElsieStaticFiles("/assets", Path.Combine(app.Environment.ContentRootPath, "wwwroot"));
app.MapElsieOpenApi(o =>
{
    o.Info.Title = "Elsie Full Sample";
    o.Info.Description = "Auth + CORS + rate limit + health + static + views";
    o.Info.Version = "v1";
    o.UiPath = "/scalar";
});
app.MapElsie();
app.Run();

namespace Elsie.Sample.Full
{
    public sealed record Note(Guid Id, string Title, string Owner, DateTimeOffset CreatedAt);
    public sealed record NoteList(string Owner, IReadOnlyList<Note> Items);
    public sealed record CreateNote(string Title);
    public sealed record LoginBody(string User, string Password);

    public interface INoteStore
    {
        IReadOnlyList<Note> ListFor(string owner);
        Note Add(string owner, string title);
    }

    public sealed class InMemoryNoteStore : INoteStore
    {
        private readonly ConcurrentDictionary<Guid, Note> _items = new();

        public IReadOnlyList<Note> ListFor(string owner) =>
            _items.Values
                .Where(n => string.Equals(n.Owner, owner, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(n => n.CreatedAt)
                .ToArray();

        public Note Add(string owner, string title)
        {
            var note = new Note(Guid.NewGuid(), title, owner, DateTimeOffset.UtcNow);
            _items[note.Id] = note;
            return note;
        }
    }

    public sealed class HomeModule : ElsieModule
    {
        public HomeModule()
        {
            Get("/", async (ctx, ct) =>
            {
                var user = ctx.GetUser();
                return await ctx.ViewAsync(
                    "home",
                    new
                    {
                        Title = "Elsie Full",
                        Name = user.Identity?.IsAuthenticated == true
                            ? user.Identity.Name
                            : "guest",
                        Authenticated = user.Identity?.IsAuthenticated == true
                    },
                    cancellationToken: ct);
            }).WithSummary("Home page").WithTags("pages");

            Get("/api", () => ElsieResult.Json(new
            {
                name = "Elsie Full Sample",
                links = new
                {
                    home = "/",
                    me = "/me",
                    notes = "/api/notes",
                    login = "/login",
                    healthz = "/healthz",
                    assets = "/assets/app.css",
                    openapi = "/openapi.json",
                    scalar = "/scalar"
                },
                demo = new
                {
                    user = "ada",
                    password = "pass",
                    note = "Cookie session after POST /login; notes are rate-limited."
                }
            })).WithTags("catalog");
        }
    }

    public sealed class AuthModule : ElsieModule
    {
        public AuthModule()
        {
            Post("/login", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<LoginBody>(ct);
                if (!bind.IsSuccess)
                {
                    return bind.Error!;
                }

                var body = bind.Value!;
                if (!string.Equals(body.User, "ada", StringComparison.Ordinal) ||
                    !string.Equals(body.Password, "pass", StringComparison.Ordinal))
                {
                    return ElsieResult.Unauthorized("Invalid credentials.");
                }

                await ctx.SignInCookieAsync(body.User, roles: ["user"]);
                return ElsieResult.NoContent();
            }).Accepts<LoginBody>().WithTags("auth");

            Post("/logout", async (ctx, _) =>
            {
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return ElsieResult.NoContent();
            }).WithTags("auth");
        }
    }

    public sealed class MeModule : ElsieModule
    {
        public MeModule()
        {
            Before(ElsieAuthGates.RequireAuthenticated());

            Get("/me", ctx =>
            {
                var user = ctx.GetUser();
                return ctx.Json(new
                {
                    name = user.Identity?.Name,
                    authenticated = true,
                    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray()
                });
            })
            .WithSummary("Current principal")
            .WithTags("auth");
        }
    }

    /// <summary>
    /// Authenticated notes API with a fixed-window rate limit (per remote IP).
    /// </summary>
    public sealed class NotesModule : ElsieModule
    {
        public NotesModule(INoteStore store)
        {
            Path("/api");
            Before(ElsieAuthGates.RequireAuthenticated());
            Before(ElsieRateLimit.FixedWindow(permitLimit: 30, window: TimeSpan.FromMinutes(1)));

            Group("/notes", () =>
            {
                Get("/", ctx =>
                {
                    var owner = ctx.GetUser().Identity?.Name ?? "anonymous";
                    return ctx.Json(new NoteList(owner, store.ListFor(owner)));
                })
                .Named("listNotes")
                .WithSummary("List notes for the signed-in user")
                .WithTags("notes")
                .Produces<NoteList>();

                Post("/", async (ctx, ct) =>
                {
                    var bind = await ctx.BindJsonAsync<CreateNote>(ct);
                    if (!bind.IsSuccess)
                    {
                        return bind.Error!;
                    }

                    var title = bind.Value!.Title?.Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        return ElsieResult.BadRequest("Title is required.");
                    }

                    var owner = ctx.GetUser().Identity?.Name
                        ?? throw new InvalidOperationException("Authenticated user missing name.");
                    var created = store.Add(owner, title);
                    var location = ctx.UrlFor("listNotes"); // collection URL; single-item not registered
                    return ElsieResult.Created(location, created);
                })
                .Accepts<CreateNote>()
                .Produces<Note>(201)
                .WithTags("notes");
            });
        }
    }
}

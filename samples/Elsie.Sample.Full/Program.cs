using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Elsie;
using Elsie.Web;
using Elsie.Auth;
using Elsie.Cors;
using Elsie.HealthChecks;
using Elsie.RateLimiting;
using Elsie.Sample.Full;
using Elsie.Validation;
using Elsie.Views;

// -----------------------------------------------------------------------------
// Kitchen-sink sample — auth + cors + rate limit + health + static + views +
// compression + validation + security headers.
//
//   GET  /                     Liquid home
//   GET  /assets/app.css       static files
//   GET  /healthz[/live|/ready]
//   POST /login                { "user":"ada", "password":"pass" } + X-CSRF-TOKEN
//   POST /logout               + X-CSRF-TOKEN (from GET /csrf)
//   GET  /csrf                 issue antiforgery cookie + token
//   GET  /me                   requires cookie auth
//   GET  /api/notes            requires auth + rate limit
//   POST /api/notes            requires auth + rate limit + validation
//   GET  /openapi.json  |  /scalar
//
//   dotnet run --project samples/Elsie.Sample.Full
// -----------------------------------------------------------------------------

var contentRoot = ResolveContentRoot();
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

static string ResolveContentRoot()
{
    var cwd = Directory.GetCurrentDirectory();
    if (Directory.Exists(Path.Combine(cwd, "Views")))
    {
        return cwd;
    }

    var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    return Directory.Exists(Path.Combine(project, "Views")) ? project : cwd;
}

ElsieApp.Create(args)
    .ContentRoot(contentRoot)
    .Logging(loggerFactory)
    .Compression()
    .Configure(o =>
    {
        o.ScanEntryAssembly = false;
        o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
        o.MapException<ArgumentException>((_, ex) => ElsieResult.BadRequest(ex.Message));
    })
    .Module<HomeModule>()
    .Module<AuthModule>()
    .Module<MeModule>()
    .Module<NotesModule>()
    .Services(s =>
    {
        s.AddSingleton<INoteStore, InMemoryNoteStore>();
        s.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions
            {
                CookieName = "elsie-full",
                HttpOnly = true,
                SlidingExpiration = true
            };
            o.Cookie.TicketKeyFromString("elsie-full-sample-dev-key");
        });
        s.AddElsieAntiforgery();
        s.AddElsieDataAnnotationsValidation();
        s.AddElsieCors(o =>
        {
            o.AddDefaultPolicy(p => p
                .AllowOrigins("http://localhost:5173", "http://127.0.0.1:5173")
                .AllowMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
                .AllowHeaders("Content-Type", "Authorization", "X-Request-Id", "X-CSRF-TOKEN")
                .AllowCredentials()
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
        });
        s.AddElsieHealthChecks(o =>
        {
            o.AddCheck("self", () => ElsieHealthCheckResult.Healthy("process up"), ElsieHealthCheckTags.Live);
            o.AddCheck("notes", () => ElsieHealthCheckResult.Healthy("in-memory store"), ElsieHealthCheckTags.Ready);
        });
        s.AddElsieViews(o =>
        {
            o.ContentRoot = contentRoot;
            o.ReloadOnChange = true;
        });
        s.ConfigureElsiePipelines(p =>
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
            p.AddAfter(ElsieSecurityHeaders.DefaultAfter());
        });
    })
    .StaticFiles(s =>
    {
        s.Root = "wwwroot";
        s.RequestPath = "/assets";
    })
    .OpenApi(o =>
    {
        o.Info.Title = "Elsie Full Sample";
        o.Info.Description = "Auth + CORS + rate limit + health + static + views + CSRF + validation";
        o.Info.Version = "v1";
        o.UiPath = "/scalar";
    })
    .Run();

namespace Elsie.Sample.Full
{
    public sealed record Note(Guid Id, string Title, string Owner, DateTimeOffset CreatedAt);
    public sealed record NoteList(string Owner, IReadOnlyList<Note> Items);

    public sealed class CreateNote
    {
        [Required, MinLength(1), MaxLength(200)]
        public string Title { get; set; } = "";
    }

    public sealed class LoginBody
    {
        [Required]
        public string User { get; set; } = "";

        [Required]
        public string Password { get; set; } = "";
    }

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
                    csrf = "/csrf",
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
                    note = "GET /csrf then send X-CSRF-TOKEN on POST /login and mutations; notes are rate-limited."
                }
            })).WithTags("catalog");
        }
    }

    public sealed class AuthModule : ElsieModule
    {
        public AuthModule()
        {
            Before(ElsieAntiforgeryService.RequireAntiforgery());

            Get("/csrf", ctx =>
            {
                var token = ctx.GetAntiforgeryToken();
                return ctx.Json(new { token, header = "X-CSRF-TOKEN" });
            }).WithSummary("Issue antiforgery cookie + token").WithTags("auth");

            Post("/login", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<LoginBody>(ct);
                if (!bind.IsSuccess)
                {
                    return bind.Error!;
                }

                var body = bind.Value!;
                if (ctx.ValidateWithDataAnnotations(body) is { } invalid)
                {
                    return invalid;
                }

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
                await ctx.SignOutAsync();
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
            Before(ElsieAntiforgeryService.RequireAntiforgery());
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

                    var body = bind.Value!;
                    if (ctx.ValidateWithDataAnnotations(body) is { } invalid)
                    {
                        return invalid;
                    }

                    var title = body.Title.Trim();
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

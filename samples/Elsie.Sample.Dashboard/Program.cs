using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Elsie;
using Elsie.Auth;
using Elsie.Sample.Dashboard;
using Elsie.Validation;
using Elsie.Views;
using Elsie.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// -----------------------------------------------------------------------------
// Multi-page dashboard sample — Fluid views + cookie auth + form CSRF + validation.
//
//   GET  /                     marketing home
//   GET  /login  POST /login   form auth (+ antiforgery)
//   GET  /register POST /register
//   POST /logout
//   GET  /dashboard            overview (auth)
//   GET  /dashboard/activity   recent activity (auth)
//   GET  /dashboard/settings   profile settings (auth)
//   GET  /assets/app.css
//
//   Seed user: ada@elsie.dev / pass
//   dotnet run --project samples/Elsie.Sample.Dashboard
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

    // bin/Release/netX.0 → project dir when launched via dll
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
        o.MapException<ArgumentException>((_, ex) => ElsieResult.BadRequest(ex.Message));
    })
    .Module<HomeModule>()
    .Module<AccountModule>()
    .Module<DashboardModule>()
    .Services(s =>
    {
        s.AddSingleton<IUserStore, InMemoryUserStore>();
        s.AddSingleton<IActivityStore, InMemoryActivityStore>();
        s.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions
            {
                CookieName = "elsie-dashboard",
                HttpOnly = true,
                SlidingExpiration = true
            };
            o.Cookie.TicketKeyFromString("elsie-dashboard-sample-dev-key");
        });
        s.AddElsieAntiforgery();
        s.AddElsieDataAnnotationsValidation();
        s.AddElsieViews(o =>
        {
            o.ContentRoot = contentRoot;
            o.ReloadOnChange = true;
        });
        s.ConfigureElsiePipelines(p => p.AddAfter(ElsieSecurityHeaders.DefaultAfter()));
    })
    .StaticFiles(s =>
    {
        s.Root = "wwwroot";
        s.RequestPath = "/assets";
    })
    .Run();

namespace Elsie.Sample.Dashboard
{
    public sealed record UserAccount(string Email, string DisplayName, string PasswordHash, DateTimeOffset CreatedAt);

    public sealed class LoginForm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Required]
        public string Password { get; set; } = "";

        public string? ReturnUrl { get; set; }
    }

    public sealed class RegisterForm
    {
        [Required, EmailAddress]
        public string Email { get; set; } = "";

        [Required, MinLength(1)]
        public string DisplayName { get; set; } = "";

        [Required, MinLength(4)]
        public string Password { get; set; } = "";

        [Required]
        public string ConfirmPassword { get; set; } = "";
    }

    public sealed class SettingsForm
    {
        [Required, MinLength(1)]
        public string DisplayName { get; set; } = "";
    }

    public interface IUserStore
    {
        bool TryGet(string email, out UserAccount user);
        bool TryAdd(string email, string displayName, string password, out string? error);
        bool Validate(string email, string password, out UserAccount? user);
        bool TryUpdateDisplayName(string email, string displayName, out UserAccount? user);
    }

    public sealed class InMemoryUserStore : IUserStore
    {
        private readonly ConcurrentDictionary<string, UserAccount> _users =
            new(StringComparer.OrdinalIgnoreCase);

        public InMemoryUserStore()
        {
            _users["ada@elsie.dev"] = new UserAccount(
                "ada@elsie.dev",
                "Ada Lovelace",
                HashPassword("pass"),
                DateTimeOffset.UtcNow.AddDays(-14));
        }

        public bool TryGet(string email, out UserAccount user) =>
            _users.TryGetValue(email.Trim(), out user!);

        public bool TryAdd(string email, string displayName, string password, out string? error)
        {
            email = email.Trim();
            displayName = displayName.Trim();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                error = "Enter a valid email.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                error = "Display name is required.";
                return false;
            }

            if (password.Length < 4)
            {
                error = "Password must be at least 4 characters.";
                return false;
            }

            var account = new UserAccount(email, displayName, HashPassword(password), DateTimeOffset.UtcNow);
            if (!_users.TryAdd(email, account))
            {
                error = "An account with that email already exists.";
                return false;
            }

            error = null;
            return true;
        }

        public bool Validate(string email, string password, out UserAccount? user)
        {
            if (!_users.TryGetValue(email.Trim(), out var found))
            {
                user = null;
                return false;
            }

            // Demo sample only — fixed-time compare of SHA-256 hex (not a production KDF).
            var ok = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(found.PasswordHash),
                Encoding.UTF8.GetBytes(HashPassword(password)));
            user = ok ? found : null;
            return ok;
        }

        public bool TryUpdateDisplayName(string email, string displayName, out UserAccount? user)
        {
            displayName = displayName.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                user = null;
                return false;
            }

            if (!_users.TryGetValue(email, out var existing))
            {
                user = null;
                return false;
            }

            user = existing with { DisplayName = displayName };
            _users[email] = user;
            return true;
        }

        private static string HashPassword(string password)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("elsie-dashboard:" + password));
            return Convert.ToHexString(hash);
        }
    }

    public sealed record ActivityItem(DateTimeOffset At, string Kind, string Message);

    public interface IActivityStore
    {
        IReadOnlyList<ActivityItem> ListFor(string email, int take = 20);
        void Add(string email, string kind, string message);
    }

    public sealed class InMemoryActivityStore : IActivityStore
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<ActivityItem>> _items =
            new(StringComparer.OrdinalIgnoreCase);

        public InMemoryActivityStore()
        {
            Add("ada@elsie.dev", "system", "Welcome to the Elsie dashboard sample.");
            Add("ada@elsie.dev", "note", "Seeded demo activity for Ada.");
        }

        public IReadOnlyList<ActivityItem> ListFor(string email, int take = 20)
        {
            if (!_items.TryGetValue(email, out var queue))
            {
                return [];
            }

            return queue.Reverse().Take(take).ToArray();
        }

        public void Add(string email, string kind, string message)
        {
            var queue = _items.GetOrAdd(email, static _ => new ConcurrentQueue<ActivityItem>());
            queue.Enqueue(new ActivityItem(DateTimeOffset.UtcNow, kind, message));
            while (queue.Count > 50 && queue.TryDequeue(out _))
            {
            }
        }
    }

    internal static class PageAuth
    {
        /// <summary>Redirect anonymous browsers to login (keeps returnUrl).</summary>
        public static Func<ElsieContext, ElsieResult?> RequirePageUser() => ctx =>
        {
            if (ctx.GetUser().Identity?.IsAuthenticated == true)
            {
                return null;
            }

            var returnUrl = ctx.Request.Path + ctx.Request.QueryString;
            return ElsieResult.Redirect("/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        };

        public static string? CurrentEmail(ElsieContext ctx) =>
            ctx.GetUser().Identity?.Name;

        public static string SafeReturnUrl(string? returnUrl) =>
            !string.IsNullOrWhiteSpace(returnUrl)
            && returnUrl.StartsWith('/')
            && !returnUrl.StartsWith("//", StringComparison.Ordinal)
                ? returnUrl
                : "/dashboard";
    }

    public sealed class HomeModule : ElsieModule
    {
        public HomeModule()
        {
            // Logout form lives in layout when signed in.
            Before(ElsieAntiforgeryService.RequireAntiforgery());

            Get("/", async (ctx, ct) =>
            {
                var user = ctx.GetUser();
                var authed = user.Identity?.IsAuthenticated == true;
                return await ctx.ViewAsync(
                    "home",
                    new
                    {
                        Title = "Elsie Dashboard",
                        Authenticated = authed,
                        UserName = authed ? user.Identity!.Name : null,
                        Active = "home",
                        CsrfToken = ctx.GetAntiforgeryToken()
                    },
                    cancellationToken: ct);
            }).WithSummary("Marketing home").WithTags("pages");
        }
    }

    public sealed class AccountModule : ElsieModule
    {
        public AccountModule(IUserStore users, IActivityStore activity)
        {
            Before(ElsieAntiforgeryService.RequireAntiforgery());

            Get("/login", async (ctx, ct) =>
            {
                if (ctx.GetUser().Identity?.IsAuthenticated == true)
                {
                    return ElsieResult.Redirect("/dashboard");
                }

                var returnUrl = ctx.Request.GetQuery("returnUrl");
                return await ctx.ViewAsync(
                    "login",
                    new
                    {
                        Title = "Sign in",
                        Authenticated = false,
                        Active = "login",
                        Error = (string?)null,
                        Email = "",
                        ReturnUrl = returnUrl ?? "",
                        CsrfToken = ctx.GetAntiforgeryToken()
                    },
                    cancellationToken: ct);
            }).WithTags("auth");

            Post("/login", async (ctx, ct) =>
            {
                var bind = await ctx.BindFormAsync<LoginForm>(ct);
                if (!bind.IsSuccess)
                {
                    return bind.Error!;
                }

                var form = bind.Value!;
                if (ctx.ValidateWithDataAnnotations(form) is { } invalid)
                {
                    return await ctx.ViewAsync(
                        "login",
                        new
                        {
                            Title = "Sign in",
                            Authenticated = false,
                            Active = "login",
                            Error = "Enter a valid email and password.",
                            Email = form.Email,
                            ReturnUrl = form.ReturnUrl ?? "",
                            CsrfToken = ctx.GetAntiforgeryToken()
                        },
                        cancellationToken: ct);
                }

                if (!users.Validate(form.Email, form.Password, out var account) || account is null)
                {
                    return await ctx.ViewAsync(
                        "login",
                        new
                        {
                            Title = "Sign in",
                            Authenticated = false,
                            Active = "login",
                            Error = "Invalid email or password.",
                            Email = form.Email,
                            ReturnUrl = form.ReturnUrl ?? "",
                            CsrfToken = ctx.GetAntiforgeryToken()
                        },
                        cancellationToken: ct);
                }

                await ctx.SignInCookieAsync(account.Email, roles: ["user"]);
                activity.Add(account.Email, "auth", "Signed in.");
                return ElsieResult.Redirect(PageAuth.SafeReturnUrl(form.ReturnUrl));
            }).WithTags("auth");

            Get("/register", async (ctx, ct) =>
            {
                if (ctx.GetUser().Identity?.IsAuthenticated == true)
                {
                    return ElsieResult.Redirect("/dashboard");
                }

                return await ctx.ViewAsync(
                    "register",
                    new
                    {
                        Title = "Create account",
                        Authenticated = false,
                        Active = "register",
                        Error = (string?)null,
                        Email = "",
                        DisplayName = "",
                        CsrfToken = ctx.GetAntiforgeryToken()
                    },
                    cancellationToken: ct);
            }).WithTags("auth");

            Post("/register", async (ctx, ct) =>
            {
                var bind = await ctx.BindFormAsync<RegisterForm>(ct);
                if (!bind.IsSuccess)
                {
                    return bind.Error!;
                }

                var form = bind.Value!;
                if (ctx.ValidateWithDataAnnotations(form) is { } invalid)
                {
                    return await RegisterError(ctx, form, "Check the form fields and try again.", ct);
                }

                if (!string.Equals(form.Password, form.ConfirmPassword, StringComparison.Ordinal))
                {
                    return await RegisterError(ctx, form, "Passwords do not match.", ct);
                }

                if (!users.TryAdd(form.Email, form.DisplayName, form.Password, out var error))
                {
                    return await RegisterError(ctx, form, error ?? "Could not create account.", ct);
                }

                await ctx.SignInCookieAsync(form.Email.Trim(), roles: ["user"]);
                activity.Add(form.Email.Trim(), "auth", "Account created.");
                return ElsieResult.Redirect("/dashboard");
            }).WithTags("auth");

            Post("/logout", async (ctx, _) =>
            {
                var email = PageAuth.CurrentEmail(ctx);
                await ctx.SignOutAsync();
                if (email is not null)
                {
                    activity.Add(email, "auth", "Signed out.");
                }

                return ElsieResult.Redirect("/");
            }).WithTags("auth");
        }

        private static Task<ElsieResult> RegisterError(
            ElsieContext ctx,
            RegisterForm form,
            string error,
            CancellationToken ct) =>
            ctx.ViewAsync(
                "register",
                new
                {
                    Title = "Create account",
                    Authenticated = false,
                    Active = "register",
                    Error = error,
                    Email = form.Email,
                    DisplayName = form.DisplayName,
                    CsrfToken = ctx.GetAntiforgeryToken()
                },
                cancellationToken: ct);
    }

    public sealed class DashboardModule : ElsieModule
    {
        public DashboardModule(IUserStore users, IActivityStore activity)
        {
            Path("/dashboard");
            Before(PageAuth.RequirePageUser());
            Before(ElsieAntiforgeryService.RequireAntiforgery());

            Get("/", async (ctx, ct) =>
            {
                var email = PageAuth.CurrentEmail(ctx)!;
                users.TryGet(email, out var account);
                var items = activity.ListFor(email, take: 5);
                return await ctx.ViewAsync(
                    "dashboard/index",
                    new
                    {
                        Title = "Overview",
                        Authenticated = true,
                        Active = "overview",
                        UserName = email,
                        DisplayName = account?.DisplayName ?? email,
                        CsrfToken = ctx.GetAntiforgeryToken(),
                        Stats = new
                        {
                            Projects = 3,
                            TasksOpen = 7,
                            ActivityCount = activity.ListFor(email).Count
                        },
                        Recent = items.Select(a => new
                        {
                            a.Kind,
                            a.Message,
                            At = a.At.ToString("u")
                        }).ToArray()
                    },
                    cancellationToken: ct);
            }).WithSummary("Dashboard overview").WithTags("dashboard");

            Get("/activity", async (ctx, ct) =>
            {
                var email = PageAuth.CurrentEmail(ctx)!;
                users.TryGet(email, out var account);
                var items = activity.ListFor(email, take: 20);
                return await ctx.ViewAsync(
                    "dashboard/activity",
                    new
                    {
                        Title = "Activity",
                        Authenticated = true,
                        Active = "activity",
                        UserName = email,
                        DisplayName = account?.DisplayName ?? email,
                        CsrfToken = ctx.GetAntiforgeryToken(),
                        Items = items.Select(a => new
                        {
                            a.Kind,
                            a.Message,
                            At = a.At.ToString("u")
                        }).ToArray()
                    },
                    cancellationToken: ct);
            }).WithTags("dashboard");

            Get("/settings", async (ctx, ct) =>
            {
                var email = PageAuth.CurrentEmail(ctx)!;
                users.TryGet(email, out var account);
                return await ctx.ViewAsync(
                    "dashboard/settings",
                    new
                    {
                        Title = "Settings",
                        Authenticated = true,
                        Active = "settings",
                        UserName = email,
                        DisplayName = account?.DisplayName ?? email,
                        Email = email,
                        CsrfToken = ctx.GetAntiforgeryToken(),
                        Message = (string?)null,
                        Error = (string?)null
                    },
                    cancellationToken: ct);
            }).WithTags("dashboard");

            Post("/settings", async (ctx, ct) =>
            {
                var email = PageAuth.CurrentEmail(ctx)!;
                var bind = await ctx.BindFormAsync<SettingsForm>(ct);
                if (!bind.IsSuccess)
                {
                    return bind.Error!;
                }

                var form = bind.Value!;
                if (ctx.ValidateWithDataAnnotations(form) is { } invalid
                    || !users.TryUpdateDisplayName(email, form.DisplayName, out var account)
                    || account is null)
                {
                    return await ctx.ViewAsync(
                        "dashboard/settings",
                        new
                        {
                            Title = "Settings",
                            Authenticated = true,
                            Active = "settings",
                            UserName = email,
                            DisplayName = form.DisplayName,
                            Email = email,
                            CsrfToken = ctx.GetAntiforgeryToken(),
                            Message = (string?)null,
                            Error = "Display name cannot be empty."
                        },
                        cancellationToken: ct);
                }

                activity.Add(email, "settings", "Updated display name.");
                return await ctx.ViewAsync(
                    "dashboard/settings",
                    new
                    {
                        Title = "Settings",
                        Authenticated = true,
                        Active = "settings",
                        UserName = email,
                        DisplayName = account.DisplayName,
                        Email = email,
                        CsrfToken = ctx.GetAntiforgeryToken(),
                        Message = "Saved.",
                        Error = (string?)null
                    },
                    cancellationToken: ct);
            }).WithTags("dashboard");
        }
    }
}

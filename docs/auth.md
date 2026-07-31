# Auth

Two layers:

1. **`ElsieAuth`** (core) — header / API-key / bearer-string before-hooks (no crypto)
2. **`Elsie.Auth`** — cookie session tickets + JWT validation + principal gates

## Header / API key (core)

```csharp
Before(ElsieAuth.RequireApiKey("dev-secret", onlyMutatingMethods: true));
Before(ElsieAuth.RequireHeader("X-Tenant", "acme"));
Before(ElsieAuth.RequireBearer(token => token == "ok"));
Before(ElsieAuth.RequireCookie("sid")); // cookie present
```

## Cookie + JWT package

```bash
dotnet add package Elsie.Auth
```

```csharp
ElsieApp.Create(args)
    .Module<App>()
    .Services(s =>
    {
        s.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions
            {
                CookieName = "elsie-auth",
                HttpOnly = true,
                SlidingExpiration = true,
                ExpireTimeSpan = TimeSpan.FromHours(8)
            };
            o.Cookie.TicketKeyFromString(Environment.GetEnvironmentVariable("ELSIE_TICKET_KEY")
                ?? "change-me-in-production");

            // Optional JWT (requires SigningKey)
            // o.JwtBearer = new ElsieJwtBearerOptions
            // {
            //     Issuer = "https://issuer",
            //     Audience = "api",
            //     SigningKey = new SymmetricSecurityKey(keyBytes)
            // };
        });
    })
    .Run();
```

Or fluent: `.Auth(o => { ... })` via `ElsieAuthAppExtensions`.

The host attaches a principal before dispatch (`IElsiePrincipalAttacher`): JWT bearer header first when configured, otherwise cookie ticket.

## Gates

```csharp
Before(ElsieAuthGates.RequireAuthenticated());
Before(ElsieAuthGates.RequireRole("admin"));
Before(ElsieAuthGates.RequireClaim(ClaimTypes.Name, "ada"));
```

## Sign-in / sign-out

```csharp
await ctx.SignInCookieAsync("ada", roles: ["user"]);
await ctx.SignInAsync(principal);
await ctx.SignOutAsync();

var user = ctx.GetUser(); // ClaimsPrincipal via ElsiePrincipal
```

Cookie tickets are AES-GCM sealed (name/role claims + expiry). Set a stable `TicketKey` in production.

## See also

- [pipelines-and-errors.md](pipelines-and-errors.md)
- [hosting-and-aot.md](hosting-and-aot.md)

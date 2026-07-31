# Auth

Two layers:

1. **`ElsieAuth`** (core) — header / API-key / bearer-string before-hooks (no crypto)
2. **`Elsie.Auth`** — cookie session tickets + JWT validation + principal gates + antiforgery + minimal OIDC helpers

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
                Secure = true,                          // HTTPS
                SameSite = ElsieSameSite.Lax,           // Core enum (not ASP.NET)
                SlidingExpiration = true,
                ExpireTimeSpan = TimeSpan.FromHours(8)
            };
            o.Cookie.TicketKeyFromString(Environment.GetEnvironmentVariable("ELSIE_TICKET_KEY")
                ?? "change-me-in-production");

            // Optional JWT (requires SigningKey — Authority/OIDC metadata not in v1)
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

Cookie tickets are AES-GCM sealed (name/role claims + expiry).

**Production:** set a long random secret via `TicketKeyFromString` (≥ 16 chars) or a raw 32-byte `TicketKey`.  
**Local only:** `AllowInsecureDevelopmentKey = true` installs a well-known key (never ship that).

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

## Antiforgery

Double-submit cookie. Mutating requests need header **`X-CSRF-TOKEN`** **or** form field **`__RequestVerificationToken`** (urlencoded/multipart). Tokens are **Base64Url** (safe in forms). Body is buffered once and shared with `BindFormAsync` / `ReadFormAsync`.

```csharp
s.AddElsieAuth(...);
s.AddElsieAntiforgery(); // optional configure cookie name / SameSite

// module:
Before(ElsieAntiforgeryService.RequireAntiforgery());

// JSON SPA / API clients:
// GET a route that calls ctx.GetAntiforgeryToken() then send X-CSRF-TOKEN

// HTML forms:
var token = ctx.GetAntiforgeryToken();
// <input type="hidden" name="__RequestVerificationToken" value="…" />
```

See [Dashboard](../samples/Elsie.Sample.Dashboard) (form field) and [Full](../samples/Elsie.Sample.Full) (header + `GET /csrf`).

## OIDC (minimal helpers)

Not a full OIDC middleware stack — helpers only. Prefer **PKCE** + validated id_token:

```csharp
var state = ElsieOidc.CreateState();
var nonce = ElsieOidc.CreateNonce();
var verifier = ElsieOidc.CreateCodeVerifier();
var challenge = ElsieOidc.CreateCodeChallenge(verifier);

var options = new ElsieOidcOptions
{
    Authority = "https://idp.example",
    ClientId = "app",
    RedirectUri = "https://app.example/callback",
    // ClientSecret optional for public clients using PKCE
};

var url = ElsieOidc.BuildAuthorizeUrl(options, state, nonce, codeChallenge: challenge);
// redirect browser to url; on callback verify state, then:
var tokens = await ElsieOidc.ExchangeCodeAsync(http, options, code, codeVerifier: verifier, ct);
var principal = ElsieOidc.PrincipalFromIdToken(
    tokens.IdToken,
    jwtOptions,                 // must set SigningKey for signature validation
    expectedNonce: nonce);
// Unvalidated payload parse is opt-in only:
// ElsieOidc.PrincipalFromIdToken(idToken, jwt: null, allowUnvalidated: true); // DEV ONLY
```

## See also

- [pipelines-and-errors.md](pipelines-and-errors.md)
- [security.md](security.md)
- [hosting-and-aot.md](hosting-and-aot.md)

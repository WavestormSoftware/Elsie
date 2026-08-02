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
                Secure = true,                          // DEFAULT true (strict) — set false for plain-HTTP dev only
                SameSite = ElsieSameSite.Lax,           // Core enum (not ASP.NET)
                SlidingExpiration = true,
                ExpireTimeSpan = TimeSpan.FromHours(8)  // default 8h
                // CookiePrefix = "__Host-"             // enforces Secure + Path=/ + no Domain at startup
            };
            o.Cookie.TicketKeyFromString(Environment.GetEnvironmentVariable("ELSIE_TICKET_KEY")
                ?? "change-me-in-production");

            // Optional JWT — static key or OIDC discovery (JWKS):
            // o.JwtBearer = new ElsieJwtBearerOptions
            // {
            //     Authority = "https://issuer",        // OIDC discovery → JWKS (24h auto-refresh, rollover-safe)
            //     Audience = "api"
            // };
            // …or a static key (no network):
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

The host attaches a principal before dispatch (`IElsiePrincipalAttacher`): JWT bearer header first when configured, otherwise cookie.

**Cookies.** By default the client-side v1 ticket is used (AES-GCM sealed name/role claims + expiry). When `o.SessionStore` is set, cookies become opaque **v2 session ids** (≥ 128-bit) and the principal lives server-side with sliding TTL renewal on every request; `SignOutAsync` removes the store entry and clears the cookie.

**JWT / JWKS.** With only `Authority` (or `JwksUrl`) configured, signing keys are discovered from the OIDC metadata / JWKS endpoint: `ConfigurationManager`-backed caching, refresh on `JwksRefreshInterval` (default 24 h), previous keys kept during rollover, and an unreachable authority fails validation (→ 401) without ever crashing the request. `AllowHttpMetadata = true` permits plain-HTTP endpoints (dev/tests only); `ValidateIssuerSigningKey` is on by default so unknown `kid` values are rejected.

**Production:** set a long random secret via `TicketKeyFromString` (≥ 16 chars) or a raw 32-byte `TicketKey`.
**Local only:** `AllowInsecureDevelopmentKey = true` installs a well-known key (never ship that).

## Gates

```csharp
Before(ElsieAuthGates.RequireAuthenticated());
Before(ElsieAuthGates.RequireRole("admin"));
Before(ElsieAuthGates.RequireClaim(ClaimTypes.Name, "ada"));
Before(ElsieAuthGates.RequirePolicy("admin")); // named policy, see below
```

When auth options are configured the gates shape their responses through Challenge/Forbid:

- anonymous → `ctx.Challenge()` → JWT: **401 + `WWW-Authenticate: Bearer`**; cookie with `ChallengeLoginPath`: **302 → login path**; otherwise plain 401
- authenticated but unauthorized → `ctx.Forbid()` → **302 → `ForbidAccessDeniedPath`** (cookie) or plain **403**

## Named policies

```csharp
s.AddElsieAuth(o =>
{
    o.Cookie = …;
    o.AddElsiePolicy("admin", p => p.RequireRole("admin"));
    o.AddElsiePolicy("staff", p => p.RequireRole("staff", "admin").RequireClaim("tenant", "acme"));
});
// module:
Before(ElsieAuthGates.RequirePolicy("admin"));
```

Policies are `IReadOnlyList<Func<ClaimsPrincipal, bool>>` requirements with `RequireRole` / `RequireClaim` / `AddRequirement` shortcuts. An unknown policy name throws at **startup** (module constructors run during app build). For deterministic multi-app setups use `ElsieAuthGates.RequirePolicy(options, name)`.

## Sessions

```csharp
o.SessionStore = new InMemoryElsieSessionStore();   // bounded ~100k, sliding TTL, eviction
// or the Redis package (Elsie.Extensions.Auth.Redis) for multi-instance deployments
```

With a store configured, `SignInAsync` writes the principal to the store and emits a v2 opaque cookie; `SignOutAsync` removes the entry. The default remains the client-side v1 encrypted ticket.

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

> Note: the Redis-backed session store (`Elsie.Extensions.Auth.Redis`, planned) plugs into the same `IElsieSessionStore` seam for multi-instance deployments; the in-memory store ships with this phase.

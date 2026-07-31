# Security notes

Elsie is a small HTTP stack. Treat production like any other custom host.

## Defaults that matter

| Setting | Default | Production guidance |
|---------|---------|---------------------|
| Cookie `TicketKey` | Required (or dev flag) | Long random secret; never commit |
| `AllowInsecureDevelopmentKey` | off unless you set it | **Never** in prod |
| Cookie / antiforgery `SameSite` | `ElsieSameSite.Lax` / `Strict` | Use `None` + `Secure` only for cross-site HTTPS |
| `UseForwardedHeaders` | **false** | Enable only behind a trusted proxy |
| Rate-limit partition | **RemoteIp only** | Use `ForwardedPartitionKey` only with trusted XFF |
| `MaxRequestBodyBytes` | 10 MiB | Lower for APIs that don’t need big posts |
| Static files | path-safe | Keep roots outside secrets |

## Cookie sessions

- Tickets are **AES-GCM** sealed claims + expiry.
- `TicketKeyFromString` requires ≥ **16** characters (SHA-256 → 32-byte key).
- Set `Secure = true` and `SameSite = ElsieSameSite.Lax|Strict|None` for HTTPS.

## Headers / keys

- `ElsieAuth.RequireApiKey` / `RequireHeader` use constant-time compare.
- Prefer TLS (proxy or Elsie HTTPS) for anything sensitive.
- Baseline browser headers: `ElsieSecurityHeaders.DefaultAfter()` after-hook.

## Reverse proxy

```csharp
.Server(o =>
{
    o.UseForwardedHeaders = true; // only if proxy strips untrusted X-Forwarded-*
    o.MaxRequestBodyBytes = 1_000_000;
    o.MaxConcurrentConnections = 10_000;
    o.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
})
```

Recommended: terminate TLS on the proxy; Elsie listens on loopback HTTP/1.1.

## Antiforgery

Browser cookie apps should register `AddElsieAntiforgery` and `Before(ElsieAntiforgeryService.RequireAntiforgery())` on mutating routes. Double-submit cookie + `X-CSRF-TOKEN` header **or** form field `__RequestVerificationToken` (Base64Url tokens; see Dashboard sample).

## What we test

Automated tests cover (Web + Auth suites):

- Body over limit → **413**; oversized headers → client error
- Static path traversal / encoded `..` does not leak files
- Forwarded headers on/off + CRLF host rejection
- Cookie ticket tamper / wrong key / expired / garbage
- Short ticket secrets rejected; missing key without dev flag fails DI setup
- API-key gate rejects wrong keys
- HTTP/1.1 parser body/header limits
- 405 / 404 problem bodies do not leak handler data
- Antiforgery header + form field paths
- Static path directory-boundary (sibling root-prefix)
- Response header / cookie attribute / download-name CR/LF rejection
- Unsafe `X-Request-Id` values are not echoed

## CI / supply chain

Repo CI runs `dotnet list package --vulnerable` and packs all package IDs. Dependabot watches NuGet + GitHub Actions weekly (`.github/dependabot.yml`).

## Out of scope (for now)

- Full HTTP/2 adversarial fuzzing / h2spec CI
- Full OIDC middleware (helpers + PKCE only; `PrincipalFromIdToken` requires JWT validation unless `allowUnvalidated: true`)
- WAF / rate limit at the edge (use proxy + Elsie rate-limit hooks)

## See also

- [auth.md](auth.md)
- [production-checklist.md](production-checklist.md)
- [hosting-and-aot.md](hosting-and-aot.md)

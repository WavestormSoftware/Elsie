# Security notes

Elsie is a small HTTP stack. Treat production like any other custom host.

## Defaults that matter

| Setting | Default | Production guidance |
|---------|---------|---------------------|
| Cookie `TicketKey` | Required (or dev flag) | Long random secret; never commit |
| `AllowInsecureDevelopmentKey` | off unless you set it | **Never** in prod |
| `UseForwardedHeaders` | **false** | Enable only behind a trusted proxy |
| `MaxRequestBodyBytes` | 10 MiB | Lower for APIs that don’t need big posts |
| Static files | path-safe | Keep roots outside secrets |

## Cookie sessions

- Tickets are **AES-GCM** sealed claims + expiry.
- `TicketKeyFromString` requires ≥ **16** characters (SHA-256 → 32-byte key).
- Set `Secure = true` and `SameSite = None/Lax/Strict` appropriately for HTTPS.

## Headers / keys

- `ElsieAuth.RequireApiKey` / `RequireHeader` use constant-time compare.
- Prefer TLS (proxy or Elsie HTTPS) for anything sensitive.

## Reverse proxy

```csharp
.Server(o =>
{
    o.UseForwardedHeaders = true; // only if proxy strips untrusted X-Forwarded-*
    o.MaxRequestBodyBytes = 1_000_000;
})
```

Recommended: terminate TLS on the proxy; Elsie listens on loopback HTTP/1.1.

## What we test

Automated tests cover:

- Body over limit → **413**
- Static path traversal does not leak files
- Forwarded headers on/off behavior
- Tampered cookie tickets → unauthenticated
- Short ticket secrets rejected

## Out of scope (for now)

- Full HTTP/2 adversarial fuzzing / h2spec CI
- Automated dependency CVE scanning in CI (run `dotnet list package --vulnerable` in your pipeline)
- WAF / rate limit at the edge (use proxy + Elsie rate-limit hooks)

## See also

- [auth.md](auth.md)
- [hosting-and-aot.md](hosting-and-aot.md)

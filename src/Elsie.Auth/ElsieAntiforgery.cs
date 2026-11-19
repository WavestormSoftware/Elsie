using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.Auth;

/// <summary>Double-submit cookie antiforgery for browser cookie-auth apps.</summary>
public sealed class ElsieAntiforgeryOptions
{
    public string CookieName { get; set; } = "elsie-csrf";
    public string HeaderName { get; set; } = "X-CSRF-TOKEN";
    public string FormFieldName { get; set; } = "__RequestVerificationToken";
    public bool HttpOnly { get; set; }

    /// <summary>
    /// Emit <c>Secure</c> on the antiforgery cookie. Defaults to <b>true</b>; set false only for
    /// plain-HTTP local development (the token would otherwise never be stored).
    /// </summary>
    public bool Secure { get; set; } = true;

    public ElsieSameSite SameSite { get; set; } = ElsieSameSite.Strict;

    /// <summary>
    /// How long a minted token stays valid. Default 2 hours. Tokens are also bound to the current
    /// principal, so a token cannot be reused after a sign-in/sign-out or across users.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>HMAC key; defaults to cookie TicketKey when available.</summary>
    public byte[]? SigningKey { get; set; }
}

public static class ElsieAntiforgeryServiceExtensions
{
    public static IServiceCollection AddElsieAntiforgery(
        this IServiceCollection services,
        Action<ElsieAntiforgeryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ElsieAntiforgeryOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddSingleton<ElsieAntiforgeryService>();
        return services;
    }
}

public sealed class ElsieAntiforgeryService
{
    private static readonly byte[] EphemeralDevelopmentKey = RandomNumberGenerator.GetBytes(32);
    private static readonly object TokenItemKey = new();

    private readonly ElsieAntiforgeryOptions _options;
    private readonly IServiceProvider _services;

    public ElsieAntiforgeryService(ElsieAntiforgeryOptions options, IServiceProvider services)
    {
        _options = options;
        _services = services;
    }

    public string GetAndStoreToken(ElsieContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // One token per request: repeated calls (layout + partials) must not append duplicate
        // Set-Cookie headers or mint tokens the page then mixes up.
        if (ctx.Request.Items.TryGetValue(TokenItemKey, out var cached) && cached is string existing)
        {
            return existing;
        }

        var token = CreateToken(ctx);
        ctx.Request.Items[TokenItemKey] = token;
        ctx.Response.SetCookie(_options.CookieName, token, new ElsieCookieOptions
        {
            HttpOnly = _options.HttpOnly,
            Secure = _options.Secure,
            SameSite = _options.SameSite,
            Path = "/"
        });
        return token;
    }

    /// <summary>Validate header double-submit (sync). Prefer <see cref="IsValidAsync"/> for form fields.</summary>
    public bool IsValid(ElsieContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var cookie = ctx.Request.GetCookie(_options.CookieName);
        if (string.IsNullOrEmpty(cookie) || !VerifyFormat(cookie, ctx))
        {
            return false;
        }

        var header = ctx.Request.GetHeader(_options.HeaderName);
        return !string.IsNullOrEmpty(header) && FixedTimeEquals(header, cookie);
    }

    /// <summary>
    /// Validate double-submit via header <c>X-CSRF-TOKEN</c> or form field <c>__RequestVerificationToken</c>.
    /// Form path buffers the body once (shared with later <c>BindFormAsync</c> / <c>ReadFormAsync</c>).
    /// </summary>
    public async Task<bool> IsValidAsync(ElsieContext ctx, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var cookie = ctx.Request.GetCookie(_options.CookieName);
        if (string.IsNullOrEmpty(cookie) || !VerifyFormat(cookie, ctx))
        {
            return false;
        }

        var header = ctx.Request.GetHeader(_options.HeaderName);
        if (!string.IsNullOrEmpty(header) && FixedTimeEquals(header, cookie))
        {
            return true;
        }

        var contentType = ctx.Request.ContentType ?? string.Empty;
        if (contentType.Length == 0
            || (!contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var form = await ctx.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (!form.IsSuccess)
        {
            return false;
        }

        using var collection = form.Value!;
        var field = collection.GetField(_options.FormFieldName);
        return !string.IsNullOrEmpty(field) && FixedTimeEquals(field, cookie);
    }

    /// <summary>
    /// Returns an <see cref="IElsieMiddleware"/> that forbids (403) mutating requests without
    /// a valid CSRF token (header or form field) and lets safe methods through. Register with
    /// <c>app.Use(...)</c> / <c>Module.Use(...)</c>.
    /// </summary>
    public static IElsieMiddleware RequireAntiforgery() => new AntiforgeryGate();

    private sealed class AntiforgeryGate : IElsieMiddleware
    {
        public async Task InvokeAsync(ElsieContext ctx, ElsieMiddlewareDelegate next)
        {
            ArgumentNullException.ThrowIfNull(ctx);
            if (!IsSafe(ctx.Request.Method))
            {
                var svc = ctx.GetService<ElsieAntiforgeryService>();
                if (svc is null)
                {
                    ctx.Result = ElsieResult.Problem(500, "Misconfigured", "Antiforgery is not registered.");
                    return;
                }

                if (!await svc.IsValidAsync(ctx, ctx.RequestAborted).ConfigureAwait(false))
                {
                    ctx.Result = ElsieResult.Forbidden("Antiforgery token missing or invalid.");
                    return;
                }
            }

            await next(ctx);
        }
    }

    private string CreateToken(ElsieContext ctx)
    {
        // nonce(32) | issuedAtUnixSeconds(8), authenticated with HMAC over the principal binding.
        var material = new byte[40];
        RandomNumberGenerator.Fill(material.AsSpan(0, 32));
        BinaryPrimitives.WriteInt64BigEndian(material.AsSpan(32), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var mac = ComputeMac(material, ctx);
        // Base64Url — safe in cookies and application/x-www-form-urlencoded (no '+').
        return ToBase64Url(material) + "." + ToBase64Url(mac);
    }

    private bool VerifyFormat(string token, ElsieContext ctx)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            var material = FromBase64Url(parts[0]);
            if (material.Length != 40)
            {
                return false;
            }

            var issued = BinaryPrimitives.ReadInt64BigEndian(material.AsSpan(32));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lifetime = (long)_options.TokenLifetime.TotalSeconds;
            if (lifetime <= 0 || issued > now + 300 || now - issued > lifetime)
            {
                return false;
            }

            var mac = FromBase64Url(parts[1]);
            var expected = ComputeMac(material, ctx);
            return CryptographicOperations.FixedTimeEquals(mac, expected);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private byte[] ComputeMac(byte[] material, ElsieContext ctx)
    {
        var binding = PrincipalBinding(ctx);
        var buffer = new byte[material.Length + binding.Length];
        material.CopyTo(buffer, 0);
        binding.CopyTo(buffer, material.Length);
        return HMACSHA256.HashData(ResolveKey(), buffer);
    }

    /// <summary>Binds the token to the current principal (anonymous tokens cannot be replayed by a user).</summary>
    private static byte[] PrincipalBinding(ElsieContext ctx)
    {
        var user = ctx.GetUser();
        var name = user.Identity?.IsAuthenticated == true ? user.Identity.Name ?? string.Empty : string.Empty;
        return SHA256.HashData(Encoding.UTF8.GetBytes($"elsie-csrf-v2|{name}"));
    }

    private static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => Convert.FromBase64String(s + "=="),
            3 => Convert.FromBase64String(s + "="),
            0 => Convert.FromBase64String(s),
            _ => throw new FormatException("Invalid Base64Url length.")
        };
    }

    private byte[] ResolveKey()
    {
        if (_options.SigningKey is { Length: > 0 } key)
        {
            return key;
        }

        var auth = _services.GetService<ElsieAuthOptions>();
        if (auth?.Cookie?.TicketKey is { Length: > 0 } ticket)
        {
            return ticket;
        }

        // Ephemeral process key — ok for single-node dev and avoids a globally forgeable default.
        return EphemeralDevelopmentKey;
    }

    private static bool IsSafe(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)
        || method.Equals("TRACE", StringComparison.OrdinalIgnoreCase);

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}

public static class ElsieAntiforgeryContextExtensions
{
    public static string GetAntiforgeryToken(this ElsieContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.GetRequiredService<ElsieAntiforgeryService>().GetAndStoreToken(ctx);
    }
}

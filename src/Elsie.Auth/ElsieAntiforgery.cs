using System.Security.Cryptography;
using System.Text;
using Elsie.Pipelines;
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
    public bool Secure { get; set; }
    public ElsieSameSite SameSite { get; set; } = ElsieSameSite.Strict;

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
        var token = CreateToken();
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
        if (string.IsNullOrEmpty(cookie) || !VerifyFormat(cookie))
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
        if (string.IsNullOrEmpty(cookie) || !VerifyFormat(cookie))
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

    /// <summary>Forbidden when mutating method lacks valid CSRF token (header or form field).</summary>
    public static ElsieBeforeDelegate RequireAntiforgery()
    {
        return async (ctx, ct) =>
        {
            if (IsSafe(ctx.Request.Method))
            {
                return null;
            }

            var svc = ctx.GetService<ElsieAntiforgeryService>();
            if (svc is null)
            {
                return ElsieResult.Problem(500, "Misconfigured", "Antiforgery is not registered.");
            }

            return await svc.IsValidAsync(ctx, ct).ConfigureAwait(false)
                ? null
                : ElsieResult.Forbidden("Antiforgery token missing or invalid.");
        };
    }

    private string CreateToken()
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var key = ResolveKey();
        var mac = HMACSHA256.HashData(key, nonce);
        // Base64Url — safe in cookies and application/x-www-form-urlencoded (no '+').
        return ToBase64Url(nonce) + "." + ToBase64Url(mac);
    }

    private bool VerifyFormat(string token)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        try
        {
            var nonce = FromBase64Url(parts[0]);
            var mac = FromBase64Url(parts[1]);
            var expected = HMACSHA256.HashData(ResolveKey(), nonce);
            return CryptographicOperations.FixedTimeEquals(mac, expected);
        }
        catch
        {
            return false;
        }
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

        // ephemeral process key — ok for single-node dev
        return SHA256.HashData(Encoding.UTF8.GetBytes("elsie-csrf-dev-key-change-me!!"));
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

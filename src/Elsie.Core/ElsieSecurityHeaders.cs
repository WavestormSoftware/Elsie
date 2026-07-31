namespace Elsie;

/// <summary>Before-hook factories for common browser security response headers.</summary>
public static class ElsieSecurityHeaders
{
    /// <summary>
    /// Sets baseline headers on every response via after-hook:
    /// X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy (none).
    /// </summary>
    public static Func<ElsieContext, ElsieResult, ElsieResult> DefaultAfter(
        string frameOptions = "DENY",
        string referrerPolicy = "no-referrer",
        string? contentSecurityPolicy = null)
    {
        return (_, result) =>
        {
            result = result
                .WithHeader("X-Content-Type-Options", "nosniff")
                .WithHeader("X-Frame-Options", frameOptions)
                .WithHeader("Referrer-Policy", referrerPolicy)
                .WithHeader("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
            if (!string.IsNullOrWhiteSpace(contentSecurityPolicy))
            {
                result = result.WithHeader("Content-Security-Policy", contentSecurityPolicy);
            }

            return result;
        };
    }
}

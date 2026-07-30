namespace Elsie;

/// <summary>Small before-hook helpers for header / API-key gates.</summary>
public static class ElsieAuth
{
    /// <summary>
    /// Require <paramref name="headerName"/> equals <paramref name="expectedValue"/>.
    /// Returns 401 problem result when missing/wrong; null when allowed.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequireHeader(
        string headerName,
        string expectedValue,
        bool onlyMutatingMethods = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        ArgumentNullException.ThrowIfNull(expectedValue);

        return ctx =>
        {
            if (onlyMutatingMethods && IsSafeMethod(ctx.Request.Method))
            {
                return null;
            }

            var actual = ctx.Request.GetHeader(headerName);
            return string.Equals(actual, expectedValue, StringComparison.Ordinal)
                ? null
                : ElsieResult.Unauthorized($"Provide header {headerName}.");
        };
    }

    /// <summary>
    /// Require API key header (default <c>X-Api-Key</c>) on <b>all</b> methods by default.
    /// Pass <paramref name="onlyMutatingMethods"/> = <c>true</c> to skip GET/HEAD/OPTIONS (legacy).
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequireApiKey(
        string expectedKey,
        string headerName = "X-Api-Key",
        bool onlyMutatingMethods = false) =>
        RequireHeader(headerName, expectedKey, onlyMutatingMethods);

    /// <summary>
    /// Require <c>Authorization: Bearer …</c>. Optional <paramref name="validate"/> checks the token.
    /// JWT/signature validation stays with the host (ASP.NET auth) — this is a thin gate.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequireBearer(
        Func<string, bool>? validate = null,
        bool onlyMutatingMethods = false)
    {
        return ctx =>
        {
            if (onlyMutatingMethods && IsSafeMethod(ctx.Request.Method))
            {
                return null;
            }

            if (!TryGetBearerToken(ctx.Request, out var token))
            {
                return ElsieResult.Unauthorized("Bearer token required.");
            }

            return validate is null || validate(token)
                ? null
                : ElsieResult.Unauthorized("Invalid bearer token.");
        };
    }

    /// <summary>
    /// Require a named cookie. Optional <paramref name="validate"/> checks the cookie value.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequireCookie(
        string cookieName,
        Func<string, bool>? validate = null,
        bool onlyMutatingMethods = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cookieName);

        return ctx =>
        {
            if (onlyMutatingMethods && IsSafeMethod(ctx.Request.Method))
            {
                return null;
            }

            var value = ctx.Request.GetCookie(cookieName);
            if (string.IsNullOrEmpty(value))
            {
                return ElsieResult.Unauthorized($"Cookie '{cookieName}' is required.");
            }

            return validate is null || validate(value)
                ? null
                : ElsieResult.Unauthorized($"Cookie '{cookieName}' is invalid.");
        };
    }

    /// <summary>Parse <c>Authorization: Bearer …</c> token, or null when absent/malformed.</summary>
    public static bool TryGetBearerToken(ElsieRequest request, out string token)
    {
        ArgumentNullException.ThrowIfNull(request);
        token = string.Empty;
        var header = request.GetHeader("Authorization");
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = header[prefix.Length..].Trim();
        return token.Length > 0;
    }

    private static bool IsSafeMethod(string method) =>
        method is "GET" or "HEAD" or "OPTIONS";
}

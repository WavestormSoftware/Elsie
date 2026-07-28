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
    /// Require API key header (default <c>X-Api-Key</c>). Skips GET/HEAD/OPTIONS by default.
    /// </summary>
    public static Func<ElsieContext, ElsieResult?> RequireApiKey(
        string expectedKey,
        string headerName = "X-Api-Key",
        bool onlyMutatingMethods = true) =>
        RequireHeader(headerName, expectedKey, onlyMutatingMethods);

    private static bool IsSafeMethod(string method) =>
        method is "GET" or "HEAD" or "OPTIONS";
}

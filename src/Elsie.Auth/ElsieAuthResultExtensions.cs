namespace Elsie.Auth;

/// <summary>
/// <see cref="ElsieResult"/> helpers for authentication challenges and authorization failures.
/// Auth gates use these automatically when the corresponding options are configured.
/// </summary>
public static class ElsieAuthResultExtensions
{
    /// <summary>
    /// Produces an authentication challenge for the current request:
    /// JWT scheme → 401 with <c>WWW-Authenticate: Bearer</c>; cookie scheme with
    /// <see cref="ElsieAuthOptions.ChallengeLoginPath"/> → 302 redirect; otherwise a plain 401.
    /// </summary>
    public static ElsieResult Challenge(this ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.GetService<ElsieAuthOptions>();
        if (options?.JwtBearer is not null)
        {
            return ElsieResult.Unauthorized("Authentication required.")
                .WithHeader("WWW-Authenticate", "Bearer");
        }

        if (options?.Cookie is not null && !string.IsNullOrWhiteSpace(options.ChallengeLoginPath))
        {
            return ElsieResult.Redirect(options.ChallengeLoginPath!);
        }

        return ElsieResult.Unauthorized("Authentication required.");
    }

    /// <summary>
    /// Produces an authorization-failure result: 302 redirect to
    /// <see cref="ElsieAuthOptions.ForbidAccessDeniedPath"/> when cookie auth and that path are
    /// configured; otherwise a plain 403.
    /// </summary>
    public static ElsieResult Forbid(this ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.GetService<ElsieAuthOptions>();
        if (options?.Cookie is not null && !string.IsNullOrWhiteSpace(options.ForbidAccessDeniedPath))
        {
            return ElsieResult.Redirect(options.ForbidAccessDeniedPath!);
        }

        return ElsieResult.Forbidden("Access denied.");
    }
}

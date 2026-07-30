namespace Elsie;

/// <summary>
/// Mutable response bag for before/after hooks (headers + cookies).
/// Final status/body come from <see cref="ElsieResult"/>; cookies are appended as <c>Set-Cookie</c> at bake.
/// </summary>
public sealed class ElsieResponse
{
    private readonly List<string> _setCookies = [];

    public ElsieHeaders Headers { get; } = new();

    /// <summary>Pending <c>Set-Cookie</c> header values (appended at bake after result headers).</summary>
    public IReadOnlyList<string> SetCookies => _setCookies;

    public void SetCookie(string name, string value, ElsieCookieOptions? options = null)
    {
        _setCookies.Add(ElsieCookieFormatter.FormatSetCookie(name, value, options));
    }

    public void DeleteCookie(string name, ElsieCookieOptions? options = null)
    {
        _setCookies.Add(ElsieCookieFormatter.FormatDeleteCookie(name, options));
    }
}

namespace Elsie;

/// <summary>
/// Mutable response bag for middleware and handlers (headers + cookies + trailers).
/// Final status/body come from <see cref="ElsieResult"/>; cookies are appended as
/// <c>Set-Cookie</c> and trailers flow to HTTP/2 / HTTP/3 transports at bake.
/// </summary>
public sealed class ElsieResponse
{
    private readonly List<string> _setCookies = [];
    private readonly List<KeyValuePair<string, string>> _trailers = [];

    public ElsieHeaders Headers { get; } = new();

    /// <summary>Pending <c>Set-Cookie</c> header values (appended at bake after result headers).</summary>
    public IReadOnlyList<string> SetCookies => _setCookies;

    /// <summary>
    /// HTTP/2 / HTTP/3 response trailers (sent in a trailing HEADERS frame after the body,
    /// e.g. <c>grpc-status</c>). Ignored by HTTP/1.1.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Trailers => _trailers;

    public void SetCookie(string name, string value, ElsieCookieOptions? options = null)
    {
        _setCookies.Add(ElsieCookieFormatter.FormatSetCookie(name, value, options));
    }

    public void DeleteCookie(string name, ElsieCookieOptions? options = null)
    {
        _setCookies.Add(ElsieCookieFormatter.FormatDeleteCookie(name, options));
    }

    /// <summary>Add a response trailer (delivered after the body on HTTP/2 / HTTP/3).</summary>
    public ElsieResponse AddTrailer(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _trailers.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }
}

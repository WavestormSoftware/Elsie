namespace Elsie;

/// <summary>
/// Mutable response bag for before/after hooks (headers). Final status/body come from <see cref="ElsieResult"/>.
/// </summary>
public sealed class ElsieResponse
{
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    public IDictionary<string, string> Headers => _headers;
}

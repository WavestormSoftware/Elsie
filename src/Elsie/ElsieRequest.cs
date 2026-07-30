using System.Text;

namespace Elsie;

/// <summary>
/// Host-agnostic HTTP request facade.
/// Multi-value query/headers are the source of truth; <see cref="Query"/> / <see cref="Headers"/> are first-wins views.
/// </summary>
public sealed class ElsieRequest
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyValues =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<string> NoValues = Array.Empty<string>();

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _queryValues;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _headerValues;
    private readonly string? _queryStringRaw;
    private IReadOnlyDictionary<string, string>? _queryView;
    private IReadOnlyDictionary<string, string>? _headersView;
    private IDictionary<object, object?>? _items;
    private string? _queryString;
    private byte[]? _bufferedBody;

    public ElsieRequest(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        IReadOnlyDictionary<string, string>? headers = null,
        Stream? body = null,
        long? contentLength = null,
        string? contentType = null,
        IServiceProvider? requestServices = null,
        CancellationToken requestAborted = default,
        IDictionary<object, object?>? items = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? queryValues = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headerValues = null,
        string? scheme = null,
        string? host = null,
        string? pathBase = null,
        string? protocol = null,
        string? remoteIp = null,
        string? queryString = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        Method = method.ToUpperInvariant();
        Path = NormalizePath(path);
        _queryValues = queryValues ?? Promote(query);
        _headerValues = headerValues ?? Promote(headers);
        _items = items;
        _queryStringRaw = queryString;
        Body = body ?? Stream.Null;
        ContentLength = contentLength;
        ContentType = contentType;
        RequestServices = requestServices ?? EmptyServiceProvider.Instance;
        RequestAborted = requestAborted;
        Scheme = scheme;
        Host = host;
        PathBase = pathBase;
        Protocol = protocol;
        RemoteIp = remoteIp;
    }

    public string Method { get; }
    public string Path { get; }

    /// <summary>URI scheme (<c>http</c>/<c>https</c>), host-filled when available.</summary>
    public string? Scheme { get; }

    /// <summary>Host header / authority, host-filled when available.</summary>
    public string? Host { get; }

    /// <summary>Path base prefix stripped before routing, host-filled when available.</summary>
    public string? PathBase { get; }

    /// <summary>HTTP protocol string (e.g. <c>HTTP/1.1</c>), host-filled when available.</summary>
    public string? Protocol { get; }

    /// <summary>Remote client IP when the host provides it.</summary>
    public string? RemoteIp { get; }

    /// <summary>Raw query string including leading <c>?</c>, or empty.</summary>
    public string QueryString => _queryString ??= _queryStringRaw ?? BuildQueryString(_queryValues);

    /// <summary>First value per key. Prefer <see cref="GetQueryValues"/> for multi-value.</summary>
    public IReadOnlyDictionary<string, string> Query => _queryView ??= FirstWins(_queryValues);

    /// <summary>First value per key. Prefer <see cref="GetHeaderValues"/> for multi-value.</summary>
    public IReadOnlyDictionary<string, string> Headers => _headersView ??= FirstWins(_headerValues);
    public Stream Body { get; }
    public long? ContentLength { get; }
    public string? ContentType { get; }
    public IServiceProvider RequestServices { get; }
    public CancellationToken RequestAborted { get; }

    /// <summary>
    /// Per-request bag for host adapters and middleware (e.g. stashed <c>HttpContext</c>).
    /// </summary>
    public IDictionary<object, object?> Items => _items ??= new Dictionary<object, object?>();

    public string? GetHeader(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _headerValues.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;
    }

    public string? GetQuery(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _queryValues.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;
    }

    /// <summary>All values for a query key (empty when absent).</summary>
    public IReadOnlyList<string> GetQueryValues(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _queryValues.TryGetValue(name, out var values) ? values : NoValues;
    }

    /// <summary>All values for a header key (empty when absent).</summary>
    public IReadOnlyList<string> GetHeaderValues(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _headerValues.TryGetValue(name, out var values) ? values : NoValues;
    }

    /// <summary>First matching cookie value from the <c>Cookie</c> header, or null.</summary>
    public string? GetCookie(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var header = GetHeader("Cookie");
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq].Trim();
            if (!key.Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            return part[(eq + 1)..].Trim();
        }

        return null;
    }

    /// <summary>Read the entire body as UTF-8 text.</summary>
    public async Task<string> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await BufferBodyAsync(cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Buffer the request body into memory (once). Subsequent reads return the cached buffer.
    /// Resets <see cref="Body"/> is not possible when the original stream is non-seekable;
    /// callers should use the returned bytes.
    /// </summary>
    public async Task<byte[]> BufferBodyAsync(CancellationToken cancellationToken = default)
    {
        if (_bufferedBody is not null)
        {
            return _bufferedBody;
        }

        if (Body.CanSeek && Body.Position > 0)
        {
            Body.Position = 0;
        }

        await using var ms = new MemoryStream();
        await Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        _bufferedBody = ms.ToArray();
        return _bufferedBody;
    }

    /// <summary>ASP.NET-style path segment prefix check (case-insensitive).</summary>
    public bool PathStartsWithSegments(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix == "/")
        {
            return true;
        }

        var normalized = NormalizePath(prefix);
        if (Path.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var needle = normalized.TrimEnd('/');
        return Path.StartsWith(needle + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        if (path.Length > 1 && path.EndsWith('/'))
        {
            path = path.TrimEnd('/');
        }

        return path;
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append('?');
        var first = true;
        foreach (var (key, list) in values)
        {
            foreach (var value in list)
            {
                if (!first)
                {
                    sb.Append('&');
                }

                first = false;
                sb.Append(Uri.EscapeDataString(key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(value));
            }
        }

        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Promote(
        IReadOnlyDictionary<string, string>? single)
    {
        if (single is null || single.Count == 0)
        {
            return EmptyValues;
        }

        var map = new Dictionary<string, IReadOnlyList<string>>(single.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in single)
        {
            map[key] = new[] { value };
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> FirstWins(
        IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        if (ReferenceEquals(values, EmptyValues) || values.Count == 0)
        {
            return EmptyMap;
        }

        var map = new Dictionary<string, string>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in values)
        {
            map[key] = list.Count > 0 ? list[0] : string.Empty;
        }

        return map;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

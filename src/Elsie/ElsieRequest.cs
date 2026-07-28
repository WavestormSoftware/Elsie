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
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headerValues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        Method = method.ToUpperInvariant();
        Path = NormalizePath(path);
        _queryValues = queryValues ?? Promote(query);
        _headerValues = headerValues ?? Promote(headers);
        Query = FirstWins(_queryValues);
        Headers = FirstWins(_headerValues);
        Body = body ?? Stream.Null;
        ContentLength = contentLength;
        ContentType = contentType;
        RequestServices = requestServices ?? EmptyServiceProvider.Instance;
        RequestAborted = requestAborted;
        Items = items ?? new Dictionary<object, object?>();
    }

    public string Method { get; }
    public string Path { get; }

    /// <summary>First value per key. Prefer <see cref="GetQueryValues"/> for multi-value.</summary>
    public IReadOnlyDictionary<string, string> Query { get; }

    /// <summary>First value per key. Prefer <see cref="GetHeaderValues"/> for multi-value.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Stream Body { get; }
    public long? ContentLength { get; }
    public string? ContentType { get; }
    public IServiceProvider RequestServices { get; }
    public CancellationToken RequestAborted { get; }

    /// <summary>
    /// Per-request bag for host adapters and middleware (e.g. stashed <c>HttpContext</c>).
    /// </summary>
    public IDictionary<object, object?> Items { get; }

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

namespace Elsie;

/// <summary>
/// Host-agnostic HTTP request facade.
/// </summary>
public sealed class ElsieRequest
{
    private static readonly Dictionary<string, string> Empty =
        new(StringComparer.OrdinalIgnoreCase);

    public ElsieRequest(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? query = null,
        IReadOnlyDictionary<string, string>? headers = null,
        Stream? body = null,
        long? contentLength = null,
        string? contentType = null,
        IServiceProvider? requestServices = null,
        CancellationToken requestAborted = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        Method = method.ToUpperInvariant();
        Path = NormalizePath(path);
        Query = query ?? Empty;
        Headers = headers ?? Empty;
        Body = body ?? Stream.Null;
        ContentLength = contentLength;
        ContentType = contentType;
        RequestServices = requestServices ?? EmptyServiceProvider.Instance;
        RequestAborted = requestAborted;
    }

    public string Method { get; }
    public string Path { get; }
    public IReadOnlyDictionary<string, string> Query { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Stream Body { get; }
    public long? ContentLength { get; }
    public string? ContentType { get; }
    public IServiceProvider RequestServices { get; }
    public CancellationToken RequestAborted { get; }

    public string? GetHeader(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    public string? GetQuery(string name) =>
        Query.TryGetValue(name, out var value) ? value : null;

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

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

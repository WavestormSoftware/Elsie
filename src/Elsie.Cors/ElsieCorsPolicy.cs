namespace Elsie.Cors;

/// <summary>CORS policy: origins, methods, headers, credentials, max-age.</summary>
public sealed class ElsieCorsPolicy
{
    private readonly HashSet<string> _origins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _methods = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exposedHeaders = new(StringComparer.OrdinalIgnoreCase);

    public bool AllowAnyOrigin { get; private set; }
    public bool AllowAnyMethod { get; private set; }
    public bool AllowAnyHeader { get; private set; }
    public bool SupportsCredentials { get; private set; }
    public TimeSpan? PreflightMaxAge { get; private set; }

    public IReadOnlyCollection<string> Origins => _origins;
    public IReadOnlyCollection<string> Methods => _methods;
    public IReadOnlyCollection<string> Headers => _headers;
    public IReadOnlyCollection<string> ExposedHeaders => _exposedHeaders;

    public ElsieCorsPolicy AllowOrigin(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        if (origin == "*")
        {
            AllowAnyOrigin = true;
            _origins.Clear();
        }
        else
        {
            AllowAnyOrigin = false;
            _origins.Add(origin);
        }

        return this;
    }

    public ElsieCorsPolicy AllowOrigins(params string[] origins)
    {
        ArgumentNullException.ThrowIfNull(origins);
        foreach (var origin in origins)
        {
            AllowOrigin(origin);
        }

        return this;
    }

    public ElsieCorsPolicy AllowMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (method == "*")
        {
            AllowAnyMethod = true;
            _methods.Clear();
        }
        else
        {
            AllowAnyMethod = false;
            _methods.Add(method.ToUpperInvariant());
        }

        return this;
    }

    public ElsieCorsPolicy AllowMethods(params string[] methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        foreach (var method in methods)
        {
            AllowMethod(method);
        }

        return this;
    }

    public ElsieCorsPolicy AllowHeader(string header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(header);
        if (header == "*")
        {
            AllowAnyHeader = true;
            _headers.Clear();
        }
        else
        {
            AllowAnyHeader = false;
            _headers.Add(header);
        }

        return this;
    }

    public ElsieCorsPolicy AllowHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var header in headers)
        {
            AllowHeader(header);
        }

        return this;
    }

    public ElsieCorsPolicy WithExposedHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(header))
            {
                _exposedHeaders.Add(header);
            }
        }

        return this;
    }

    public ElsieCorsPolicy AllowCredentials()
    {
        SupportsCredentials = true;
        return this;
    }

    public ElsieCorsPolicy SetPreflightMaxAge(TimeSpan maxAge)
    {
        if (maxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge));
        }

        PreflightMaxAge = maxAge;
        return this;
    }

    internal bool IsOriginAllowed(string origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }

        return AllowAnyOrigin || _origins.Contains(origin);
    }
}

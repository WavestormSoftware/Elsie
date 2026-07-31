namespace Elsie.Routing;

/// <summary>
/// A registered route on an <see cref="ElsieModule"/>.
/// </summary>
public sealed class RouteDescriptor
{
    private readonly List<string> _tags = [];
    private readonly List<RouteProduces> _produces = [];
    private readonly List<string> _security = [];
    private Dictionary<string, object?>? _items;

    public RouteDescriptor(string method, string template, RouteHandler handler, ElsieModule? module = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        Method = method.ToUpperInvariant();
        Template = NormalizeTemplate(template);
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Module = module;
    }

    public string Method { get; }
    public string Template { get; }
    public RouteHandler Handler { get; }
    public ElsieModule? Module { get; }

    /// <summary>Optional unique route name for link generation and OpenAPI operationId override.</summary>
    public string? Name { get; internal set; }

    public string? Summary { get; internal set; }
    public string? Description { get; internal set; }
    public IReadOnlyList<string> Tags => _tags;
    public Type? AcceptsType { get; internal set; }
    public Type? AcceptsQueryType { get; internal set; }
    public IReadOnlyList<RouteProduces> Produces => _produces;
    public IReadOnlyList<string> Security => _security;

    /// <summary>Extensibility bag for satellite packages (e.g. CORS policy name).</summary>
    public IDictionary<string, object?> Items => _items ??= new Dictionary<string, object?>(StringComparer.Ordinal);

    internal void AddTags(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            if (!string.IsNullOrWhiteSpace(tag) && !_tags.Contains(tag, StringComparer.Ordinal))
            {
                _tags.Add(tag);
            }
        }
    }

    internal void AddProduces(Type type, int statusCode) =>
        _produces.Add(new RouteProduces(type, statusCode));

    internal void AddSecurity(string scheme)
    {
        if (!_security.Contains(scheme, StringComparer.Ordinal))
        {
            _security.Add(scheme);
        }
    }

    internal static string NormalizeTemplate(string template)
    {
        template = template.Trim();
        if (!template.StartsWith('/'))
        {
            template = "/" + template;
        }

        if (template.Length > 1 && template.EndsWith('/'))
        {
            template = template.TrimEnd('/');
        }

        return template;
    }
}

/// <summary>Declared response type/status metadata for OpenAPI.</summary>
public sealed class RouteProduces
{
    public RouteProduces(Type type, int statusCode)
    {
        Type = type ?? throw new ArgumentNullException(nameof(type));
        StatusCode = statusCode;
    }

    public Type Type { get; }
    public int StatusCode { get; }
}

/// <summary>
/// Fluent wrapper returned from verb registration methods.
/// </summary>
public sealed class RouteBuilder
{
    internal RouteBuilder(RouteDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public RouteDescriptor Descriptor { get; }

    public RouteBuilder Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Descriptor.Name = name;
        return this;
    }

    public RouteBuilder WithSummary(string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        Descriptor.Summary = summary;
        return this;
    }

    public RouteBuilder WithDescription(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Descriptor.Description = description;
        return this;
    }

    public RouteBuilder WithTags(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        Descriptor.AddTags(tags);
        return this;
    }

    public RouteBuilder Accepts<T>()
    {
        Descriptor.AcceptsType = typeof(T);
        return this;
    }

    /// <summary>Declare query-string DTO shape for OpenAPI (flattened object properties → query params).</summary>
    public RouteBuilder AcceptsQuery<T>()
    {
        Descriptor.AcceptsQueryType = typeof(T);
        return this;
    }

    public RouteBuilder Produces<T>(int statusCode = 200)
    {
        Descriptor.AddProduces(typeof(T), statusCode);
        return this;
    }

    public RouteBuilder WithSecurity(string scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        Descriptor.AddSecurity(scheme);
        return this;
    }
}

/// <summary>
/// Normalized async route handler.
/// </summary>
public delegate Task<ElsieResult> RouteHandler(ElsieContext context, CancellationToken cancellationToken);

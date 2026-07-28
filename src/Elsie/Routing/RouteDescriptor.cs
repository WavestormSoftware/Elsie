namespace Elsie.Routing;

/// <summary>
/// A registered route on an <see cref="ElsieModule"/>.
/// </summary>
public sealed class RouteDescriptor
{
    public RouteDescriptor(string method, string template, RouteHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        Method = method.ToUpperInvariant();
        Template = NormalizeTemplate(template);
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public string Method { get; }
    public string Template { get; }
    public RouteHandler Handler { get; }

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

/// <summary>
/// Normalized async route handler.
/// </summary>
public delegate Task<ElsieResult> RouteHandler(ElsieContext context, CancellationToken cancellationToken);

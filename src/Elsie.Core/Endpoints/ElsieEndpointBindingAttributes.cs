namespace Elsie;

/// <summary>Bind the parameter from the matched route value with the same name.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromRouteAttribute : Attribute
{
    public FromRouteAttribute()
    {
    }

    public FromRouteAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Route value name; defaults to the parameter name.</summary>
    public string? Name { get; }
}

/// <summary>Bind the parameter from the query string (repeated keys fill arrays).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromQueryAttribute : Attribute
{
    public FromQueryAttribute()
    {
    }

    public FromQueryAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Query key; defaults to the parameter name.</summary>
    public string? Name { get; }
}

/// <summary>Bind the parameter from a request header.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromHeaderAttribute : Attribute
{
    public FromHeaderAttribute()
    {
    }

    public FromHeaderAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Header name; defaults to the parameter name.</summary>
    public string? Name { get; }
}

/// <summary>
/// Bind the parameter from the request body (JSON, or form fields when the request is
/// <c>application/x-www-form-urlencoded</c>/<c>multipart/form-data</c>). Honors
/// <see cref="ElsieOptions.MaxBindBodySize"/>; failures produce a 400/415 problem response.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromBodyAttribute : Attribute
{
}

/// <summary>Resolve the parameter from the request-scoped service provider.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromServicesAttribute : Attribute
{
}

/// <summary>
/// Marks a parameter type as a service resolved from DI instead of being bound from the body or
/// query string. Alias of <see cref="FromServicesAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ElsieServiceAttribute : Attribute
{
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Elsie.Routing;

namespace Elsie.OpenApi;

/// <summary>Builds an OpenAPI 3.0 document from an Elsie <see cref="RouteTable"/> and route metadata.</summary>
public static partial class ElsieOpenApiDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Create an OpenAPI 3 document object graph suitable for JSON serialization.</summary>
    public static Dictionary<string, object?> Create(RouteTable table, ElsieOpenApiInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        info ??= new ElsieOpenApiInfo();

        var schemaTypes = new List<Type>();
        foreach (var route in table.Routes)
        {
            if (route.AcceptsType is not null)
            {
                schemaTypes.Add(route.AcceptsType);
            }

            if (route.AcceptsQueryType is not null)
            {
                schemaTypes.Add(route.AcceptsQueryType);
            }

            foreach (var p in route.Produces)
            {
                schemaTypes.Add(p.Type);
            }
        }

        var componentsSchemas = ElsieJsonSchema.BuildComponentsSchemas(schemaTypes, out var typeToName);

        var paths = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var route in table.Routes)
        {
            var (path, pathParameters) = ConvertTemplate(route.Template);
            if (!paths.TryGetValue(path, out var operations))
            {
                operations = new Dictionary<string, object>(StringComparer.Ordinal);
                paths[path] = operations;
            }

            var operation = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["operationId"] = route.Name ?? MakeOperationId(route.Method, route.Template),
                ["responses"] = BuildResponses(route, componentsSchemas, typeToName)
            };

            if (!string.IsNullOrWhiteSpace(route.Summary))
            {
                operation["summary"] = route.Summary!;
            }

            if (!string.IsNullOrWhiteSpace(route.Description))
            {
                operation["description"] = route.Description!;
            }

            if (route.Tags.Count > 0)
            {
                operation["tags"] = route.Tags.ToArray();
            }

            var parameters = new List<Dictionary<string, object>>(pathParameters);
            if (route.AcceptsQueryType is not null)
            {
                parameters.AddRange(BuildQueryParameters(route.AcceptsQueryType, componentsSchemas, typeToName));
            }

            if (parameters.Count > 0)
            {
                operation["parameters"] = parameters;
            }

            if (route.AcceptsType is not null)
            {
                operation["requestBody"] = Dict(
                    ("required", true),
                    ("content", Dict(
                        ("application/json", Dict(
                            ("schema", ElsieJsonSchema.RefOrInline(route.AcceptsType, componentsSchemas, typeToName)))))));
            }
            else if (route.Method is "POST" or "PUT" or "PATCH")
            {
                // Fallback when handler didn't declare Accepts<T>.
                operation["requestBody"] = Dict(
                    ("required", true),
                    ("content", Dict(
                        ("application/json", Dict(
                            ("schema", Dict(("type", "object"))))))));
            }

            if (route.Security.Count > 0)
            {
                operation["security"] = route.Security
                    .Select(scheme => new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [scheme] = Array.Empty<string>()
                    })
                    .ToArray();
            }

            operations[route.Method.ToLowerInvariant()] = operation;
        }

        var components = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (componentsSchemas.Count > 0)
        {
            components["schemas"] = componentsSchemas;
        }

        if (info.SecuritySchemes.Count > 0)
        {
            var schemes = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (name, scheme) in info.SecuritySchemes)
            {
                schemes[name] = scheme.ToOpenApiObject();
            }

            components["securitySchemes"] = schemes;
        }

        var doc = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = info.Title,
                ["version"] = info.Version,
                ["description"] = info.Description
            },
            ["paths"] = paths
        };

        if (components.Count > 0)
        {
            doc["components"] = components;
        }

        return doc;
    }

    /// <summary>Serialize <see cref="Create"/> to UTF-8 JSON.</summary>
    public static byte[] ToUtf8Json(RouteTable table, ElsieOpenApiInfo? info = null, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToUtf8Bytes(Create(table, info), options ?? JsonOptions);

    /// <summary>Serialize <see cref="Create"/> to a JSON string.</summary>
    public static string ToJson(RouteTable table, ElsieOpenApiInfo? info = null, JsonSerializerOptions? options = null) =>
        Encoding.UTF8.GetString(ToUtf8Json(table, info, options));

    internal static (string Path, List<Dictionary<string, object>> Parameters) ConvertTemplate(string template)
    {
        var parameters = new List<Dictionary<string, object>>();
        var path = ParamRegex().Replace(template, match =>
        {
            var inner = match.Groups[1].Value;
            if (inner.StartsWith('*'))
            {
                inner = inner[1..];
            }

            string? constraint = null;
            var required = true;

            var eq = inner.IndexOf('=');
            if (eq >= 0)
            {
                inner = inner[..eq];
                required = false;
            }
            else if (inner.EndsWith('?'))
            {
                inner = inner[..^1];
                required = false;
            }

            var colon = inner.IndexOf(':');
            if (colon > 0)
            {
                constraint = inner[(colon + 1)..];
                inner = inner[..colon];
            }

            parameters.Add(Dict(
                ("name", inner),
                ("in", "path"),
                ("required", required),
                ("schema", SchemaForConstraint(constraint))));

            return "{" + inner + "}";
        });

        return (path, parameters);
    }

    private static Dictionary<string, object> BuildResponses(
        RouteDescriptor route,
        Dictionary<string, object> components,
        Dictionary<Type, string> typeToName)
    {
        var responses = new Dictionary<string, object>(StringComparer.Ordinal);
        if (route.Produces.Count == 0)
        {
            responses["200"] = Dict(("description", "OK"));
            return responses;
        }

        foreach (var group in route.Produces.GroupBy(p => p.StatusCode))
        {
            var status = group.Key.ToString();
            // Last declaration wins for schema if multiple types share a status.
            var last = group.Last();
            responses[status] = Dict(
                ("description", StatusDescription(group.Key)),
                ("content", Dict(
                    ("application/json", Dict(
                        ("schema", ElsieJsonSchema.RefOrInline(last.Type, components, typeToName)))))));
        }

        return responses;
    }

    private static List<Dictionary<string, object>> BuildQueryParameters(
        Type queryType,
        Dictionary<string, object> components,
        Dictionary<Type, string> typeToName)
    {
        var list = new List<Dictionary<string, object>>();
        foreach (var prop in queryType.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var name = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            var required = Nullable.GetUnderlyingType(prop.PropertyType) is null
                           && prop.PropertyType.IsValueType;
            list.Add(Dict(
                ("name", name),
                ("in", "query"),
                ("required", required),
                ("schema", ElsieJsonSchema.RefOrInline(
                    Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType,
                    components,
                    typeToName))));
        }

        return list;
    }

    private static string StatusDescription(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        _ => "Response"
    };

    private static Dictionary<string, object> SchemaForConstraint(string? constraint) =>
        (constraint?.ToLowerInvariant()) switch
        {
            "int" => Dict(("type", "integer"), ("format", "int32")),
            "long" => Dict(("type", "integer"), ("format", "int64")),
            "guid" => Dict(("type", "string"), ("format", "uuid")),
            "bool" => Dict(("type", "boolean")),
            "datetime" => Dict(("type", "string"), ("format", "date-time")),
            "decimal" or "double" => Dict(("type", "number")),
            _ => Dict(("type", "string"))
        };

    private static Dictionary<string, object> Dict(params (string Key, object Value)[] pairs)
    {
        var d = new Dictionary<string, object>(pairs.Length, StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }

        return d;
    }

    private static string MakeOperationId(string method, string template) =>
        $"{method}_{NonOpIdChars().Replace(template, "_").Trim('_')}";

    [GeneratedRegex(@"\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ParamRegex();

    [GeneratedRegex(@"[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonOpIdChars();
}

/// <summary>OpenAPI <c>info</c> + optional security scheme registry.</summary>
public sealed class ElsieOpenApiInfo
{
    public string Title { get; set; } = "Elsie API";
    public string Version { get; set; } = "v1";
    public string? Description { get; set; }

    /// <summary>
    /// Named security schemes emitted under <c>components.securitySchemes</c>.
    /// Reference from routes via <see cref="RouteBuilder.WithSecurity"/>.
    /// </summary>
    public Dictionary<string, ElsieOpenApiSecurityScheme> SecuritySchemes { get; } =
        new(StringComparer.Ordinal);
}

/// <summary>OpenAPI security scheme (apiKey / http bearer / etc.).</summary>
public sealed class ElsieOpenApiSecurityScheme
{
    public string Type { get; set; } = "apiKey";
    public string? Name { get; set; }
    public string? In { get; set; }
    public string? Scheme { get; set; }
    public string? BearerFormat { get; set; }
    public string? Description { get; set; }

    public static ElsieOpenApiSecurityScheme ApiKeyHeader(string headerName = "X-Api-Key", string? description = null) =>
        new()
        {
            Type = "apiKey",
            Name = headerName,
            In = "header",
            Description = description
        };

    public static ElsieOpenApiSecurityScheme BearerJwt(string? description = null) =>
        new()
        {
            Type = "http",
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = description
        };

    internal Dictionary<string, object?> ToOpenApiObject()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = Type
        };
        if (!string.IsNullOrWhiteSpace(Name))
        {
            d["name"] = Name;
        }

        if (!string.IsNullOrWhiteSpace(In))
        {
            d["in"] = In;
        }

        if (!string.IsNullOrWhiteSpace(Scheme))
        {
            d["scheme"] = Scheme;
        }

        if (!string.IsNullOrWhiteSpace(BearerFormat))
        {
            d["bearerFormat"] = BearerFormat;
        }

        if (!string.IsNullOrWhiteSpace(Description))
        {
            d["description"] = Description;
        }

        return d;
    }
}

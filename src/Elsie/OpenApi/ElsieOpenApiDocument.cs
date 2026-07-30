using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Elsie.Routing;

namespace Elsie.OpenApi;

/// <summary>Builds a minimal OpenAPI 3.0 document from an Elsie <see cref="RouteTable"/>.</summary>
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

        var paths = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        foreach (var route in table.Routes)
        {
            var (path, parameters) = ConvertTemplate(route.Template);
            if (!paths.TryGetValue(path, out var operations))
            {
                operations = new Dictionary<string, object>(StringComparer.Ordinal);
                paths[path] = operations;
            }

            var operation = Dict(
                ("operationId", MakeOperationId(route.Method, route.Template)),
                ("responses", Dict(("200", Dict(("description", "OK"))))));

            if (parameters.Count > 0)
            {
                operation["parameters"] = parameters;
            }

            if (route.Method is "POST" or "PUT" or "PATCH")
            {
                operation["requestBody"] = Dict(
                    ("required", true),
                    ("content", Dict(
                        ("application/json", Dict(
                            ("schema", Dict(("type", "object"))))))));
            }

            operations[route.Method.ToLowerInvariant()] = operation;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
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

            // OpenAPI path params are always required in the path template sense;
            // optional segments are still emitted as required=false for documentation.
            parameters.Add(Dict(
                ("name", inner),
                ("in", "path"),
                ("required", required),
                ("schema", SchemaForConstraint(constraint))));

            return "{" + inner + "}";
        });

        return (path, parameters);
    }

    private static Dictionary<string, object> SchemaForConstraint(string? constraint) =>
        (constraint?.ToLowerInvariant()) switch
        {
            "int" => Dict(("type", "integer"), ("format", "int32")),
            "long" => Dict(("type", "integer"), ("format", "int64")),
            "guid" => Dict(("type", "string"), ("format", "uuid")),
            "bool" => Dict(("type", "boolean")),
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

/// <summary>OpenAPI <c>info</c> block for <see cref="ElsieOpenApiDocument"/>.</summary>
public sealed class ElsieOpenApiInfo
{
    public string Title { get; set; } = "Elsie API";
    public string Version { get; set; } = "v1";
    public string? Description { get; set; }
}

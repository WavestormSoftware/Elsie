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

            var method = route.Method.ToLowerInvariant();
            if (method is "head" or "options")
            {
                // Still document them — useful for complete surface maps.
            }

            var operation = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["operationId"] = MakeOperationId(route.Method, route.Template),
                ["responses"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["200"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["description"] = "OK"
                    }
                }
            };

            if (parameters.Count > 0)
            {
                operation["parameters"] = parameters;
            }

            if (route.Method is "POST" or "PUT" or "PATCH")
            {
                operation["requestBody"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["required"] = true,
                    ["content"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["schema"] = new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                ["type"] = "object"
                            }
                        }
                    }
                };
            }

            operations[method] = operation;
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
        JsonSerializer.Serialize(Create(table, info), options ?? JsonOptions);

    internal static (string Path, List<Dictionary<string, object>> Parameters) ConvertTemplate(string template)
    {
        var parameters = new List<Dictionary<string, object>>();
        var path = ParamRegex().Replace(template, match =>
        {
            var inner = match.Groups[1].Value;
            var isCatchAll = inner.StartsWith('*');
            if (isCatchAll)
            {
                inner = inner[1..];
            }

            string? constraint = null;
            var colon = inner.IndexOf(':');
            if (colon > 0)
            {
                constraint = inner[(colon + 1)..];
                inner = inner[..colon];
            }

            parameters.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = inner,
                ["in"] = "path",
                ["required"] = true,
                ["schema"] = SchemaForConstraint(constraint)
            });

            return "{" + inner + "}";
        });

        return (path, parameters);
    }

    private static Dictionary<string, object> SchemaForConstraint(string? constraint) =>
        (constraint?.ToLowerInvariant()) switch
        {
            "int" or "long" => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "integer",
                ["format"] = constraint!.Equals("long", StringComparison.OrdinalIgnoreCase) ? "int64" : "int32"
            },
            "guid" => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "string",
                ["format"] = "uuid"
            },
            "bool" => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "boolean"
            },
            _ => new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = "string"
            }
        };

    private static string MakeOperationId(string method, string template)
    {
        var cleaned = NonOpIdChars().Replace(template, "_").Trim('_');
        return $"{method}_{cleaned}";
    }

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

using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Elsie.Routing;

/// <summary>
/// Immutable route collection + matcher built at startup.
/// </summary>
public sealed class RouteTable
{
    private readonly RouteMatcher _matcher;
    private readonly Dictionary<string, RouteDescriptor> _named;

    public RouteTable(IEnumerable<RouteDescriptor> routes, ElsieOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        options ??= new ElsieOptions();
        var list = routes.ToList();
        var constraints = new RouteConstraintResolver(options.RouteConstraints);

        // Compile-time validation (unknown constraints, dup params, catch-all position) happens in Compile.
        EnsureNoExactDuplicates(list);
        EnsureNoAmbiguity(list, constraints);
        _named = BuildNamedIndex(list);

        Routes = list;
        Options = options;
        _matcher = new RouteMatcher(list, constraints, options.ImplicitHead);
    }

    public IReadOnlyList<RouteDescriptor> Routes { get; }
    internal ElsieOptions Options { get; }

    public static RouteTable FromModules(IEnumerable<ElsieModule> modules, ElsieOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return new RouteTable(modules.SelectMany(m => m.Routes), options);
    }

    /// <summary>Full lookup: matched, method-not-allowed, or not found.</summary>
    public RouteLookup Lookup(string method, string path) => _matcher.Lookup(method, path);

    /// <summary>True only when method+path match a handler.</summary>
    public bool TryMatch(string method, string path, out RouteMatch? match) =>
        _matcher.TryMatch(method, path, out match);

    /// <summary>Expand a named route template with route values (URL-encoded).</summary>
    public string GetPathByName(string name, IReadOnlyDictionary<string, string?>? values = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_named.TryGetValue(name, out var route))
        {
            throw new InvalidOperationException($"No route named '{name}' is registered.");
        }

        return ExpandTemplate(route.Template, values);
    }

    /// <summary>Expand a named route; values may be a dictionary or anonymous object.</summary>
    public string GetPathByName(string name, object? values) =>
        GetPathByName(name, ToRouteValueDictionary(values));

    /// <summary>Try expand a named route; returns false when the name is unknown.</summary>
    public bool TryGetPathByName(string name, out string path, IReadOnlyDictionary<string, string?>? values = null)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || !_named.TryGetValue(name, out var route))
        {
            return false;
        }

        path = ExpandTemplate(route.Template, values);
        return true;
    }

    internal static string ExpandTemplate(string template, IReadOnlyDictionary<string, string?>? values)
    {
        values ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var raw = template.Trim('/');
        if (raw.Length == 0)
        {
            return "/";
        }

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (!(part.StartsWith('{') && part.EndsWith('}') && part.Length > 2))
            {
                sb.Append('/').Append(part);
                continue;
            }

            var inner = part[1..^1];
            var isCatchAll = inner.StartsWith('*');
            if (isCatchAll)
            {
                inner = inner[1..];
            }

            // Strip constraint / optional / default for the name.
            var eq = inner.IndexOf('=');
            if (eq >= 0) inner = inner[..eq];
            if (inner.EndsWith('?')) inner = inner[..^1];
            var colon = inner.IndexOf(':');
            if (colon > 0) inner = inner[..colon];

            if (!values.TryGetValue(inner, out var value) || value is null)
            {
                // Optional with no value → skip segment
                if (part.Contains('?', StringComparison.Ordinal) || part.Contains('=', StringComparison.Ordinal))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Missing route value '{inner}' for template '{template}'.");
            }

            if (isCatchAll)
            {
                // Catch-all: allow embedded slashes (encode each segment).
                foreach (var seg in value.Split('/', StringSplitOptions.None))
                {
                    sb.Append('/').Append(Uri.EscapeDataString(seg));
                }
            }
            else
            {
                sb.Append('/').Append(Uri.EscapeDataString(value));
            }
        }

        return sb.Length == 0 ? "/" : sb.ToString();
    }

    internal static IReadOnlyDictionary<string, string?> ToRouteValueDictionary(object? values)
    {
        if (values is null)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        if (values is IReadOnlyDictionary<string, string?> typed)
        {
            return typed;
        }

        if (values is IDictionary<string, string?> dict)
        {
            return new Dictionary<string, string?>(dict, StringComparer.OrdinalIgnoreCase);
        }

        if (values is IDictionary map)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in map)
            {
                if (entry.Key is null) continue;
                result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] =
                    entry.Value is null ? null : Convert.ToString(entry.Value, CultureInfo.InvariantCulture);
            }

            return result;
        }

        var props = values.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var bag = new Dictionary<string, string?>(props.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var prop in props)
        {
            if (!prop.CanRead) continue;
            var v = prop.GetValue(values);
            bag[prop.Name] = v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        return bag;
    }

    private static void EnsureNoExactDuplicates(List<RouteDescriptor> routes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            var key = route.Method + " " + route.Template;
            if (!seen.Add(key))
            {
                throw new InvalidOperationException($"Duplicate route registration: {key}");
            }
        }
    }

    private static Dictionary<string, RouteDescriptor> BuildNamedIndex(List<RouteDescriptor> routes)
    {
        var named = new Dictionary<string, RouteDescriptor>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (string.IsNullOrEmpty(route.Name))
            {
                continue;
            }

            if (!named.TryAdd(route.Name, route))
            {
                throw new InvalidOperationException(
                    $"Duplicate route name '{route.Name}' on templates '{named[route.Name].Template}' and '{route.Template}'.");
            }
        }

        return named;
    }

    private static void EnsureNoAmbiguity(List<RouteDescriptor> routes, RouteConstraintResolver constraints)
    {
        var inspected = routes
            .Select(r =>
            {
                var (prec, statics, template, method) = RouteMatcher.Inspect(r, constraints);
                return (Route: r, Precedence: prec, Statics: statics, Template: template, Method: method);
            })
            .ToList();

        for (var i = 0; i < inspected.Count; i++)
        {
            for (var j = i + 1; j < inspected.Count; j++)
            {
                var a = inspected[i];
                var b = inspected[j];
                if (!string.Equals(a.Method, b.Method, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!PrecedenceEqual(a.Precedence, b.Precedence))
                {
                    continue;
                }

                if (!StaticsEqual(a.Statics, b.Statics))
                {
                    continue;
                }

                // Same method + equal precedence vector + equal statics → ambiguous
                // (e.g. /users/{id} vs /users/{name})
                throw new InvalidOperationException(
                    $"Ambiguous routes for {a.Method}: '{a.Template}' and '{b.Template}' " +
                    "have equal precedence and static segments.");
            }
        }
    }

    private static bool PrecedenceEqual(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    private static bool StaticsEqual(string?[] a, string?[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] is null && b[i] is null) continue;
            if (a[i] is null || b[i] is null) return false;
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}

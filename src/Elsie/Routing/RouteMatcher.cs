using Microsoft.AspNetCore.Http;

namespace Elsie.Routing;

/// <summary>
/// Simple segment matcher supporting static segments and {param} tokens.
/// </summary>
public sealed class RouteMatcher : IRouteMatcher
{
    private readonly IReadOnlyList<CompiledRoute> _routes;

    public RouteMatcher(RouteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _routes = table.Routes.Select(CompiledRoute.Compile).ToArray();
    }

    public bool TryMatch(string method, PathString path, out RouteMatch? match)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        method = method.ToUpperInvariant();
        var pathValue = NormalizePath(path.Value ?? "/");

        foreach (var route in _routes)
        {
            if (!string.Equals(route.Descriptor.Method, method, StringComparison.Ordinal))
            {
                continue;
            }

            if (route.TryMatch(pathValue, out var values))
            {
                match = new RouteMatch(route.Descriptor, values);
                return true;
            }
        }

        match = null;
        return false;
    }

    internal static string NormalizePath(string path)
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

    private sealed class CompiledRoute
    {
        private readonly string[] _segments;
        private readonly bool[] _isParam;
        private readonly string[] _paramNames;

        private CompiledRoute(RouteDescriptor descriptor, string[] segments, bool[] isParam, string[] paramNames)
        {
            Descriptor = descriptor;
            _segments = segments;
            _isParam = isParam;
            _paramNames = paramNames;
        }

        public RouteDescriptor Descriptor { get; }

        public static CompiledRoute Compile(RouteDescriptor descriptor)
        {
            var raw = descriptor.Template.Trim('/');
            if (raw.Length == 0)
            {
                return new CompiledRoute(descriptor, Array.Empty<string>(), Array.Empty<bool>(), Array.Empty<string>());
            }

            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var segments = new string[parts.Length];
            var isParam = new bool[parts.Length];
            var names = new string[parts.Length];

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.StartsWith('{') && part.EndsWith('}') && part.Length > 2)
                {
                    isParam[i] = true;
                    names[i] = part[1..^1];
                    segments[i] = string.Empty;
                }
                else
                {
                    isParam[i] = false;
                    names[i] = string.Empty;
                    segments[i] = part;
                }
            }

            return new CompiledRoute(descriptor, segments, isParam, names);
        }

        public bool TryMatch(string path, out IReadOnlyDictionary<string, string> values)
        {
            var raw = path.Trim('/');
            if (raw.Length == 0)
            {
                if (_segments.Length == 0)
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return true;
                }

                values = null!;
                return false;
            }

            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != _segments.Length)
            {
                values = null!;
                return false;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < parts.Length; i++)
            {
                if (_isParam[i])
                {
                    map[_paramNames[i]] = Uri.UnescapeDataString(parts[i]);
                }
                else if (!string.Equals(parts[i], _segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    values = null!;
                    return false;
                }
            }

            values = map;
            return true;
        }
    }
}

using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Elsie.Routing;

/// <summary>
/// Simple segment matcher supporting static segments and {param} / {param:constraint} tokens.
/// Built-in constraints: int, long, guid, bool.
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
        private readonly Segment[] _segments;

        private CompiledRoute(RouteDescriptor descriptor, Segment[] segments)
        {
            Descriptor = descriptor;
            _segments = segments;
        }

        public RouteDescriptor Descriptor { get; }

        public static CompiledRoute Compile(RouteDescriptor descriptor)
        {
            var raw = descriptor.Template.Trim('/');
            if (raw.Length == 0)
            {
                return new CompiledRoute(descriptor, Array.Empty<Segment>());
            }

            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var segments = new Segment[parts.Length];

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.StartsWith('{') && part.EndsWith('}') && part.Length > 2)
                {
                    var inner = part[1..^1];
                    var colon = inner.IndexOf(':');
                    if (colon > 0)
                    {
                        var name = inner[..colon];
                        var constraint = inner[(colon + 1)..];
                        segments[i] = Segment.Param(name, constraint);
                    }
                    else
                    {
                        segments[i] = Segment.Param(inner, constraint: null);
                    }
                }
                else
                {
                    segments[i] = Segment.Static(part);
                }
            }

            return new CompiledRoute(descriptor, segments);
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
                var segment = _segments[i];
                var value = Uri.UnescapeDataString(parts[i]);

                if (!segment.IsParameter)
                {
                    if (!string.Equals(value, segment.Literal, StringComparison.OrdinalIgnoreCase))
                    {
                        values = null!;
                        return false;
                    }

                    continue;
                }

                if (!MatchesConstraint(value, segment.Constraint))
                {
                    values = null!;
                    return false;
                }

                map[segment.Name!] = value;
            }

            values = map;
            return true;
        }

        private static bool MatchesConstraint(string value, string? constraint)
        {
            if (string.IsNullOrEmpty(constraint))
            {
                return true;
            }

            return constraint.ToLowerInvariant() switch
            {
                "int" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                "long" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
                "guid" => Guid.TryParse(value, out _),
                "bool" => bool.TryParse(value, out _),
                _ => throw new InvalidOperationException($"Unknown route constraint '{constraint}'. Supported: int, long, guid, bool.")
            };
        }

        private readonly struct Segment
        {
            private Segment(bool isParameter, string? literal, string? name, string? constraint)
            {
                IsParameter = isParameter;
                Literal = literal;
                Name = name;
                Constraint = constraint;
            }

            public bool IsParameter { get; }
            public string? Literal { get; }
            public string? Name { get; }
            public string? Constraint { get; }

            public static Segment Static(string literal) => new(false, literal, null, null);
            public static Segment Param(string name, string? constraint) => new(true, null, name, constraint);
        }
    }
}

using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Elsie.Routing;

/// <summary>
/// Segment matcher: static segments, {param}, {param:constraint}, and trailing {*catchAll}.
/// Built-in constraints: int, long, guid, bool.
/// </summary>
public sealed class RouteMatcher : IRouteMatcher
{
    private readonly IReadOnlyList<CompiledRoute> _routes;

    public RouteMatcher(RouteTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        // Catch-all routes sort after concrete ones so specific templates win by default.
        _routes = table.Routes
            .Select(CompiledRoute.Compile)
            .OrderBy(static r => r.HasCatchAll ? 1 : 0)
            .ToArray();
    }

    public RouteLookup Lookup(string method, PathString path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        method = method.ToUpperInvariant();
        var pathValue = NormalizePath(path.Value ?? "/");

        RouteMatch? matched = null;
        List<string>? allowed = null;

        foreach (var route in _routes)
        {
            if (!route.TryMatch(pathValue, out var values))
            {
                continue;
            }

            if (string.Equals(route.Descriptor.Method, method, StringComparison.Ordinal))
            {
                matched = new RouteMatch(route.Descriptor, values);
                break;
            }

            allowed ??= [];
            if (!allowed.Contains(route.Descriptor.Method, StringComparer.Ordinal))
            {
                allowed.Add(route.Descriptor.Method);
            }
        }

        if (matched is not null)
        {
            return RouteLookup.Matched(matched);
        }

        if (allowed is { Count: > 0 })
        {
            allowed.Sort(StringComparer.Ordinal);
            return RouteLookup.MethodNotAllowed(allowed);
        }

        return RouteLookup.NotFound();
    }

    public bool TryMatch(string method, PathString path, out RouteMatch? match)
    {
        var lookup = Lookup(method, path);
        if (lookup.Status == RouteLookupStatus.Matched)
        {
            match = lookup.Match;
            return true;
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
        private readonly bool _hasCatchAll;

        private CompiledRoute(RouteDescriptor descriptor, Segment[] segments, bool hasCatchAll)
        {
            Descriptor = descriptor;
            _segments = segments;
            _hasCatchAll = hasCatchAll;
        }

        public RouteDescriptor Descriptor { get; }
        public bool HasCatchAll => _hasCatchAll;

        public static CompiledRoute Compile(RouteDescriptor descriptor)
        {
            var raw = descriptor.Template.Trim('/');
            if (raw.Length == 0)
            {
                return new CompiledRoute(descriptor, Array.Empty<Segment>(), hasCatchAll: false);
            }

            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var segments = new Segment[parts.Length];
            var hasCatchAll = false;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.StartsWith('{') && part.EndsWith('}') && part.Length > 2)
                {
                    var inner = part[1..^1];
                    var isCatchAll = inner.StartsWith('*');
                    if (isCatchAll)
                    {
                        if (i != parts.Length - 1)
                        {
                            throw new InvalidOperationException(
                                $"Catch-all parameter '{{{inner}}}' must be the final segment in template '{descriptor.Template}'.");
                        }

                        inner = inner[1..];
                        if (inner.Length == 0)
                        {
                            throw new InvalidOperationException(
                                $"Catch-all parameter in template '{descriptor.Template}' must have a name (e.g. {{*path}}).");
                        }

                        // Constraints on catch-all are not supported.
                        var colon = inner.IndexOf(':');
                        if (colon >= 0)
                        {
                            throw new InvalidOperationException(
                                $"Catch-all parameter '{{{part[1..^1]}}}' does not support constraints.");
                        }

                        segments[i] = Segment.CatchAll(inner);
                        hasCatchAll = true;
                    }
                    else
                    {
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
                }
                else
                {
                    segments[i] = Segment.Static(part);
                }
            }

            return new CompiledRoute(descriptor, segments, hasCatchAll);
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

                // "/files/{*path}" matches "/files" with empty catch-all.
                if (_hasCatchAll && _segments.Length == 1 && _segments[0].IsCatchAll)
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [_segments[0].Name!] = string.Empty
                    };
                    return true;
                }

                values = null!;
                return false;
            }

            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (_hasCatchAll)
            {
                // Need at least all non-catch-all segments; catch-all may consume zero or more.
                var fixedCount = _segments.Length - 1;
                if (parts.Length < fixedCount)
                {
                    values = null!;
                    return false;
                }

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < fixedCount; i++)
                {
                    if (!MatchSegment(_segments[i], parts[i], map))
                    {
                        values = null!;
                        return false;
                    }
                }

                var catchAll = _segments[^1];
                if (parts.Length == fixedCount)
                {
                    map[catchAll.Name!] = string.Empty;
                }
                else
                {
                    var rest = new string[parts.Length - fixedCount];
                    for (var i = 0; i < rest.Length; i++)
                    {
                        rest[i] = Uri.UnescapeDataString(parts[fixedCount + i]);
                    }

                    map[catchAll.Name!] = string.Join('/', rest);
                }

                values = map;
                return true;
            }

            if (parts.Length != _segments.Length)
            {
                values = null!;
                return false;
            }

            var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < parts.Length; i++)
            {
                if (!MatchSegment(_segments[i], parts[i], exact))
                {
                    values = null!;
                    return false;
                }
            }

            values = exact;
            return true;
        }

        private static bool MatchSegment(Segment segment, string rawPart, Dictionary<string, string> map)
        {
            var value = Uri.UnescapeDataString(rawPart);

            if (!segment.IsParameter)
            {
                return string.Equals(value, segment.Literal, StringComparison.OrdinalIgnoreCase);
            }

            if (!MatchesConstraint(value, segment.Constraint))
            {
                return false;
            }

            map[segment.Name!] = value;
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
            private Segment(bool isParameter, bool isCatchAll, string? literal, string? name, string? constraint)
            {
                IsParameter = isParameter;
                IsCatchAll = isCatchAll;
                Literal = literal;
                Name = name;
                Constraint = constraint;
            }

            public bool IsParameter { get; }
            public bool IsCatchAll { get; }
            public string? Literal { get; }
            public string? Name { get; }
            public string? Constraint { get; }

            public static Segment Static(string literal) => new(false, false, literal, null, null);
            public static Segment Param(string name, string? constraint) => new(true, false, null, name, constraint);
            public static Segment CatchAll(string name) => new(true, true, null, name, null);
        }
    }
}

namespace Elsie.Routing;

/// <summary>
/// Segment matcher with deterministic precedence.
/// Per-segment rank: static (0) &gt; constrained-param (1) &gt; param (2) &gt; catch-all (3).
/// Owned by <see cref="RouteTable"/> — not a public seam.
/// </summary>
internal sealed class RouteMatcher
{
    private readonly IReadOnlyList<CompiledRoute> _routes;
    private readonly bool _implicitHead;

    public RouteMatcher(
        IReadOnlyList<RouteDescriptor> routes,
        RouteConstraintResolver constraints,
        bool implicitHead = true)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(constraints);
        _implicitHead = implicitHead;

        var compiled = routes.Select(r => CompiledRoute.Compile(r, constraints)).ToList();
        // Deterministic order: precedence vector, then template ordinal (stable tie-break for non-ambiguous).
        _routes = compiled
            .OrderBy(static r => r.Precedence, PrecedenceComparer.Instance)
            .ThenBy(static r => r.Descriptor.Template, StringComparer.Ordinal)
            .ThenBy(static r => r.Descriptor.Method, StringComparer.Ordinal)
            .ToArray();
    }

    public RouteLookup Lookup(string method, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        method = method.ToUpperInvariant();
        var pathValue = ElsieRequest.NormalizePath(path);

        CompiledRoute? best = null;
        IReadOnlyDictionary<string, string>? bestValues = null;
        List<string>? allowed = null;

        foreach (var route in _routes)
        {
            if (!route.TryMatch(pathValue, out var values))
            {
                continue;
            }

            if (string.Equals(route.Descriptor.Method, method, StringComparison.Ordinal))
            {
                // First hit wins: list is precedence-sorted.
                best = route;
                bestValues = values;
                break;
            }

            // HEAD → GET fallback considered after full scan for explicit HEAD.
            allowed ??= [];
            if (!allowed.Contains(route.Descriptor.Method, StringComparer.Ordinal))
            {
                allowed.Add(route.Descriptor.Method);
            }
        }

        if (best is null && _implicitHead && method == "HEAD")
        {
            foreach (var route in _routes)
            {
                if (!string.Equals(route.Descriptor.Method, "GET", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!route.TryMatch(pathValue, out var values))
                {
                    continue;
                }

                best = route;
                bestValues = values;
                break;
            }
        }

        if (best is not null)
        {
            return RouteLookup.Matched(new RouteMatch(best.Descriptor, bestValues!));
        }

        if (allowed is { Count: > 0 })
        {
            if (_implicitHead && allowed.Contains("GET", StringComparer.Ordinal)
                && !allowed.Contains("HEAD", StringComparer.Ordinal))
            {
                allowed.Add("HEAD");
            }

            allowed.Sort(StringComparer.Ordinal);
            return RouteLookup.MethodNotAllowed(allowed);
        }

        return RouteLookup.NotFound();
    }

    public bool TryMatch(string method, string path, out RouteMatch? match)
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

    internal static int[] ComputePrecedence(IReadOnlyList<SegmentKind> kinds)
    {
        var ranks = new int[kinds.Count];
        for (var i = 0; i < kinds.Count; i++)
        {
            ranks[i] = kinds[i] switch
            {
                SegmentKind.Static => 0,
                SegmentKind.Constrained => 1,
                SegmentKind.Param => 2,
                SegmentKind.CatchAll => 3,
                _ => 2
            };
        }

        return ranks;
    }

    internal enum SegmentKind
    {
        Static = 0,
        Constrained = 1,
        Param = 2,
        CatchAll = 3
    }

    private sealed class PrecedenceComparer : IComparer<int[]>
    {
        public static readonly PrecedenceComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var n = Math.Min(x.Length, y.Length);
            for (var i = 0; i < n; i++)
            {
                var c = x[i].CompareTo(y[i]);
                if (c != 0) return c;
            }

            // Fewer segments preferred when shared prefix equal? Prefer more-specific (longer) when ranks tie on prefix.
            // Actually for match we only compare among routes that already matched the path.
            // For sort order of candidates of different lengths: shorter fixed templates with better ranks come first
            // via segment-by-segment. Remaining length: prefer routes without catch-all already encoded in rank.
            return x.Length.CompareTo(y.Length);
        }
    }

    private sealed class CompiledRoute
    {
        private readonly Segment[] _segments;
        private readonly int _fixedCount;
        private readonly bool _hasCatchAll;
        private readonly RouteConstraintResolver _constraints;

        private CompiledRoute(
            RouteDescriptor descriptor,
            Segment[] segments,
            int[] precedence,
            string?[] staticLiterals,
            RouteConstraintResolver constraints)
        {
            Descriptor = descriptor;
            _segments = segments;
            Precedence = precedence;
            StaticLiterals = staticLiterals;
            _constraints = constraints;
            _hasCatchAll = segments.Length > 0 && segments[^1].Kind == SegmentKind.CatchAll;
            _fixedCount = _hasCatchAll ? segments.Length - 1 : segments.Length;
            RequiredSegmentCount = segments.Count(static s => !s.IsOptional && s.Kind != SegmentKind.CatchAll);
            MaxSegmentCount = _hasCatchAll ? int.MaxValue : segments.Length;
        }

        public RouteDescriptor Descriptor { get; }
        public int[] Precedence { get; }
        /// <summary>Static literal at each segment index, or null for params.</summary>
        public string?[] StaticLiterals { get; }
        public int RequiredSegmentCount { get; }
        public int MaxSegmentCount { get; }
        public Segment[] Segments => _segments;

        public static CompiledRoute Compile(RouteDescriptor descriptor, RouteConstraintResolver constraints)
        {
            var raw = descriptor.Template.Trim('/');
            if (raw.Length == 0)
            {
                return new CompiledRoute(
                    descriptor,
                    Array.Empty<Segment>(),
                    Array.Empty<int>(),
                    Array.Empty<string?>(),
                    constraints);
            }

            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var segments = new Segment[parts.Length];
            var kinds = new SegmentKind[parts.Length];
            var statics = new string?[parts.Length];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sawOptional = false;

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

                        if (sawOptional)
                        {
                            throw new InvalidOperationException(
                                $"Catch-all cannot follow optional parameters in template '{descriptor.Template}'.");
                        }

                        inner = inner[1..];
                        if (inner.Length == 0)
                        {
                            throw new InvalidOperationException(
                                $"Catch-all parameter in template '{descriptor.Template}' must have a name (e.g. {{*path}}).");
                        }

                        if (inner.Contains(':') || inner.Contains('?') || inner.Contains('='))
                        {
                            throw new InvalidOperationException(
                                $"Catch-all parameter '{{{part[1..^1]}}}' does not support constraints, optionals, or defaults.");
                        }

                        if (!names.Add(inner))
                        {
                            throw new InvalidOperationException(
                                $"Duplicate route parameter '{inner}' in template '{descriptor.Template}'.");
                        }

                        segments[i] = Segment.CatchAll(inner);
                        kinds[i] = SegmentKind.CatchAll;
                    }
                    else
                    {
                        string? constraint = null;
                        string? defaultValue = null;
                        var optional = false;

                        // Parse: name[:constraint][?] or name[=default]
                        var namePart = inner;
                        var eq = namePart.IndexOf('=');
                        if (eq >= 0)
                        {
                            defaultValue = namePart[(eq + 1)..];
                            namePart = namePart[..eq];
                            optional = true;
                        }
                        else if (namePart.EndsWith('?'))
                        {
                            optional = true;
                            namePart = namePart[..^1];
                        }

                        var colon = namePart.IndexOf(':');
                        string name;
                        if (colon > 0)
                        {
                            name = namePart[..colon];
                            constraint = namePart[(colon + 1)..];
                        }
                        else
                        {
                            name = namePart;
                        }

                        if (string.IsNullOrWhiteSpace(name))
                        {
                            throw new InvalidOperationException(
                                $"Empty parameter name in template '{descriptor.Template}'.");
                        }

                        if (!names.Add(name))
                        {
                            throw new InvalidOperationException(
                                $"Duplicate route parameter '{name}' in template '{descriptor.Template}'.");
                        }

                        if (optional)
                        {
                            // Optional/default only trailing (may have multiple trailing optionals).
                            sawOptional = true;
                        }
                        else if (sawOptional)
                        {
                            throw new InvalidOperationException(
                                $"Optional parameters must be trailing in template '{descriptor.Template}'.");
                        }

                        if (!string.IsNullOrEmpty(constraint))
                        {
                            constraints.ValidateKnown(constraint, descriptor.Template);
                        }

                        var kind = string.IsNullOrEmpty(constraint) ? SegmentKind.Param : SegmentKind.Constrained;
                        segments[i] = Segment.Param(name, constraint, optional, defaultValue);
                        kinds[i] = kind;
                    }
                }
                else
                {
                    if (sawOptional)
                    {
                        throw new InvalidOperationException(
                            $"Static segment '{part}' cannot follow optional parameters in template '{descriptor.Template}'.");
                    }

                    segments[i] = Segment.Static(part);
                    kinds[i] = SegmentKind.Static;
                    statics[i] = part;
                }
            }

            var precedence = ComputePrecedence(kinds);
            return new CompiledRoute(descriptor, segments, precedence, statics, constraints);
        }

        public bool TryMatch(string path, out IReadOnlyDictionary<string, string> values)
        {
            var raw = path.Trim('/');
            string[] parts;
            if (raw.Length == 0)
            {
                parts = Array.Empty<string>();
            }
            else
            {
                parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
            }

            if (_segments.Length == 0)
            {
                if (parts.Length == 0)
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return true;
                }

                values = null!;
                return false;
            }

            if (_hasCatchAll)
            {
                if (parts.Length < _fixedCount)
                {
                    values = null!;
                    return false;
                }

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < _fixedCount; i++)
                {
                    if (!MatchSegment(_segments[i], parts[i], map))
                    {
                        values = null!;
                        return false;
                    }
                }

                var catchAll = _segments[^1];
                if (parts.Length == _fixedCount)
                {
                    map[catchAll.Name!] = string.Empty;
                }
                else
                {
                    var rest = new string[parts.Length - _fixedCount];
                    for (var i = 0; i < rest.Length; i++)
                    {
                        rest[i] = Uri.UnescapeDataString(parts[_fixedCount + i]);
                    }

                    map[catchAll.Name!] = string.Join('/', rest);
                }

                values = map;
                return true;
            }

            // Non-catch-all: allow fewer parts when trailing optionals present.
            if (parts.Length > _segments.Length || parts.Length < RequiredSegmentCount)
            {
                values = null!;
                return false;
            }

            var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _segments.Length; i++)
            {
                var segment = _segments[i];
                if (i < parts.Length)
                {
                    if (!MatchSegment(segment, parts[i], exact))
                    {
                        values = null!;
                        return false;
                    }
                }
                else
                {
                    // Optional remainder
                    if (!segment.IsOptional)
                    {
                        values = null!;
                        return false;
                    }

                    if (segment.DefaultValue is not null)
                    {
                        exact[segment.Name!] = segment.DefaultValue;
                    }
                }
            }

            values = exact;
            return true;
        }

        private bool MatchSegment(Segment segment, string rawPart, Dictionary<string, string> map)
        {
            var value = Uri.UnescapeDataString(rawPart);

            if (segment.Kind == SegmentKind.Static)
            {
                return string.Equals(value, segment.Literal, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrEmpty(segment.Constraint)
                && !_constraints.Matches(segment.Constraint!, value))
            {
                return false;
            }

            map[segment.Name!] = value;
            return true;
        }
    }

    internal readonly struct Segment
    {
        private Segment(SegmentKind kind, string? literal, string? name, string? constraint, bool optional, string? defaultValue)
        {
            Kind = kind;
            Literal = literal;
            Name = name;
            Constraint = constraint;
            IsOptional = optional;
            DefaultValue = defaultValue;
        }

        public SegmentKind Kind { get; }
        public string? Literal { get; }
        public string? Name { get; }
        public string? Constraint { get; }
        public bool IsOptional { get; }
        public string? DefaultValue { get; }

        public static Segment Static(string literal) =>
            new(SegmentKind.Static, literal, null, null, false, null);

        public static Segment Param(string name, string? constraint, bool optional, string? defaultValue) =>
            new(string.IsNullOrEmpty(constraint) ? SegmentKind.Param : SegmentKind.Constrained,
                null, name, constraint, optional, defaultValue);

        public static Segment CatchAll(string name) =>
            new(SegmentKind.CatchAll, null, name, null, false, null);
    }

    /// <summary>Expose compile for RouteTable ambiguity checks.</summary>
    internal static (int[] Precedence, string?[] Statics, string Template, string Method) Inspect(
        RouteDescriptor descriptor,
        RouteConstraintResolver constraints)
    {
        var c = CompiledRoute.Compile(descriptor, constraints);
        return (c.Precedence, c.StaticLiterals, c.Descriptor.Template, c.Descriptor.Method);
    }
}

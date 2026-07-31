namespace Elsie.Routing;

/// <summary>
/// Segment matcher with deterministic precedence.
/// Per-segment rank: static (0) &gt; constrained-param (1) &gt; param (2) &gt; catch-all (3).
/// Owned by <see cref="RouteTable"/> — not a public seam.
/// </summary>
internal sealed class RouteMatcher
{
    private static readonly IReadOnlyDictionary<string, string> EmptyRouteValues =
        new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

    private readonly bool _implicitHead;
    private readonly CompiledRoute[] _dynamic;
    private readonly CompiledRoute[] _emptyCandidates;
    private readonly Dictionary<string, CompiledRoute[]> _staticBuckets;

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
        var ordered = compiled
            .OrderBy(static r => r.Precedence, PrecedenceComparer.Instance)
            .ThenBy(static r => r.Descriptor.Template, StringComparer.Ordinal)
            .ThenBy(static r => r.Descriptor.Method, StringComparer.Ordinal)
            .ToArray();

        var dynamic = new List<CompiledRoute>();
        var empty = new List<CompiledRoute>();
        var buckets = new Dictionary<string, List<CompiledRoute>>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in ordered)
        {
            if (route.Segments.Length == 0)
            {
                empty.Add(route);
                continue;
            }

            var first = route.Segments[0];
            if (first.Kind == SegmentKind.Static)
            {
                var key = first.Literal!;
                if (!buckets.TryGetValue(key, out var list))
                {
                    buckets[key] = list = [];
                }

                list.Add(route);
            }
            else
            {
                dynamic.Add(route);
            }
        }

        _dynamic = dynamic.ToArray();
        _emptyCandidates = MergePreserveOrder(empty, dynamic);
        _staticBuckets = new Dictionary<string, CompiledRoute[]>(buckets.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in buckets)
        {
            _staticBuckets[key] = MergePreserveOrder(list, dynamic);
        }
    }

    public RouteLookup Lookup(string method, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        method = method.ToUpperInvariant();

        var pathValue = ElsieRequest.NormalizePath(path);
        var parts = SplitPath(pathValue);
        var candidates = SelectCandidates(parts);

        CompiledRoute? best = null;
        IReadOnlyDictionary<string, string>? bestValues = null;
        List<string>? allowed = null;

        foreach (var route in candidates)
        {
            if (!route.TryMatch(parts, out var values))
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
            foreach (var route in candidates)
            {
                if (!string.Equals(route.Descriptor.Method, "GET", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!route.TryMatch(parts, out var values))
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

    private CompiledRoute[] SelectCandidates(string[] parts)
    {
        if (parts.Length == 0)
        {
            return _emptyCandidates;
        }

        var first = DecodeIfNeeded(parts[0]);
        if (_staticBuckets.TryGetValue(first, out var bucket))
        {
            return bucket;
        }

        return _dynamic;
    }

    private static string[] SplitPath(string path)
    {
        var raw = path.Trim('/');
        if (raw.Length == 0)
        {
            return [];
        }

        return raw.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string DecodeIfNeeded(string raw) =>
        raw.Contains('%') ? Uri.UnescapeDataString(raw) : raw;

    /// <summary>
    /// Merge two precedence-sorted lists into one sorted array (stable by original order).
    /// </summary>
    private static CompiledRoute[] MergePreserveOrder(List<CompiledRoute> primary, List<CompiledRoute> dynamic)
    {
        if (dynamic.Count == 0)
        {
            return primary.ToArray();
        }

        if (primary.Count == 0)
        {
            return dynamic.ToArray();
        }

        // Re-sort union with same comparer as global order.
        var all = new List<CompiledRoute>(primary.Count + dynamic.Count);
        all.AddRange(primary);
        all.AddRange(dynamic);
        return all
            .OrderBy(static r => r.Precedence, PrecedenceComparer.Instance)
            .ThenBy(static r => r.Descriptor.Template, StringComparer.Ordinal)
            .ThenBy(static r => r.Descriptor.Method, StringComparer.Ordinal)
            .ToArray();
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

            return x.Length.CompareTo(y.Length);
        }
    }

    private sealed class CompiledRoute
    {
        private readonly Segment[] _segments;
        private readonly int _fixedCount;
        private readonly bool _hasCatchAll;
        private readonly int _paramCount;

        private CompiledRoute(
            RouteDescriptor descriptor,
            Segment[] segments,
            int[] precedence,
            string?[] staticLiterals)
        {
            Descriptor = descriptor;
            _segments = segments;
            Precedence = precedence;
            StaticLiterals = staticLiterals;
            _hasCatchAll = segments.Length > 0 && segments[^1].Kind == SegmentKind.CatchAll;
            _fixedCount = _hasCatchAll ? segments.Length - 1 : segments.Length;
            RequiredSegmentCount = segments.Count(static s => !s.IsOptional && s.Kind != SegmentKind.CatchAll);
            _paramCount = 0;
            foreach (var s in segments)
            {
                if (s.Kind != SegmentKind.Static)
                {
                    _paramCount++;
                }
            }
        }

        public RouteDescriptor Descriptor { get; }
        public int[] Precedence { get; }
        /// <summary>Static literal at each segment index, or null for params.</summary>
        public string?[] StaticLiterals { get; }
        public int RequiredSegmentCount { get; }
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
                    Array.Empty<string?>());
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

                        ElsieRouteConstraint? predicate = null;
                        if (!string.IsNullOrEmpty(constraint))
                        {
                            if (!constraints.TryCreate(constraint, out predicate, out var error))
                            {
                                throw new InvalidOperationException(
                                    $"Unknown or invalid route constraint '{constraint}' in template '{descriptor.Template}'. {error}");
                            }
                        }

                        var kind = string.IsNullOrEmpty(constraint) ? SegmentKind.Param : SegmentKind.Constrained;
                        segments[i] = Segment.Param(name, optional, defaultValue, predicate);
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
            return new CompiledRoute(descriptor, segments, precedence, statics);
        }

        public bool TryMatch(string[] parts, out IReadOnlyDictionary<string, string> values)
        {
            if (_segments.Length == 0)
            {
                if (parts.Length == 0)
                {
                    values = EmptyRouteValues;
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

                var map = new Dictionary<string, string>(_paramCount, StringComparer.OrdinalIgnoreCase);
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
                    map[catchAll.Name!] = DecodeCatchAll(parts, _fixedCount);
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

            // All-static and exact segment count → empty values if no params.
            if (_paramCount == 0)
            {
                for (var i = 0; i < _segments.Length; i++)
                {
                    if (!MatchStaticSegment(_segments[i], parts[i]))
                    {
                        values = null!;
                        return false;
                    }
                }

                values = EmptyRouteValues;
                return true;
            }

            var exact = new Dictionary<string, string>(_paramCount, StringComparer.OrdinalIgnoreCase);
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

        private static bool MatchSegment(Segment segment, string rawPart, Dictionary<string, string>? map)
        {
            if (segment.Kind == SegmentKind.Static)
            {
                return MatchStaticSegment(segment, rawPart);
            }

            var value = DecodeIfNeeded(rawPart);
            if (segment.Predicate is not null && !segment.Predicate(value))
            {
                return false;
            }

            map![segment.Name!] = value;
            return true;
        }

        private static bool MatchStaticSegment(Segment segment, string rawPart)
        {
            return string.Equals(DecodeIfNeeded(rawPart), segment.Literal, StringComparison.OrdinalIgnoreCase);
        }

        private static string DecodeCatchAll(string[] parts, int start)
        {
            if (start == parts.Length - 1)
            {
                return DecodeIfNeeded(parts[start]);
            }

            var builder = new System.Text.StringBuilder();
            for (var i = start; i < parts.Length; i++)
            {
                if (i > start)
                {
                    builder.Append('/');
                }

                builder.Append(DecodeIfNeeded(parts[i]));
            }

            return builder.ToString();
        }
    }

    internal readonly struct Segment
    {
        private Segment(
            SegmentKind kind,
            string? literal,
            string? name,
            bool optional,
            string? defaultValue,
            ElsieRouteConstraint? predicate)
        {
            Kind = kind;
            Literal = literal;
            Name = name;
            IsOptional = optional;
            DefaultValue = defaultValue;
            Predicate = predicate;
        }

        public SegmentKind Kind { get; }
        public string? Literal { get; }
        public string? Name { get; }
        public bool IsOptional { get; }
        public string? DefaultValue { get; }
        public ElsieRouteConstraint? Predicate { get; }

        public static Segment Static(string literal) =>
            new(SegmentKind.Static, literal, null, false, null, null);

        public static Segment Param(
            string name,
            bool optional,
            string? defaultValue,
            ElsieRouteConstraint? predicate) =>
            new(predicate is null ? SegmentKind.Param : SegmentKind.Constrained,
                null, name, optional, defaultValue, predicate);

        public static Segment CatchAll(string name) =>
            new(SegmentKind.CatchAll, null, name, false, null, null);
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

using System.Globalization;
using System.Text.RegularExpressions;

namespace Elsie.Routing;

/// <summary>Built-in + custom route constraint resolver used at compile and match time.</summary>
internal sealed class RouteConstraintResolver
{
    private readonly IReadOnlyDictionary<string, ElsieRouteConstraint> _custom;

    public RouteConstraintResolver(IDictionary<string, ElsieRouteConstraint>? custom = null)
    {
        _custom = custom is null
            ? new Dictionary<string, ElsieRouteConstraint>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ElsieRouteConstraint>(custom, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Validate that a constraint expression is known. Throws on unknown names.</summary>
    public void ValidateKnown(string expression, string template)
    {
        if (!TryCreate(expression, out _, out var error))
        {
            throw new InvalidOperationException(
                $"Unknown or invalid route constraint '{expression}' in template '{template}'. {error}");
        }
    }

    public bool Matches(string expression, string value)
    {
        if (!TryCreate(expression, out var predicate, out _))
        {
            // Should have been caught at startup.
            return false;
        }

        return predicate(value);
    }

    public bool TryCreate(string expression, out ElsieRouteConstraint predicate, out string? error)
    {
        predicate = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Constraint expression is empty.";
            return false;
        }

        var name = expression;
        string? args = null;
        var paren = expression.IndexOf('(');
        if (paren >= 0)
        {
            if (!expression.EndsWith(')'))
            {
                error = "Constraint arguments must be closed with ')'.";
                return false;
            }

            name = expression[..paren];
            args = expression[(paren + 1)..^1];
        }

        name = name.Trim();
        if (_custom.TryGetValue(name, out var custom))
        {
            if (args is not null)
            {
                error = $"Custom constraint '{name}' does not accept arguments.";
                return false;
            }

            predicate = custom;
            return true;
        }

        switch (name.ToLowerInvariant())
        {
            case "int":
                predicate = static v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                return true;
            case "long":
                predicate = static v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                return true;
            case "guid":
                predicate = static v => Guid.TryParse(v, out _);
                return true;
            case "bool":
                predicate = static v => bool.TryParse(v, out _);
                return true;
            case "alpha":
                predicate = static v => v.Length > 0 && v.All(static c => char.IsLetter(c));
                return true;
            case "datetime":
                predicate = static v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
                    || DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
                return true;
            case "decimal":
                predicate = static v => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
                return true;
            case "double":
                predicate = static v => double.TryParse(v, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _);
                return true;
            case "minlength":
            {
                if (!TryParseIntArg(args, out var n, out error)) return false;
                predicate = v => v.Length >= n;
                return true;
            }
            case "maxlength":
            {
                if (!TryParseIntArg(args, out var n, out error)) return false;
                predicate = v => v.Length <= n;
                return true;
            }
            case "length":
            {
                if (args is null)
                {
                    error = "length constraint requires length(n) or length(min,max).";
                    return false;
                }

                var parts = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 1)
                {
                    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact))
                    {
                        error = $"Invalid length argument '{args}'.";
                        return false;
                    }

                    predicate = v => v.Length == exact;
                    return true;
                }

                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
                {
                    predicate = v => v.Length >= min && v.Length <= max;
                    return true;
                }

                error = $"Invalid length argument '{args}'.";
                return false;
            }
            case "min":
            {
                if (!TryParseLongArg(args, out var n, out error)) return false;
                predicate = v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x >= n;
                return true;
            }
            case "max":
            {
                if (!TryParseLongArg(args, out var n, out error)) return false;
                predicate = v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x <= n;
                return true;
            }
            case "range":
            {
                if (args is null)
                {
                    error = "range constraint requires range(min,max).";
                    return false;
                }

                var parts = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2
                    || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)
                    || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
                {
                    error = $"Invalid range argument '{args}'.";
                    return false;
                }

                predicate = v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && x >= min && x <= max;
                return true;
            }
            case "regex":
            {
                if (string.IsNullOrEmpty(args))
                {
                    error = "regex constraint requires regex(pattern).";
                    return false;
                }

                Regex rx;
                try
                {
                    rx = new Regex(args, RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    error = $"Invalid regex '{args}': {ex.Message}";
                    return false;
                }

                predicate = v => rx.IsMatch(v);
                return true;
            }
            default:
                error = "Supported: int, long, guid, bool, alpha, datetime, decimal, double, minlength(n), maxlength(n), length(n|min,max), min(n), max(n), range(a,b), regex(...), plus custom RouteConstraints.";
                return false;
        }
    }

    private static bool TryParseIntArg(string? args, out int value, out string? error)
    {
        value = 0;
        if (args is null || !int.TryParse(args.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"Expected integer argument, got '{args}'.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseLongArg(string? args, out long value, out string? error)
    {
        value = 0;
        if (args is null || !long.TryParse(args.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"Expected integer argument, got '{args}'.";
            return false;
        }

        error = null;
        return true;
    }
}

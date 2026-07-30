using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace Elsie.Binding;

/// <summary>Invariant-culture converters for route/query/form scalar binding.</summary>
internal static class ElsieValueConverters
{
    private static readonly ConcurrentDictionary<Type, Func<string, object?>> s_converters = new();

    public static bool TryConvert(Type targetType, string? raw, out object? value, out string? error)
    {
        value = null;
        error = null;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (raw is null)
        {
            if (underlying != targetType || !targetType.IsValueType)
            {
                value = null;
                return true; // null ok for nullable/ref
            }

            error = "Value is required.";
            return false;
        }

        if (underlying == typeof(string))
        {
            value = raw;
            return true;
        }

        var converter = s_converters.GetOrAdd(underlying, CreateConverter);
        value = converter(raw);
        if (value is null)
        {
            error = $"Cannot convert '{raw}' to {underlying.Name}.";
            return false;
        }

        return true;
    }

    private static Func<string, object?> CreateConverter(Type type)
    {
        if (type == typeof(string)) return static s => s;
        if (type == typeof(bool)) return static s => bool.TryParse(s, out var v) ? v : null;
        if (type == typeof(byte)) return static s => byte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(sbyte)) return static s => sbyte.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(short)) return static s => short.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(ushort)) return static s => ushort.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(int)) return static s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(uint)) return static s => uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(long)) return static s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(ulong)) return static s => ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(float)) return static s => float.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(double)) return static s => double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(decimal)) return static s => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type == typeof(Guid)) return static s => Guid.TryParse(s, out var v) ? v : null;
        if (type == typeof(DateTime)) return static s => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v) ? v : null;
        if (type == typeof(DateTimeOffset)) return static s => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v) ? v : null;
        if (type == typeof(DateOnly)) return static s => DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;
        if (type == typeof(TimeOnly)) return static s => TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;
        if (type == typeof(TimeSpan)) return static s => TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (type.IsEnum)
        {
            return s => Enum.TryParse(type, s, ignoreCase: true, out var e) ? e : null;
        }

        return s =>
        {
            try
            {
                return Convert.ChangeType(s, type, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        };
    }
}

/// <summary>Reflection binder for query/route/form POCOs with cached setters.</summary>
internal static class ElsieObjectBinder
{
    private static readonly ConcurrentDictionary<Type, PropertySetter[]> s_setters = new();

    public static ElsieBindResult<T> Bind<T>(IReadOnlyDictionary<string, string?> values)
        where T : new()
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var instance = new T();
        var setters = s_setters.GetOrAdd(typeof(T), BuildSetters);

        foreach (var setter in setters)
        {
            values.TryGetValue(setter.Name, out var raw);
            if (raw is null && !values.ContainsKey(setter.Name))
            {
                // missing: leave default unless non-nullable value type without default — still OK (default(TProp))
                continue;
            }

            if (!ElsieValueConverters.TryConvert(setter.PropertyType, raw, out var converted, out var error))
            {
                if (!errors.TryGetValue(setter.Name, out var list))
                {
                    list = [];
                    errors[setter.Name] = list;
                }

                list.Add(error ?? "Invalid value.");
                continue;
            }

            setter.Set(instance, converted);
        }

        if (errors.Count > 0)
        {
            var map = errors.ToDictionary(
                static kv => kv.Key,
                static kv => kv.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
            return ElsieBindResult<T>.Fail(ElsieResult.ValidationProblem(map));
        }

        return ElsieBindResult<T>.Success(instance);
    }

    private static PropertySetter[] BuildSetters(Type type)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .Select(static p => new PropertySetter(p))
            .ToArray();
    }

    private sealed class PropertySetter
    {
        private readonly PropertyInfo _property;

        public PropertySetter(PropertyInfo property)
        {
            _property = property;
            Name = property.Name;
            PropertyType = property.PropertyType;
        }

        public string Name { get; }
        public Type PropertyType { get; }

        public void Set(object target, object? value) => _property.SetValue(target, value);
    }
}

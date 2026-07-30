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
        try
        {
            value = converter(raw);
            if (value is null && underlying.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                error = $"Cannot convert '{raw}' to {underlying.Name}.";
                return false;
            }

            return true;
        }
        catch
        {
            error = $"Cannot convert '{raw}' to {underlying.Name}.";
            return false;
        }
    }

    private static Func<string, object?> CreateConverter(Type type)
    {
        if (type == typeof(string)) return static s => s;
        if (type == typeof(bool)) return static s => bool.Parse(s);
        if (type == typeof(byte)) return static s => byte.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(sbyte)) return static s => sbyte.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(short)) return static s => short.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(ushort)) return static s => ushort.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(int)) return static s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(uint)) return static s => uint.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(long)) return static s => long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(ulong)) return static s => ulong.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (type == typeof(float)) return static s => float.Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return static s => double.Parse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        if (type == typeof(decimal)) return static s => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);
        if (type == typeof(Guid)) return static s => Guid.Parse(s);
        if (type == typeof(DateTime)) return static s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTimeOffset)) return static s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateOnly)) return static s => DateOnly.Parse(s, CultureInfo.InvariantCulture);
        if (type == typeof(TimeOnly)) return static s => TimeOnly.Parse(s, CultureInfo.InvariantCulture);
        if (type == typeof(TimeSpan)) return static s => TimeSpan.Parse(s, CultureInfo.InvariantCulture);
        if (type.IsEnum)
        {
            return s => Enum.Parse(type, s, ignoreCase: true);
        }

        return s => Convert.ChangeType(s, type, CultureInfo.InvariantCulture);
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

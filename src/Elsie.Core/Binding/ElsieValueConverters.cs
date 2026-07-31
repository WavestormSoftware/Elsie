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

        // Unknown types fail conversion (no Convert.ChangeType catch-all).
        return static _ => null;
    }
}

/// <summary>Reflection binder for query/route/form POCOs with cached setters.</summary>
internal static class ElsieObjectBinder
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_setters = new();

    public static ElsieBindResult<T> Bind<T>(IReadOnlyDictionary<string, string?> values)
        where T : new()
    {
        var multi = new Dictionary<string, IReadOnlyList<string>>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            multi[k] = v is null ? Array.Empty<string>() : new[] { v };
        }

        return Bind<T>(multi);
    }

    public static ElsieBindResult<T> Bind<T>(IReadOnlyDictionary<string, IReadOnlyList<string>> values)
        where T : new()
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var instance = new T();
        var setters = s_setters.GetOrAdd(typeof(T), BuildSetters);

        foreach (var prop in setters)
        {
            values.TryGetValue(prop.Name, out var rawList);
            var missing = rawList is null && !values.ContainsKey(prop.Name);

            if (IsStringCollection(prop.PropertyType, out var collKind, out var elemType))
            {
                var items = rawList ?? Array.Empty<string>();
                if (!TryConvertCollection(collKind, elemType, items, out var converted, out var error))
                {
                    if (!errors.TryGetValue(prop.Name, out var list))
                    {
                        list = [];
                        errors[prop.Name] = list;
                    }

                    list.Add(error ?? "Invalid value.");
                    continue;
                }

                prop.SetValue(instance, converted);
                continue;
            }

            if (missing)
            {
                // missing scalar: leave default
                continue;
            }

            var raw = rawList is { Count: > 0 } ? rawList[0] : null;
            if (!ElsieValueConverters.TryConvert(prop.PropertyType, raw, out var convertedScalar, out var scalarError))
            {
                if (!errors.TryGetValue(prop.Name, out var list))
                {
                    list = [];
                    errors[prop.Name] = list;
                }

                list.Add(scalarError ?? "Invalid value.");
                continue;
            }

            prop.SetValue(instance, convertedScalar);
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

    private enum CollectionKind { Array, List }

    private static bool IsStringCollection(Type type, out CollectionKind kind, out Type elemType)
    {
        if (type.IsArray)
        {
            kind = CollectionKind.Array;
            elemType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            kind = CollectionKind.List;
            elemType = type.GetGenericArguments()[0];
            return true;
        }

        kind = default;
        elemType = null!;
        return false;
    }

    private static bool TryConvertCollection(
        CollectionKind kind,
        Type elemType,
        IReadOnlyList<string> items,
        out object? converted,
        out string? error)
    {
        error = null;
        if (elemType == typeof(string))
        {
            if (kind == CollectionKind.Array)
            {
                var arr = new string[items.Count];
                for (var i = 0; i < items.Count; i++) arr[i] = items[i];
                converted = arr;
                return true;
            }

            var list = new List<string>(items.Count);
            list.AddRange(items);
            converted = list;
            return true;
        }

        var values = new object?[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            if (!ElsieValueConverters.TryConvert(elemType, items[i], out var v, out error))
            {
                converted = null;
                return false;
            }

            values[i] = v;
        }

        if (kind == CollectionKind.Array)
        {
            var arr = Array.CreateInstance(elemType, values.Length);
            for (var i = 0; i < values.Length; i++) arr.SetValue(values[i], i);
            converted = arr;
            return true;
        }

        var listType = typeof(List<>).MakeGenericType(elemType);
        var listObj = (System.Collections.IList)Activator.CreateInstance(listType)!;
        foreach (var v in values) listObj.Add(v);
        converted = listObj;
        return true;
    }

    private static PropertyInfo[] BuildSetters(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToArray();
}

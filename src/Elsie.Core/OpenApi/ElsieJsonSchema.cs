using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Elsie.OpenApi;

/// <summary>
/// Reflection-based JSON Schema subset for OpenAPI (primitives, enums, arrays, nested DTOs, NRT required).
/// Cycle detection throws a clear error at document build time.
/// </summary>
internal static class ElsieJsonSchema
{
    private static readonly ConcurrentDictionary<Type, string> ComponentNames = new();

    public static Dictionary<string, object> BuildComponentsSchemas(
        IEnumerable<Type> types,
        out Dictionary<Type, string> typeToName)
    {
        typeToName = new Dictionary<Type, string>();
        var components = new Dictionary<string, object>(StringComparer.Ordinal);
        var visiting = new HashSet<Type>();

        foreach (var type in types.Distinct())
        {
            EnsureSchema(type, components, typeToName, visiting, root: true);
        }

        return components;
    }

    public static Dictionary<string, object> RefOrInline(
        Type type,
        Dictionary<string, object> components,
        Dictionary<Type, string> typeToName)
    {
        // object is not a component schema (EnsureSchema no-ops); inline as free-form object.
        if (IsSimple(type) || type == typeof(object) || IsEnumerable(type, out _) ||
            Nullable.GetUnderlyingType(type) is not null)
        {
            return CreateSchema(type, components, typeToName, new HashSet<Type>(), forceInline: true);
        }

        if (!typeToName.TryGetValue(type, out var name))
        {
            EnsureSchema(type, components, typeToName, new HashSet<Type>(), root: true);
            if (!typeToName.TryGetValue(type, out name))
            {
                // Fallback if EnsureSchema skipped the type (e.g. open object).
                return CreateSchema(type, components, typeToName, new HashSet<Type>(), forceInline: true);
            }
        }

        return Dict(("$ref", "#/components/schemas/" + name));
    }

    private static void EnsureSchema(
        Type type,
        Dictionary<string, object> components,
        Dictionary<Type, string> typeToName,
        HashSet<Type> visiting,
        bool root)
    {
        type = Unwrap(type);
        if (IsSimple(type) || type == typeof(object))
        {
            return;
        }

        if (IsEnumerable(type, out var element) && element is not null)
        {
            EnsureSchema(element, components, typeToName, visiting, root: false);
            return;
        }

        // Completed schema available for $ref — but a type still in `visiting` is a cycle.
        if (visiting.Contains(type))
        {
            throw new InvalidOperationException(
                $"OpenAPI schema cycle detected at type '{type.FullName}'. " +
                "Break the cycle or project a DTO without recursive references.");
        }

        if (typeToName.ContainsKey(type))
        {
            return;
        }

        visiting.Add(type);
        try
        {
            var name = ComponentNames.GetOrAdd(type, static t => MakeComponentName(t));
            // Reserve name before recursing into properties.
            typeToName[type] = name;
            var schema = CreateObjectSchema(type, components, typeToName, visiting);
            components[name] = schema;
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    private static Dictionary<string, object> CreateSchema(
        Type type,
        Dictionary<string, object> components,
        Dictionary<Type, string> typeToName,
        HashSet<Type> visiting,
        bool forceInline)
    {
        type = Unwrap(type);

        if (IsEnumerable(type, out var element) && element is not null)
        {
            return Dict(
                ("type", "array"),
                ("items", forceInline || IsSimple(element)
                    ? CreateSchema(element, components, typeToName, visiting, forceInline: true)
                    : RefOrInline(element, components, typeToName)));
        }

        if (IsSimple(type))
        {
            return SimpleSchema(type);
        }

        if (forceInline)
        {
            return CreateObjectSchema(type, components, typeToName, visiting);
        }

        return RefOrInline(type, components, typeToName);
    }

    private static Dictionary<string, object> CreateObjectSchema(
        Type type,
        Dictionary<string, object> components,
        Dictionary<Type, string> typeToName,
        HashSet<Type> visiting)
    {
        var props = new Dictionary<string, object>(StringComparer.Ordinal);
        var required = new List<string>();

        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
            {
                continue;
            }

            var name = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                       ?? ToCamelCase(prop.Name);
            var propType = prop.PropertyType;
            var underlying = Nullable.GetUnderlyingType(propType);
            var isNullableRef = !propType.IsValueType && IsNullableReference(prop);
            var isOptional = underlying is not null || isNullableRef;

            if (underlying is not null)
            {
                propType = underlying;
            }

            if (!IsSimple(propType) && !(IsEnumerable(propType, out _)))
            {
                EnsureSchema(propType, components, typeToName, visiting, root: false);
            }
            else if (IsEnumerable(propType, out var el) && el is not null && !IsSimple(el))
            {
                EnsureSchema(el, components, typeToName, visiting, root: false);
            }

            props[name] = CreateSchema(propType, components, typeToName, visiting, forceInline: IsSimple(propType) || IsEnumerable(propType, out _));

            if (!isOptional)
            {
                required.Add(name);
            }
        }

        var schema = Dict(("type", "object"), ("properties", props));
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static Dictionary<string, object> SimpleSchema(Type type)
    {
        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            return Dict(("type", "string"), ("enum", names));
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => Dict(("type", "boolean")),
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
                or TypeCode.Int32 or TypeCode.UInt32 => Dict(("type", "integer"), ("format", "int32")),
            TypeCode.Int64 or TypeCode.UInt64 => Dict(("type", "integer"), ("format", "int64")),
            TypeCode.Single => Dict(("type", "number"), ("format", "float")),
            TypeCode.Double or TypeCode.Decimal => Dict(("type", "number"), ("format", "double")),
            TypeCode.DateTime => Dict(("type", "string"), ("format", "date-time")),
            TypeCode.String => Dict(("type", "string")),
            _ when type == typeof(Guid) => Dict(("type", "string"), ("format", "uuid")),
            _ when type == typeof(DateTimeOffset) => Dict(("type", "string"), ("format", "date-time")),
            _ when type == typeof(DateOnly) => Dict(("type", "string"), ("format", "date")),
            _ when type == typeof(TimeOnly) => Dict(("type", "string"), ("format", "time")),
            _ when type == typeof(byte[]) => Dict(("type", "string"), ("format", "byte")),
            _ when type == typeof(Uri) => Dict(("type", "string"), ("format", "uri")),
            _ => Dict(("type", "object"))
        };
    }

    private static bool IsSimple(Type type)
    {
        type = Unwrap(type);
        if (type.IsEnum || type.IsPrimitive || type == typeof(string) || type == typeof(decimal)
            || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(byte[])
            || type == typeof(Uri))
        {
            return true;
        }

        return false;
    }

    private static bool IsEnumerable(Type type, out Type? elementType)
    {
        elementType = null;
        type = Unwrap(type);
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return true;
        }

        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var args = type.GetGenericArguments();
            if (args.Length == 1)
            {
                elementType = args[0];
                return true;
            }
        }

        return false;
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsNullableReference(PropertyInfo prop)
    {
        var nullability = new NullabilityInfoContext().Create(prop);
        return nullability.ReadState == NullabilityState.Nullable;
    }

    private static string MakeComponentName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var args = string.Join("_", type.GetGenericArguments().Select(MakeComponentName));
        var tick = type.Name.IndexOf('`');
        var root = tick > 0 ? type.Name[..tick] : type.Name;
        return $"{root}Of{args}";
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static Dictionary<string, object> Dict(params (string Key, object Value)[] pairs)
    {
        var d = new Dictionary<string, object>(pairs.Length, StringComparer.Ordinal);
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }

        return d;
    }
}

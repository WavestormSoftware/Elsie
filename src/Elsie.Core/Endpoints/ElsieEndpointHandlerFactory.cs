using System.Linq.Expressions;
using System.Reflection;
using Elsie.Binding;
using Elsie.Routing;
using Elsie.Validation;

namespace Elsie.Endpoints;

/// <summary>
/// Turns a handler delegate into a <see cref="RouteHandler"/>: compiles the invocation, binds
/// parameters (route/query/header/body/services), runs validators, and normalizes the return
/// value into an <see cref="ElsieResult"/>.
/// </summary>
internal static class ElsieEndpointHandlerFactory
{
    private delegate ValueTask<object?> ParameterBinder(ElsieContext context, CancellationToken cancellationToken);
    private delegate Task<BodyBindOutcome> BodyReader(ElsieContext context, CancellationToken cancellationToken);
    private delegate ElsieBindResult<object?> QueryObjectReader(ElsieContext context);
    private delegate ElsieResult? ObjectValidator(ElsieContext context, object value);
    private delegate ValueTask<object?> Awaiter(object value);

    private readonly struct BodyBindOutcome(object? value, ElsieResult? error)
    {
        public object? Value { get; } = value;
        public ElsieResult? Error { get; } = error;
    }

    private sealed class ParameterPlan
    {
        public required ParameterBinder Bind { get; init; }
        public required Type Type { get; init; }
        public required bool FromBody { get; init; }
    }

    private enum Source
    {
        Route,
        Query,
        Header
    }

    public static RouteHandler Create(Delegate handler, string method, RouteDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(descriptor);

        var invokeMethod = handler.GetType().GetMethod("Invoke")
            ?? throw new ArgumentException("Handler must be a delegate type.", nameof(handler));
        var invokeParameters = invokeMethod.GetParameters();
        // Names, attributes and defaults live on the handler's own method (the lambda body), not on
        // the delegate's Invoke signature, whose parameters are unnamed ("arg").
        var declaredParameters = handler.Method.GetParameters();
        var useDeclared = declaredParameters.Length == invokeParameters.Length;

        var plans = new ParameterPlan[invokeParameters.Length];
        for (var i = 0; i < invokeParameters.Length; i++)
        {
            plans[i] = BuildPlan(
                useDeclared ? declaredParameters[i] : invokeParameters[i],
                invokeParameters[i].ParameterType,
                method,
                descriptor.Template);
        }

        var invoker = CompileInvoker(handler, invokeMethod);
        var awaiter = BuildAwaiter(invokeMethod.ReturnType);

        // OpenAPI inference: body parameter → request schema, complex return → 200 response schema.
        foreach (var plan in plans)
        {
            if (plan.FromBody)
            {
                descriptor.AcceptsType ??= plan.Type;
            }
        }

        if (descriptor.Produces.Count == 0 && ResponseTypeOf(invokeMethod.ReturnType) is { } responseType)
        {
            descriptor.AddProduces(responseType, 200);
        }

        return async (context, cancellationToken) =>
        {
            var arguments = new object?[plans.Length];
            try
            {
                for (var i = 0; i < plans.Length; i++)
                {
                    arguments[i] = await plans[i].Bind(context, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (ElsieEndpointBindingException ex)
            {
                return ex.Result;
            }

            object? value;
            try
            {
                value = invoker(arguments);
            }
            catch (ElsieEndpointBindingException ex)
            {
                return ex.Result;
            }

            if (awaiter is not null)
            {
                value = await awaiter(value!).ConfigureAwait(false);
            }

            return await ElsieEndpointResultMapper.ToResultAsync(value, context).ConfigureAwait(false);
        };
    }

    private static ParameterPlan BuildPlan(ParameterInfo parameter, Type type, string method, string template)
    {
        var name = parameter.Name ?? "arg";

        if (type == typeof(ElsieContext))
        {
            return Plan((ctx, _) => new ValueTask<object?>(ctx), type);
        }

        if (type == typeof(CancellationToken))
        {
            return Plan((_, ct) => new ValueTask<object?>(ct), type);
        }

        if (type == typeof(ElsieRequest))
        {
            return Plan((ctx, _) => new ValueTask<object?>(ctx.Request), type);
        }

        if (type == typeof(ElsieResponse))
        {
            return Plan((ctx, _) => new ValueTask<object?>(ctx.Response), type);
        }

        if (type == typeof(IServiceProvider))
        {
            return Plan((ctx, _) => new ValueTask<object?>(ctx.RequestServices), type);
        }

        if (parameter.GetCustomAttribute<FromServicesAttribute>() is not null
            || parameter.GetCustomAttribute<ElsieServiceAttribute>() is not null)
        {
            return Plan(
                (ctx, _) => new ValueTask<object?>(ResolveService(ctx, type, name)),
                type);
        }

        if (parameter.GetCustomAttribute<FromRouteAttribute>() is { } fromRoute)
        {
            return BindSimple(type, fromRoute.Name ?? name, Source.Route, parameter);
        }

        if (parameter.GetCustomAttribute<FromQueryAttribute>() is { } fromQuery)
        {
            return BindSimple(type, fromQuery.Name ?? name, Source.Query, parameter);
        }

        if (parameter.GetCustomAttribute<FromHeaderAttribute>() is { } fromHeader)
        {
            return BindSimple(type, fromHeader.Name ?? name, Source.Header, parameter);
        }

        if (parameter.GetCustomAttribute<FromBodyAttribute>() is not null)
        {
            return BuildBodyPlan(type);
        }

        if (IsSimple(type) || IsSimpleArray(type))
        {
            // Simple values bind from the route when the template names them, else from the query string.
            var source = TemplateHasParameter(template, name) ? Source.Route : Source.Query;
            return BindSimple(type, name, source, parameter);
        }

        // Complex type: request body for methods that carry one, query-object otherwise.
        if (HttpMethods.CanHaveBody(method))
        {
            return BuildBodyPlan(type);
        }

        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException(
                $"Cannot bind handler parameter '{name}' of type '{type.FullName}' from the query string: " +
                "the type needs a public parameterless constructor. Annotate the parameter with " +
                "[FromBody], [FromRoute], [FromQuery], or [ElsieService].");
        }

        return BuildQueryObjectPlan(type);
    }

    private static ParameterPlan Plan(ParameterBinder binder, Type type, bool fromBody = false) =>
        new() { Bind = binder, Type = type, FromBody = fromBody };

    private static ParameterPlan BuildBodyPlan(Type type)
    {
        var reader = (BodyReader)Delegate.CreateDelegate(
            typeof(BodyReader),
            s_readBodyMethod.MakeGenericMethod(type));
        return Plan(
            async (ctx, ct) =>
            {
                var outcome = await reader(ctx, ct).ConfigureAwait(false);
                if (outcome.Error is not null)
                {
                    throw new ElsieEndpointBindingException(outcome.Error);
                }

                return outcome.Value;
            },
            type,
            fromBody: true);
    }

    private static ParameterPlan BuildQueryObjectPlan(Type type)
    {
        var reader = (QueryObjectReader)Delegate.CreateDelegate(
            typeof(QueryObjectReader),
            s_readQueryObjectMethod.MakeGenericMethod(type));
        return Plan(
            (ctx, _) =>
            {
                var bind = reader(ctx);
                if (!bind.IsSuccess)
                {
                    throw new ElsieEndpointBindingException(bind.Error!);
                }

                return new ValueTask<object?>(bind.Value);
            },
            type);
    }

    private static ParameterPlan BindSimple(Type type, string name, Source source, ParameterInfo parameter)
    {
        if (IsSimpleArray(type) && source != Source.Route)
        {
            return BindSimpleArray(type, name, source, parameter);
        }

        var optional = parameter.HasDefaultValue;
        var defaultValue = optional ? parameter.DefaultValue : null;
        var nullable = IsNullable(type);

        return Plan(
            (ctx, _) =>
            {
                var raw = source switch
                {
                    Source.Route => ctx.RouteValues.TryGetValue(name, out var routeValue) ? routeValue : null,
                    Source.Query => ctx.Request.GetQuery(name),
                    Source.Header => ctx.Request.GetHeader(name),
                    _ => null
                };

                if (raw is null)
                {
                    if (optional)
                    {
                        return ValueTask.FromResult<object?>(defaultValue);
                    }

                    if (nullable)
                    {
                        return ValueTask.FromResult<object?>(null);
                    }

                    throw new ElsieEndpointBindingException(ElsieResult.BadRequest(
                        $"Missing required {SourceName(source)} value '{name}'."));
                }

                if (!ElsieValueConverters.TryConvert(type, raw, out var value, out var error))
                {
                    throw new ElsieEndpointBindingException(ElsieResult.BadRequest(
                        error ?? $"{SourceName(source)} value '{name}' must be a valid {Friendly(type)}."));
                }

                return new ValueTask<object?>(value);
            },
            type);
    }

    private static ParameterPlan BindSimpleArray(Type type, string name, Source source, ParameterInfo parameter)
    {
        var elementType = type.GetElementType()!;
        var optional = parameter.HasDefaultValue;

        return Plan(
            (ctx, ct) =>
            {
                _ = ct;
                var values = source == Source.Query
                    ? ctx.Request.GetQueryValues(name)
                    : source == Source.Header
                        ? ctx.Request.GetHeaderValues(name)
                        : [];

                if (values.Count == 0)
                {
                    if (optional)
                    {
                        return ValueTask.FromResult<object?>(parameter.DefaultValue);
                    }

                    return new ValueTask<object?>(Array.CreateInstance(elementType, 0));
                }

                var array = Array.CreateInstance(elementType, values.Count);
                for (var i = 0; i < values.Count; i++)
                {
                    if (!ElsieValueConverters.TryConvert(elementType, values[i], out var converted, out _))
                    {
                        throw new ElsieEndpointBindingException(ElsieResult.BadRequest(
                            $"{SourceName(source)} value '{name}' must be a list of {Friendly(elementType)}."));
                    }

                    array.SetValue(converted, i);
                }

                return new ValueTask<object?>(array);
            },
            type);
    }

    private static object? ResolveService(ElsieContext context, Type type, string name)
    {
        var service = context.RequestServices.GetService(type);
        if (service is null)
        {
            throw new InvalidOperationException(
                $"Unable to resolve service '{type.FullName}' for handler parameter '{name}'.");
        }

        return service;
    }

    // Body + validation helpers (invoked through delegates closed over the parameter type).

    private static async Task<BodyBindOutcome> ReadBodyAsync<T>(ElsieContext context, CancellationToken cancellationToken)
    {
        var contentType = context.Request.ContentType ?? string.Empty;
        var isForm = contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);

        ElsieBindResult<T> bind;
        if (isForm)
        {
            if (typeof(T).GetConstructor(Type.EmptyTypes) is null)
            {
                return new BodyBindOutcome(null, ElsieResult.Problem(
                    415,
                    "Unsupported Media Type",
                    $"Form binding to '{typeof(T).Name}' requires a public parameterless constructor. Send JSON instead."));
            }

            var task = (Task<BodyBindOutcome>)s_readFormMethod
                .MakeGenericMethod(typeof(T))
                .Invoke(null, [context, cancellationToken])!;
            return await task.ConfigureAwait(false);
        }

        bind = await context.BindJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        return bind.IsSuccess
            ? new BodyBindOutcome(bind.Value, Validate<T>(context, bind.Value!))
            : new BodyBindOutcome(null, bind.Error);
    }

    private static async Task<BodyBindOutcome> ReadFormBodyAsync<T>(ElsieContext context, CancellationToken cancellationToken)
        where T : new()
    {
        var bind = await context.BindFormAsync<T>(cancellationToken).ConfigureAwait(false);
        return bind.IsSuccess
            ? new BodyBindOutcome(bind.Value, Validate<T>(context, bind.Value!))
            : new BodyBindOutcome(null, bind.Error);
    }

    private static ElsieBindResult<object?> ReadQueryObject<T>(ElsieContext context)
        where T : new()
    {
        var bind = context.BindQuery<T>();
        if (!bind.IsSuccess)
        {
            return ElsieBindResult<object?>.Fail(bind.Error!);
        }

        var error = Validate<T>(context, bind.Value!);
        return error is null
            ? ElsieBindResult<object?>.Success(bind.Value)
            : ElsieBindResult<object?>.Fail(error);
    }

    private static ElsieResult? Validate<T>(ElsieContext context, object value)
    {
        if (context.RequestServices.GetService(typeof(IElsieValidator<T>)) is not IElsieValidator<T> validator)
        {
            return null;
        }

        var errors = validator.Validate((T)value);
        return errors.Count == 0 ? null : ElsieValidation.ToProblem(errors);
    }

    // Invocation + return-shape helpers.

    private static Func<object?[], object?> CompileInvoker(Delegate handler, MethodInfo invokeMethod)
    {
        var parameters = invokeMethod.GetParameters();
        var arguments = Expression.Parameter(typeof(object[]), "arguments");
        var converted = new Expression[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            converted[i] = Expression.Convert(
                Expression.ArrayIndex(arguments, Expression.Constant(i)),
                parameters[i].ParameterType);
        }

        Expression body = Expression.Invoke(
            Expression.Constant(handler, handler.GetType()),
            converted);

        if (invokeMethod.ReturnType == typeof(void))
        {
            body = Expression.Block(body, Expression.Constant(null, typeof(object)));
        }
        else
        {
            body = Expression.Convert(body, typeof(object));
        }

        return Expression.Lambda<Func<object?[], object?>>(body, arguments).Compile();
    }

    private static Awaiter? BuildAwaiter(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return static value => AwaitPlainTask((Task)value);
        }

        if (returnType == typeof(ValueTask))
        {
            return static value => AwaitPlainValueTask((ValueTask)value);
        }

        if (!returnType.IsGenericType)
        {
            return null;
        }

        var definition = returnType.GetGenericTypeDefinition();
        var resultType = returnType.GetGenericArguments()[0];
        if (definition == typeof(Task<>))
        {
            return (Awaiter)Delegate.CreateDelegate(
                typeof(Awaiter),
                s_awaitTaskOfMethod.MakeGenericMethod(resultType));
        }

        if (definition == typeof(ValueTask<>))
        {
            return (Awaiter)Delegate.CreateDelegate(
                typeof(Awaiter),
                s_awaitValueTaskOfMethod.MakeGenericMethod(resultType));
        }

        return null;
    }

    private static async ValueTask<object?> AwaitPlainTask(Task task)
    {
        await task.ConfigureAwait(false);
        return null;
    }

    private static async ValueTask<object?> AwaitPlainValueTask(ValueTask task)
    {
        await task.ConfigureAwait(false);
        return null;
    }

    private static async ValueTask<object?> AwaitTaskOf<T>(object value) =>
        await ((Task<T>)value).ConfigureAwait(false);

    private static async ValueTask<object?> AwaitValueTaskOf<T>(object value) =>
        await ((ValueTask<T>)value).ConfigureAwait(false);

    private static Type? ResponseTypeOf(Type returnType)
    {
        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return null;
        }

        var candidate = returnType;
        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                candidate = returnType.GetGenericArguments()[0];
            }
        }

        if (candidate == typeof(object)
            || candidate == typeof(string)
            || candidate == typeof(ElsieResult)
            || typeof(IElsieResult).IsAssignableFrom(candidate)
            || IsSimple(candidate))
        {
            return null;
        }

        return candidate;
    }

    private static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(Guid)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(TimeSpan)
            || type == typeof(Uri);
    }

    private static bool IsSimpleArray(Type type) =>
        type.IsArray && type.GetArrayRank() == 1 && IsSimple(type.GetElementType()!);

    private static bool TemplateHasParameter(string template, string name)
    {
        foreach (var segment in template.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length < 3 || segment[0] != '{' || segment[^1] != '}')
            {
                continue;
            }

            var inner = segment[1..^1];
            if (inner.StartsWith("*", StringComparison.Ordinal))
            {
                inner = inner[1..];
            }

            var end = inner.IndexOfAny([':', '=', '?']);
            if (end >= 0)
            {
                inner = inner[..end];
            }

            if (string.Equals(inner, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNullable(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is not null)
        {
            return true;
        }

        // Unconstrained generic parameters and reference types accept null.
        return !type.IsValueType;
    }

    private static string SourceName(Source source) => source switch
    {
        Source.Route => "Route",
        Source.Query => "Query",
        Source.Header => "Header",
        _ => "Request"
    };

    private static string Friendly(Type type) => Nullable.GetUnderlyingType(type)?.Name ?? type.Name;

    private static readonly MethodInfo s_readBodyMethod =
        typeof(ElsieEndpointHandlerFactory).GetMethod(nameof(ReadBodyAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo s_readFormMethod =
        typeof(ElsieEndpointHandlerFactory).GetMethod(nameof(ReadFormBodyAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo s_readQueryObjectMethod =
        typeof(ElsieEndpointHandlerFactory).GetMethod(nameof(ReadQueryObject), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo s_awaitTaskOfMethod =
        typeof(ElsieEndpointHandlerFactory).GetMethod(nameof(AwaitTaskOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo s_awaitValueTaskOfMethod =
        typeof(ElsieEndpointHandlerFactory).GetMethod(nameof(AwaitValueTaskOf), BindingFlags.NonPublic | BindingFlags.Static)!;
}

/// <summary>Signals a binding failure that should be returned to the client as-is.</summary>
internal sealed class ElsieEndpointBindingException(ElsieResult result) : Exception(result.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
{
    public ElsieResult Result { get; } = result;
}

internal static class HttpMethods
{
    public static bool CanHaveBody(string method) =>
        !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);
}

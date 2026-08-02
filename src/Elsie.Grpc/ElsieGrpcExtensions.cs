using System.Reflection;
using Elsie.Web;
using global::Grpc.Core;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Grpc;

/// <summary>gRPC registration extensions for <see cref="ElsieApp"/>.</summary>
public static class ElsieGrpcExtensions
{
    /// <summary>
    /// Registers a generated gRPC service (the codegen base class, e.g. <c>GreeterBase</c>) with
    /// the app. Each service method is exposed as a <c>POST /package.Service/Method</c> route
    /// served over HTTP/2 and HTTP/3 (when the h3 listener is active), with 5-byte framing,
    /// grpc-status / grpc-message trailers, deadline support (grpc-timeout), and the
    /// application/grpc content-type gate.
    /// </summary>
    /// <param name="app">The app to extend.</param>
    /// <param name="fileDescriptor">Optional <see cref="FileDescriptor"/> for the service's proto
    /// file (e.g. <c>GreeterReflection.Descriptor</c>); enables grpcurl reflection.</param>
    /// <param name="configure">Optional configuration.</param>
    public static ElsieApp MapGrpcService<TService>(
        this ElsieApp app,
        FileDescriptor? fileDescriptor = null,
        Action<ElsieGrpcOptions>? configure = null)
        where TService : class, new()
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = new ElsieGrpcOptions();
        configure?.Invoke(options);

        var binder = new ElsieServiceBinder();
        var implementation = new TService();
        BindServiceImplementation(typeof(TService), implementation, binder);

        if (options.EnableReflection)
        {
            RegisterReflection(binder, fileDescriptor);
        }

        app.Services(s => s.AddSingleton(binder));
        app.Services(s => s.AddSingleton(options));
        app.Module<ElsieGrpcModule>();
        return app;
    }

    private static void BindServiceImplementation(Type serviceType, object implementation, ElsieServiceBinder binder)
    {
        var bindMethod = serviceType.GetCustomAttribute<BindServiceMethodAttribute>() is { } attr
            ? attr.BindType.GetMethod(
                attr.BindMethodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(ServiceBinderBase), serviceType },
                modifiers: null)
            : serviceType.GetMethod(
                "BindService",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(ServiceBinderBase), serviceType },
                modifiers: null);

        if (bindMethod is null)
        {
            throw new InvalidOperationException(
                $"The service type '{serviceType.Name}' does not expose a generated " +
                $"BindService(ServiceBinderBase, {serviceType.Name}) method.");
        }

        bindMethod.Invoke(null, [binder, implementation]);
    }

    private static void RegisterReflection(ElsieServiceBinder binder, FileDescriptor? fileDescriptor)
    {
        var descriptors = new Dictionary<string, FileDescriptor?>(StringComparer.Ordinal);
        foreach (var method in binder.Methods)
        {
            var serviceName = method.FullName;
            var dot = serviceName.LastIndexOf('.');
            if (dot > 0)
            {
                serviceName = serviceName[..dot];
            }

            descriptors[serviceName] = fileDescriptor;
        }

        var reflection = new ReflectionServiceImpl(descriptors);
        var reflectionBinder = new ElsieServiceBinder();
        global::Grpc.Reflection.V1Alpha.ServerReflection.BindService(reflectionBinder, reflection);
        foreach (var method in reflectionBinder.Methods)
        {
            binder.Add(method);
        }
    }
}

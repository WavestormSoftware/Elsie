using Elsie;
using Google.Protobuf.Reflection;

namespace Elsie.Grpc;

/// <summary>
/// App-wide gRPC reflection state shared by every <c>MapGrpcService</c> call: accumulates the
/// service-name → file-descriptor mapping (so grpcurl can discover every mapped service) and
/// owns the options the single reflection route uses.
/// </summary>
internal sealed class ElsieGrpcReflectionHost
{
    private readonly Dictionary<string, FileDescriptor?> _descriptors = new(StringComparer.Ordinal);

    public ElsieGrpcReflectionHost(ElsieGrpcOptions options)
    {
        Options = options;
    }

    /// <summary>Options used by the reflection routes (from the first call that enabled reflection).</summary>
    public ElsieGrpcOptions Options { get; }

    /// <summary>Live descriptor mapping (read per reflection request).</summary>
    public IReadOnlyDictionary<string, FileDescriptor?> Descriptors => _descriptors;

    public void AddDescriptor(string serviceName, FileDescriptor? fileDescriptor) =>
        _descriptors[serviceName] = fileDescriptor;
}

/// <summary>
/// Registers the reflection-lite routes (grpc.reflection.v1alpha) exactly once per app. The
/// reflection service is deduplicated across multiple <c>MapGrpcService</c> calls — each mapped
/// service contributes descriptors to the shared <see cref="ElsieGrpcReflectionHost"/> instead
/// of registering its own duplicate ServerReflectionInfo route.
/// </summary>
internal sealed class ElsieGrpcReflectionModule : ElsieModule
{
    public ElsieGrpcReflectionModule(ElsieGrpcReflectionHost host)
    {
        // Register reflection under BOTH grpc.reflection.v1 (grpcurl v1.9+) and v1alpha
        // (legacy / reference clients). Both protos are wire-identical and share the host's
        // LIVE descriptor dictionary (filled by MapGrpcService after this module is built).
        var v1Binder = new ElsieServiceBinder();
        global::Grpc.Reflection.V1.ServerReflection.BindService(
            v1Binder,
            new ReflectionServiceImplV1(host.Descriptors));
        AddReflectionRoutes(v1Binder, host.Options);

        var v1AlphaBinder = new ElsieServiceBinder();
        global::Grpc.Reflection.V1Alpha.ServerReflection.BindService(
            v1AlphaBinder,
            new ReflectionServiceImpl(host.Descriptors));
        AddReflectionRoutes(v1AlphaBinder, host.Options);
    }

    private void AddReflectionRoutes(ElsieServiceBinder binder, ElsieGrpcOptions options)
    {
        foreach (var method in binder.Methods)
        {
            var m = method;
            Map("POST", m.RoutePath, (ctx, ct) => ElsieGrpcModule.HandleGrpcAsync(ctx, m, options, ct));
        }
    }
}


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
        var binder = new ElsieServiceBinder();
        global::Grpc.Reflection.V1Alpha.ServerReflection.BindService(
            binder,
            new ReflectionServiceImpl(host.Descriptors));

        foreach (var method in binder.Methods)
        {
            var m = method;
            Map("POST", m.RoutePath, (ctx, ct) => ElsieGrpcModule.HandleGrpcAsync(ctx, m, host.Options, ct));
        }
    }
}

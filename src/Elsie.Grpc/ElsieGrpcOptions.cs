namespace Elsie.Grpc;

/// <summary>Configuration for <c>MapGrpcService</c>.</summary>
public sealed class ElsieGrpcOptions
{
    /// <summary>Maximum incoming gRPC message size in bytes (default 4 MiB).</summary>
    public int MaxReceiveMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>Maximum outgoing gRPC message size in bytes (default 4 MiB).</summary>
    public int MaxSendMessageSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// When true (default), the reflection-lite service (grpc.reflection.v1alpha) is exposed so
    /// tools like grpcurl can discover registered services.
    /// </summary>
    public bool EnableReflection { get; set; } = true;

    /// <summary>Whether WriteResponseHeadersAsync
    /// actually adds headers to the HTTP response (default true).</summary>
    public bool WriteResponseHeaders { get; set; } = true;
}

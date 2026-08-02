# gRPC

Elsie hosts native gRPC servers through the **`Elsie.Grpc`** package — no ASP.NET, no Kestrel,
no adapter. The generated `Grpc.Core` service binders run on the same `HostDispatch` pipeline
as every other route, over **HTTP/2** and **HTTP/3** (when the h3 listener is active).

## Setup

```xml
<PackageReference Include="Elsie.Grpc" Version="0.4.0-beta" />
```

```csharp
ElsieApp.Create(args)
    .Listen(IPAddress.Any, 443, o =>
    {
        o.UseHttps = true;
        o.CertificateFromPfx("/etc/elsie/cert.pfx");
        o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
        o.EnableHttp3 = true;   // gRPC over h3 when libmsquic is present
    })
    .MapGrpcService<GreeterServiceImpl>(fileDescriptor: GreeterReflection.Descriptor)
    .Run();
```

The proto file is compiled at build time by `Grpc.Tools` (`GrpcServices="Server"` or `"Both"`).

```xml
<Protobuf Include="greet.proto" GrpcServices="Server" />
```

## What is implemented

- **`ElsieServiceBinder`** — a `Grpc.Core.ServiceBinderBase` implementation covering all four
  `AddMethod` families (unary, client streaming, server streaming, duplex streaming). The
  generated `BindService(ServiceBinderBase, impl)` is located through the codegen's
  `BindServiceMethod` attribute.
- **`ElsieServerCallContext`** — a `ServerCallContext` subclass wrapping the `ElsieContext`:
  request metadata maps from HTTP headers (binary `-bin` headers base64-decoded), response
  metadata and trailers flow to `ElsieResponse`, `Deadline` comes from the `grpc-timeout`
  header and drives cancellation through `RequestAborted` (deadline expiry surfaces as
  `DEADLINE_EXCEEDED`, not `CANCELLED`).
- **5-byte gRPC framing** (`GrpcFraming`): uncompressed frames; oversized messages are rejected
  with `RESOURCE_EXHAUSTED` (configurable `MaxReceiveMessageSize` / `MaxSendMessageSize`).
- **`grpc-status` / `grpc-message` trailers** via the HTTP/2 and HTTP/3 trailing-HEADERS channel
  (added during response streaming, so streaming status is accurate).
- **`application/grpc` content-type gate** — anything else gets a 415.
- **gRPC ↔ HTTP status mapping** — route misses (404) map to `UNIMPLEMENTED`; handler
  `RpcException`s carry their gRPC status; deadline expiries map to `DEADLINE_EXCEEDED`.
- **Cancellation** — `grpc-timeout` and client disconnects cancel the handler via
  `ServerCallContext.CancellationToken`.
- **Reflection-lite** — `grpc.reflection.v1alpha.ServerReflection` so `grpcurl` can
  `list`/`describe`. Pass the proto's generated `FileDescriptor` (e.g.
  `GreeterReflection.Descriptor`) to enable file-descriptor lookups.
- **Interceptors** — because gRPC runs inside the Elsie middleware pipeline, cross-cutting
  behavior (auth, logging, rate limiting, deadline pre-checks, metadata injection) is expressed
  as ordinary Elsie middleware with `app.Use(...)` / `Module.Use(...)`.

## Streaming

Unary and server/client/bidi streaming all work. Response bodies stream incrementally on both
transports (HTTP/2 DATA frames and HTTP/3 DATA frames); request bodies stream on HTTP/3 and are
fully buffered on HTTP/2 (the h2 layer waits for END_STREAM before dispatch).

## Example

```csharp
public sealed class GreeterServiceImpl : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context) =>
        Task.FromResult(new HelloReply { Message = "Hello " + request.Name });

    public override async Task SayHelloStream(
        HelloRequest request,
        IServerStreamWriter<HelloReply> responseStream,
        ServerCallContext context)
    {
        for (var i = 1; i <= 3; i++)
        {
            await responseStream.WriteAsync(new HelloReply { Message = $"Hello {request.Name} #{i}" });
        }
    }
}
```

Try it:

```bash
grpcurl -insecure -d '{"name":"Elsie"}' 127.0.0.1:8443 greet.Greeter/SayHello
grpcurl -insecure 127.0.0.1:8443 list
```

## See also

- [http3.md](http3.md) (HTTP/3 transport, trailers)
- [middleware.md](middleware.md) (interceptor story)
- [hosting-and-aot.md](hosting-and-aot.md)

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Elsie.Test;
using Elsie.Web;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit;

namespace Elsie.Grpc.Tests;

public sealed class EchoServiceImpl : Echo.EchoBase
{
    public override Task<EchoResponse> Unary(EchoRequest request, ServerCallContext context) =>
        Task.FromResult(new EchoResponse { Message = "echo:" + request.Message });

    public override async Task ServerStream(
        EchoRequest request,
        IServerStreamWriter<EchoResponse> responseStream,
        ServerCallContext context)
    {
        for (var i = 0; i < request.Count; i++)
        {
            await responseStream.WriteAsync(new EchoResponse { Message = $"echo:{request.Message}:{i}" })
                .ConfigureAwait(false);
        }
    }

    public override async Task<EchoResponse> ClientStream(
        IAsyncStreamReader<EchoRequest> requestStream,
        ServerCallContext context)
    {
        var count = 0;
        var last = string.Empty;
        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            count++;
            last = requestStream.Current.Message;
        }

        return new EchoResponse { Message = $"count:{count}:last:{last}" };
    }

    public override async Task Bidi(
        IAsyncStreamReader<EchoRequest> requestStream,
        IServerStreamWriter<EchoResponse> responseStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(new EchoResponse { Message = "echo:" + requestStream.Current.Message })
                .ConfigureAwait(false);
        }
    }

    public override Task<EchoResponse> Fail(EchoRequest request, ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.PermissionDenied, "not allowed"));

    public override async Task<EchoResponse> Slow(EchoRequest request, ServerCallContext context)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), context.CancellationToken).ConfigureAwait(false);
        return new EchoResponse { Message = "slow-done" };
    }
}

public sealed class GreeterServiceImpl : Greeter.GreeterBase
{
    public override Task<HelloReply> Hello(HelloRequest request, ServerCallContext context) =>
        Task.FromResult(new HelloReply { Message = "hello:" + request.Name });
}

public class ElsieGrpcTests
{
    private static int FindFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null);
    }

    private static async Task<(Echo.EchoClient Client, Elsie.Web.ElsieTestServer Server, int Port, Func<Task> Dispose)> StartServerAsync()
    {
        var cert = CreateSelfSigned();
        var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .MapGrpcService<EchoServiceImpl>(fileDescriptor: EchoReflection.Descriptor)
            .StartAsync();

        var port = server.Endpoints[0].Port;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        var channel = GrpcChannel.ForAddress(
            $"https://127.0.0.1:{port}",
            new GrpcChannelOptions { HttpHandler = handler });
        return (new Echo.EchoClient(channel), server, port, () =>
        {
            channel.Dispose();
            cert.Dispose();
            return server.DisposeAsync().AsTask();
        }
        );
    }

    [Fact]
    public async Task Unary_echo_roundtrip()
    {
        var (client, _, _, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        var reply = await client.UnaryAsync(new EchoRequest { Message = "hello" });
        Assert.Equal("echo:hello", reply.Message);
    }

    [Fact]
    public async Task Two_grpc_services_both_register_routes()
    {
        // Regression: each MapGrpcService call must carry its own binder/options in its own
        // module. Shared AddSingleton binder/options resolved last-wins, so the first service's
        // routes silently disappeared (404) while the last one's routes were duplicated.
        var cert = CreateSelfSigned();
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .MapGrpcService<EchoServiceImpl>(fileDescriptor: EchoReflection.Descriptor)
            .MapGrpcService<GreeterServiceImpl>(fileDescriptor: GreetReflection.Descriptor)
            .StartAsync();

        var port = server.Endpoints[0].Port;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        using var channel = GrpcChannel.ForAddress(
            $"https://127.0.0.1:{port}",
            new GrpcChannelOptions { HttpHandler = handler });

        // The FIRST mapped service's route must resolve (it used to 404).
        var echo = new Echo.EchoClient(channel);
        var echoReply = await echo.UnaryAsync(new EchoRequest { Message = "multi" });
        Assert.Equal("echo:multi", echoReply.Message);

        // The SECOND mapped service's route must resolve too.
        var greet = new Greeter.GreeterClient(channel);
        var greetReply = await greet.HelloAsync(new HelloRequest { Name = "world" });
        Assert.Equal("hello:world", greetReply.Message);

        cert.Dispose();
    }

    [Fact]
    public async Task Unary_trailers_and_grpc_status()
    {
        var (client, _, _, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        var call = client.UnaryAsync(new EchoRequest { Message = "x" });
        var response = await call.ResponseAsync;
        Assert.Equal("echo:x", response.Message);
        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
    }

    [Fact]
    public async Task Server_streaming()
    {
        var (client, _, _, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        var call = client.ServerStream(new EchoRequest { Message = "s", Count = 3 });
        var messages = new List<string>();
        await foreach (var m in call.ResponseStream.ReadAllAsync())
        {
            messages.Add(m.Message);
        }

        Assert.Equal(["echo:s:0", "echo:s:1", "echo:s:2"], messages);
        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
    }

    [Fact]
    public async Task Client_streaming()
    {
        var (client, _, _, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        using var call = client.ClientStream();
        for (var i = 0; i < 4; i++)
        {
            await call.RequestStream.WriteAsync(new EchoRequest { Message = $"m{i}" });
        }

        await call.RequestStream.CompleteAsync();
        var response = await call.ResponseAsync;
        Assert.Equal("count:4:last:m3", response.Message);
    }

    [Fact]
    public async Task Bidi_streaming()
    {
        var (client, _, _, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        using var call = client.Bidi();
        var received = new List<string>();
        var sending = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                await call.RequestStream.WriteAsync(new EchoRequest { Message = $"b{i}" });
            }

            await call.RequestStream.CompleteAsync();
        });

        await foreach (var m in call.ResponseStream.ReadAllAsync())
        {
            received.Add(m.Message);
        }

        await sending;
        Assert.Equal(["echo:b0", "echo:b1", "echo:b2"], received);
    }

    [Fact]
    public async Task Error_status_maps_to_rpc_exception()
    {
        var (client, _, _, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.FailAsync(new EchoRequest { Message = "boom" }));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Equal("not allowed", ex.Status.Detail);
    }

    [Fact]
    public async Task Deadline_cancels_the_call()
    {
        var (client, _, _, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SlowAsync(
                new EchoRequest { Message = "slow" },
                deadline: DateTime.UtcNow.AddMilliseconds(300)));
        Assert.Equal(StatusCode.DeadlineExceeded, ex.StatusCode);
    }

    /// <summary>
    /// Starts an h3-enabled server (same port serves h1/h2 over TLS and QUIC/h3 over UDP) and
    /// returns a GrpcChannel pinned to HTTP/3 (RequestVersionExact — no fallback to h2). Only
    /// call when <see cref="QuicListener.IsSupported"/> (libmsquic present — CI installs it).
    /// </summary>
    private static async Task<(Echo.EchoClient Client, int Port, Func<Task> Dispose)> StartHttp3ServerAsync()
    {
        var cert = CreateSelfSigned();
        var port = FindFreePort();
        var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, port, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
                o.EnableHttp3 = true;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .MapGrpcService<EchoServiceImpl>(fileDescriptor: EchoReflection.Descriptor)
            .StartAsync();

        var handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            },
            EnableMultipleHttp3Connections = true
        };
        var channel = GrpcChannel.ForAddress(
            $"https://127.0.0.1:{port}",
            new GrpcChannelOptions
            {
                HttpHandler = handler,
                HttpVersion = HttpVersion.Version30,
                HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact
            });
        return (new Echo.EchoClient(channel), port, () =>
        {
            channel.Dispose();
            handler.Dispose();
            cert.Dispose();
            return server.DisposeAsync().AsTask();
        }
        );
    }

    [Fact]
    public async Task Unary_over_http3()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        var (client, _, dispose) = await StartHttp3ServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        var reply = await client.UnaryAsync(new EchoRequest { Message = "h3" });
        Assert.Equal("echo:h3", reply.Message);
    }

    [Fact]
    public async Task Client_streaming_over_http3_preserves_message_order()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        // Every client-streaming message over h3 splits its DATA frame (GrpcFraming reads the
        // 5-byte header first); the server must still deliver the messages in exact order.
        var (client, _, dispose) = await StartHttp3ServerAsync();
        await using var d = AsyncDisposable.Create(dispose);
        using var call = client.ClientStream();
        for (var i = 0; i < 4; i++)
        {
            await call.RequestStream.WriteAsync(new EchoRequest { Message = $"m{i}" });
        }

        await call.RequestStream.CompleteAsync();
        var response = await call.ResponseAsync;
        Assert.Equal("count:4:last:m3", response.Message);
    }

    [Fact]
    public async Task Huge_grpc_timeout_value_is_ignored_not_500()
    {
        // Regression: a 17-digit grpc-timeout used to overflow TimeSpan inside the call-context
        // ctor (client-sendable 500). Per the gRPC spec the value is 1–8 digits, so it must be
        // rejected and the deadline ignored.
        var (_, server, port, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        using var http = new HttpClient(handler);
        var message = new EchoRequest { Message = "t" }.ToByteArray();
        var payload = new byte[5 + message.Length];
        payload[1] = (byte)(message.Length >> 24);
        payload[2] = (byte)(message.Length >> 16);
        payload[3] = (byte)(message.Length >> 8);
        payload[4] = (byte)message.Length;
        message.CopyTo(payload, 5);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}/elsie.test.Echo/Unary")
        {
            Content = new ByteArrayContent(payload),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        req.Headers.TryAddWithoutValidation("te", "trailers");
        req.Headers.TryAddWithoutValidation("grpc-timeout", "99999999999999999S");

        using var response = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("99999999999999999S", false)] // 17 digits — invalid per gRPC spec, must not throw
    [InlineData("100000000S", false)]         // 9 digits — outside the 1–8 digit span
    [InlineData("12345678S", true)]           // 8 digits — valid maximum
    [InlineData("1S", true)]
    [InlineData("500m", true)]
    [InlineData("2H", true)]
    [InlineData("0S", false)]                 // zero amount — invalid
    [InlineData("S", false)]                  // no digits — invalid
    [InlineData("", false)]
    public void TryParseTimeout_enforces_grpc_digit_range(string value, bool expected)
    {
        var ok = ElsieServerCallContext.TryParseTimeout(value, out var timeout);
        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.True(timeout > TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task Missing_grpc_content_type_returns_415()
    {
        var (_, server, port, dispose) = await StartServerAsync();
        await using var d = AsyncDisposable.Create(dispose);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        using var http = new HttpClient(handler);
        // A plain POST to the gRPC route without application/grpc must be rejected with 415.
        using var response = await http.PostAsync(
            $"https://127.0.0.1:{port}/elsie.test.Echo/Unary",
            new StringContent("not-a-grpc-body"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}

internal sealed class AsyncDisposable : IAsyncDisposable
{
    private readonly Func<Task> _dispose;
    private AsyncDisposable(Func<Task> dispose) => _dispose = dispose;
    public static AsyncDisposable Create(Func<Task> dispose) => new(dispose);
    public ValueTask DisposeAsync() => new(_dispose());
}

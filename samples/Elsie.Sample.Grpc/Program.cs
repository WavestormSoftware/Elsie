using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Elsie.Grpc;
using Elsie.Web;
using Greet;
using Grpc.Core;

// gRPC sample: serves a Greeter service (unary + server streaming) over Elsie HTTP/2 and
// HTTP/3. The proto file is compiled at build time by Grpc.Tools.
//
//   dotnet run -- --urls https://127.0.0.1:8443
//   grpcurl -insecure -d '{"name":"Elsie"}' 127.0.0.1:8443 greet.Greeter/SayHello
//   grpcurl -insecure 127.0.0.1:8443 list
//   grpcurl -insecure 127.0.0.1:8443 describe greet.Greeter

var port = 8443;
for (var i = 0; i < args.Length; i++)
{
    if ((args[i] == "--urls" || args[i] == "--url") &&
        i + 1 < args.Length &&
        Uri.TryCreate(args[i + 1], UriKind.Absolute, out var url) &&
        url.Port > 0)
    {
        port = url.Port;
    }
}

using var cert = CreateSelfSigned();
ElsieApp.Create(args)
    .QuietConsole(false)
    .Listen(IPAddress.Any, port, o =>
    {
        o.UseHttps = true;
        o.Certificate = cert;
        o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
        o.EnableHttp3 = true; // gRPC over HTTP/3 too (when libmsquic is present)
    })
    .Configure(o => o.ScanEntryAssembly = false)
    .MapGrpcService<GreeterServiceImpl>(fileDescriptor: GreetReflection.Descriptor)
    .Run();

static X509Certificate2 CreateSelfSigned()
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
            await responseStream.WriteAsync(new HelloReply { Message = $"Hello {request.Name} #{i}" })
                .ConfigureAwait(false);
        }
    }
}

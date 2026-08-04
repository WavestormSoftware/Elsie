using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elsie.Web.Http3;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// W1 transport-audit regression tests: RFC 7230 §5.4 Host validation (missing/duplicate → 400,
/// absolute-form waiver), response-header CRLF injection mapped to 400 (not 500), graceful
/// half-close (FIN) no longer aborting a request, and HTTP/3 second-client-control-stream
/// rejection with H3_STREAM_CREATION_ERROR (0x103). QUIC tests follow the project's platform/deadline rules.
/// </summary>
public class AuditFixDeepTests
{
    private sealed class AuditModule : ElsieModule
    {
        public AuditModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
            // Delays, then returns. Used to prove a graceful FIN mid-request does NOT abort.
            Get("/slow", async (ctx, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200), ct).ConfigureAwait(false);
                return ElsieResult.Text("slow-ok");
            });
            Get("/inject", ctx =>
            {
                var v = ctx.Request.GetQuery("v") ?? string.Empty;
                return ElsieResult.Text("ok").WithHeader("X-Echo", v);
            });
        }
    }

    // ------------------------------------------------------------------ 1. RFC 7230 §5.4 Host

    [Fact]
    public async Task Missing_host_on_http11_returns_400()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "GET /ping HTTP/1.1\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        Assert.Equal(400, FirstStatus(raw));
        Assert.DoesNotContain("pong", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_host_headers_return_400()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "GET /ping HTTP/1.1\r\nHost: a.example\r\nHost: b.example\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        Assert.Equal(400, FirstStatus(raw));
        Assert.DoesNotContain("pong", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_host_header_returns_400()
    {
        // RFC 7230 §5.4: a Host header present but empty ("Host:\r\n") is a 400 — the
        // authority is not recoverable and MUST NOT be guessed.
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "GET /ping HTTP/1.1\r\nHost:\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        Assert.Equal(400, FirstStatus(raw));
        Assert.DoesNotContain("pong", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Whitespace_only_host_header_returns_400()
    {
        // A Host header whose value is only whitespace is indistinguishable from empty and
        // must be rejected the same way.
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "GET /ping HTTP/1.1\r\nHost:   \r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        Assert.Equal(400, FirstStatus(raw));
        Assert.DoesNotContain("pong", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Absolute_form_request_target_without_host_is_accepted()
    {
        // RFC 7230 §5.4: the absolute-form request-target carries the host, so the Host header
        // is not required. The request must still be routed to the origin form.
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "GET http://localhost/ping HTTP/1.1\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        Assert.Equal(200, FirstStatus(raw));
        Assert.Contains("pong", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http10_request_without_host_is_accepted()
    {
        // RFC 7230 §5.4 requires Host only for HTTP/1.1; HTTP/1.0 is unaffected.
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "GET /ping HTTP/1.0\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        Assert.Equal(200, FirstStatus(raw));
        Assert.Contains("pong", raw, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 2. CRLF injection → 400

    [Fact]
    public async Task Response_header_crlf_returns_400_not_500()
    {
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        // Raw CRLF percent-encoded in the query value; the handler echoes it into a header.
        var raw = await SendRawAsync(
            ep,
            "GET /inject?v=a%0d%0aInjected%3A%20yes HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));

        // The injection must stay blocked AND surface as a 400, not a 500.
        Assert.Equal(400, FirstStatus(raw));
        Assert.DoesNotContain("Injected: yes", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\nInjected: yes\r\n", raw, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 3. graceful half-close (FIN) does not abort

    [Fact]
    public async Task Graceful_halfclose_after_full_request_still_receives_response()
    {
        // Regression for the DisconnectWatcher false positive: a client that sends a complete
        // request and then half-closes its write side (shutdown(SHUT_WR), a TCP FIN) is still
        // reading and must receive the response. The server must not abort the request and
        // close without a response.
        await using var server = await StartServerAsync();
        var ep = server.Endpoints[0];
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();

        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /slow HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));
        // Half-close the write side (FIN) — the client still expects to read the response.
        tcp.Client.Shutdown(SocketShutdown.Send);

        var raw = await ReadAllAsync(ns, TimeSpan.FromSeconds(10));
        Assert.Equal(200, FirstStatus(raw));
        Assert.Contains("slow-ok", raw, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 4. HTTP/3 second control stream → 0x103

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    public async Task Second_client_control_stream_closes_connection_with_0x103()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSignedCert();
            var port = FindFreePort();
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, port, o =>
                {
                    o.UseHttps = true;
                    o.Certificate = cert;
                    o.EnableHttp3 = true;
                })
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<AuditModule>()
                .StartAsync();

            await using var connection = await ConnectAsync(port, ct);

            // First client control stream (type 0x00).
            await using var firstControl = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await firstControl.WriteAsync(new byte[] { 0x00 }, ct);
            await firstControl.FlushAsync(ct);

            // Second client control stream (type 0x00) — must close the connection with 0x103.
            await using var secondControl = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
            await secondControl.WriteAsync(new byte[] { 0x00 }, ct);
            await secondControl.FlushAsync(ct);

            await AssertConnectionClosedWithErrorAsync(connection, 0x103, port, ct);
        });
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<ElsieTestServer> StartServerAsync()
    {
        return await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<AuditModule>()
            .StartAsync();
    }

    private static async Task<string> SendRawAsync(IPEndPoint ep, string request, TimeSpan timeout)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(request));
        return await ReadAllAsync(ns, timeout);
    }

    private static async Task<string> ReadAllAsync(NetworkStream ns, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var n = await ns.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
                if (n == 0)
                {
                    break;
                }

                ms.Write(buffer, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            // read deadline — return what we have
        }
        catch (IOException)
        {
            // peer closed / reset — return what we have
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static int FirstStatus(string raw)
    {
        var idx = raw.IndexOf("HTTP/1.", StringComparison.Ordinal);
        if (idx < 0)
        {
            return -1;
        }

        var start = idx + "HTTP/1.".Length;
        // Skip the minor version digit.
        if (start < raw.Length && char.IsAsciiDigit(raw[start]))
        {
            start++;
        }

        if (start < raw.Length && raw[start] == ' ')
        {
            start++;
        }

        var end = raw.IndexOf(' ', start);
        if (end > start && int.TryParse(raw.AsSpan(start, end - start), out var code))
        {
            return code;
        }

        return -1;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task<QuicConnection> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        return await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            DefaultStreamErrorCode = 0x0100,
            DefaultCloseErrorCode = 0x0100,
            // Zero inbound credit (the default) would starve the server's control/QPACK streams.
            MaxInboundBidirectionalStreams = 100,
            MaxInboundUnidirectionalStreams = 100,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [SslApplicationProtocol.Http3],
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }
        }, cancellationToken);
    }

    /// <summary>Probes with valid requests until the peer's connection close surfaces as a
    /// <see cref="QuicException"/> carrying <paramref name="expectedErrorCode"/> (the BCL exposes
    /// no connection-closed event). Transient pre-close errors (e.g. stream limits) are retried.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task AssertConnectionClosedWithErrorAsync(
        QuicConnection connection,
        long expectedErrorCode,
        int port,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var probe = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
                var encoder = new QpackEncoder(encoderStream: null);
                var block = encoder.EncodeFieldSection(
                    [
                        (":method", "GET"),
                        (":scheme", "https"),
                        (":path", "/"),
                        (":authority", $"127.0.0.1:{port}")
                    ],
                    streamId: 0);
                await Http3FrameWriter.WriteAsync(probe, new Http3Frame(Http3FrameType.Headers, block), cancellationToken);
                await probe.FlushAsync(cancellationToken);
                probe.CompleteWrites();

                await ReadUntilConnectionClosedAsync(probe, new byte[4096], cancellationToken);
            }
            catch (QuicException ex) when (ex.ApplicationErrorCode == expectedErrorCode)
            {
                Assert.Equal(QuicError.ConnectionAborted, ex.QuicError);
                return;
            }
            catch (QuicException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"The HTTP/3 connection did not close with error code 0x{expectedErrorCode:X}.");
    }

    /// <summary>Loops on reads until the peer's connection close surfaces as a
    /// <see cref="QuicException"/>; partial reads are irrelevant to this loop.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macOS")]
    [SupportedOSPlatform("windows")]
    private static async Task ReadUntilConnectionClosedAsync(QuicStream stream, byte[] buffer, CancellationToken ct)
    {
#pragma warning disable CA2022 // partial reads are fine — the loop exists only to observe the close
        while (true)
        {
            await stream.ReadAsync(buffer, ct);
        }
#pragma warning restore CA2022
    }

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static X509Certificate2 CreateSelfSignedCert()
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
}

using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elsie.Web.Http3;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// HTTP/3 request-body streaming regression tests. The lazy DATA-frame body stream must serve
/// body bytes in exact wire order even when a frame is larger than the consumer's read buffer
/// (the framework CopyToAsync path reads 81920 bytes and gRPC framing reads the 5-byte header
/// first, so every large DATA frame is split). Skipped when <c>QuicListener.IsSupported</c> is
/// false (no libmsquic); CI installs libmsquic so these run in http3.yml.
/// </summary>
public class Http3RequestBodyTests
{
    private sealed class EchoModule : ElsieModule
    {
        public EchoModule()
        {
            // Reads the body in small chunks (deliberately smaller than the DATA frames the
            // client sends) so every frame is split across reads.
            Post("/echo", async (ctx, ct) =>
            {
                // Give the background DATA-frame pump time to enqueue all frames before the
                // handler starts reading, so a reordering bug manifests deterministically.
                await Task.Delay(500, ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buf = new byte[8192];
                while (true)
                {
                    var n = await ctx.Request.Body.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    ms.Write(buf, 0, n);
                }

                return ElsieResult.Bytes(ms.ToArray(), "application/octet-stream");
            });
        }
    }

    [Fact]
    public async Task Split_frame_remainder_is_served_before_later_frames()
    {
        // Deterministic unit regression (no QUIC needed): a 200 KB DATA frame followed by a
        // 100 KB frame. The pump must have BOTH frames queued before the consumer splits the
        // first frame, otherwise the old push-back path could not be exercised.
        var source = new BlockingStream();
        var partA = new byte[200_000];
        var partB = new byte[100_000];
        for (var i = 0; i < partA.Length; i++)
        {
            partA[i] = (byte)(i % 251);
        }

        for (var i = 0; i < partB.Length; i++)
        {
            partB[i] = (byte)(200 + (i % 50));
        }

        await Http3FrameWriter.WriteAsync(source, new Http3Frame(Http3FrameType.Data, partA), CancellationToken.None);
        await Http3FrameWriter.WriteAsync(source, new Http3Frame(Http3FrameType.Data, partB), CancellationToken.None);

        var body = new QuicRequestBodyStream(source, maxBody: long.MaxValue);
        body.StartReadingAsync(CancellationToken.None);

        // Wait until the pump has consumed both frames (so frame B is queued behind frame A).
        var encodedTotal = partA.Length + partB.Length + 2 * QuicVarInt.EncodedLength(0) + 2 * QuicVarInt.EncodedLength(300_000);
        await source.WaitForBytesConsumedAsync(encodedTotal, TimeSpan.FromSeconds(5));
        source.CloseForWriting();

        // Drain with 8 KB reads (smaller than frame A): the remainder of frame A must be
        // served before frame B's bytes, in exact wire order.
        using var all = new MemoryStream();
        var buf = new byte[8192];
        while (true)
        {
            var n = await body.ReadAsync(buf, CancellationToken.None);
            if (n == 0)
            {
                break;
            }

            all.Write(buf, 0, n);
        }

        Assert.Equal(partA.Concat(partB).ToArray(), all.ToArray());
    }

    /// <summary>QUIC is platform-guarded; the test gates on <see cref="QuicListener.IsSupported"/>.</summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macOS")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Large_request_body_preserves_byte_order_across_split_data_frames()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSigned();
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
                .Module<EchoModule>()
                .StartAsync();

            await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
                DefaultStreamErrorCode = 0x0100,
                DefaultCloseErrorCode = 0x0100,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [SslApplicationProtocol.Http3],
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true
                }
            }, ct);

            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            var encoder = new QpackEncoder(encoderStream: null);
            var block = encoder.EncodeFieldSection(
                [
                    (":method", "POST"),
                    (":scheme", "https"),
                    (":path", "/echo"),
                    (":authority", $"127.0.0.1:{port}"),
                    ("content-type", "application/octet-stream")
                ],
                streamId: 0);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), ct);

            // Two DATA frames whose combined payload (300 KB) is far larger than the handler's
            // 8 KB read buffer. The first frame alone (200 KB) exceeds the framework CopyToAsync
            // read size (81920 bytes), so every consumer read splits it.
            var partA = new byte[200_000];
            var partB = new byte[100_000];
            for (var i = 0; i < partA.Length; i++)
            {
                partA[i] = (byte)(i % 251);
            }

            for (var i = 0; i < partB.Length; i++)
            {
                partB[i] = (byte)(200 + (i % 50));
            }

            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, partA), ct);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, partB), ct);
            await stream.FlushAsync(ct);
            stream.CompleteWrites();

            using var response = new MemoryStream();
            string? status = null;
            while (true)
            {
                var frame = await Http3FrameReader.ReadAsync(stream, ct);
                if (frame is null)
                {
                    break;
                }

                if (frame.Value.Type == Http3FrameType.Headers && status is null)
                {
                    var decoder = new QpackDecoder(maxCapacity: 0, decoderStream: null);
                    var fields = decoder.DecodeHeaderBlock(frame.Value.Payload.Span).Fields!;
                    status = fields.FirstOrDefault(f => f.Item1 == ":status").Item2;
                }
                else if (frame.Value.Type == Http3FrameType.Data)
                {
                    response.Write(frame.Value.Payload.Span);
                }
            }

            Assert.Equal("200", status);
            var expected = partA.Concat(partB).ToArray();
            Assert.Equal(expected, response.ToArray());
        });
    }

    /// <summary>Test stream that blocks on empty reads (unlike MemoryStream, which returns EOF).</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly SemaphoreSlim _dataAvailable = new(0);
        private long _readPosition;
        private long _bytesRead;
        private bool _closed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void CloseForWriting()
        {
            _closed = true;
            _dataAvailable.Release();
        }

        public async Task WaitForBytesConsumedAsync(long target, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Interlocked.Read(ref _bytesRead) < target)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("BlockingStream never consumed the expected bytes.");
                }

                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                // MemoryStream reads from its internal position (left at the end by writes);
                // rewind to the logical read position before each read.
                _inner.Position = _readPosition;
                var n = _inner.Read(buffer.Span);
                if (n > 0)
                {
                    _readPosition += n;
                    Interlocked.Add(ref _bytesRead, n);
                    return n;
                }

                if (_closed)
                {
                    return 0;
                }

                await _dataAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _dataAvailable.Release();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

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
}

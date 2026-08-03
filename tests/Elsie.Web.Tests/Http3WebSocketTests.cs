using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elsie.Web.Http3;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// WebSocket over HTTP/3 (RFC 9220 extended CONNECT) integration tests using an in-process
/// QUIC client. Skipped when <c>QuicListener.IsSupported</c> is false (no libmsquic).
/// </summary>
public class Http3WebSocketTests
{
    private sealed class WsModule : ElsieModule
    {
        public WsModule()
        {
            Map("CONNECT", "/ws", () => ElsieResult.WebSocket(async (ws, ct) =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await ws.ReceiveAsync(ct).ConfigureAwait(false);
                    if (msg is null)
                    {
                        break;
                    }

                    if (msg.MessageType == WebSocketMessageType.Text)
                    {
                        await ws.SendTextAsync("echo:" + msg.GetText(), ct).ConfigureAwait(false);
                    }
                }
            }));
        }
    }

    /// <summary>QUIC is platform-guarded; the test gates on <see cref="QuicListener.IsSupported"/>.</summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macOS")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Echo_websocket_over_http3()
    {
        if (!QuicListener.IsSupported)
        {
            return; // libmsquic absent locally — CI installs it (http3.yml)
        }

        await H3TestDeadline.RunAsync(async ct =>
        {
            using var cert = CreateSelfSigned();
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0, o =>
                {
                    o.UseHttps = true;
                    o.Certificate = cert;
                    o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
                    o.EnableHttp3 = true;
                })
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<WsModule>()
                .StartAsync();

            var port = server.Endpoints[0].Port;
            await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
                DefaultStreamErrorCode = 0x0100,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true
                }
            }, ct);

            await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);
            var encoder = new QpackEncoder(encoderStream: null);
            var block = encoder.EncodeFieldSection(
                [
                    (":method", "CONNECT"),
                    (":protocol", "websocket"),
                    (":scheme", "https"),
                    (":path", "/ws"),
                    (":authority", $"127.0.0.1:{port}")
                ],
                streamId: 0);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Headers, block), ct);

            // Read the 2xx handshake response.
            var decoder = new QpackDecoder(maxCapacity: 0, decoderStream: null);
            var responseFrame = await Http3FrameReader.ReadAsync(stream, ct);
            Assert.NotNull(responseFrame);
            Assert.Equal(Http3FrameType.Headers, responseFrame!.Value.Type);
            var fields = decoder.DecodeHeaderBlock(responseFrame.Value.Payload.Span).Fields!;
            Assert.Contains((":status", "200"), fields);

            // Send a masked client text frame (RFC 6455).
            var clientFrame = BuildMaskedTextFrame("hello");
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, clientFrame), ct);

            // Read the echoed text frame (server frames are unmasked).
            var echo = await ReadWebSocketTextAsync(stream, ct);
            Assert.Equal("echo:hello", echo);

            // Close handshake: client sends close frame, server echoes it.
            var closePayload = new byte[] { 0x03, 0xE8 }; // 1000
            var closeFrame = BuildMaskedFrame(0x8, closePayload);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, closeFrame), ct);
            await Task.Delay(200, ct);
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macOS")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<string> ReadWebSocketTextAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        var payload = new List<byte>();
        while (true)
        {
            var frame = await Http3FrameReader.ReadAsync(stream, cancellationToken);
            Assert.NotNull(frame);
            if (frame!.Value.Type != Http3FrameType.Data)
            {
                continue;
            }

            payload.AddRange(frame.Value.Payload.ToArray());
            if (TryParseTextFrame(payload, out var text))
            {
                return text;
            }
        }
    }

    /// <summary>Parses the first RFC 6455 frame from the accumulated bytes (server frames are unmasked).</summary>
    private static bool TryParseTextFrame(List<byte> bytes, out string text)
    {
        text = string.Empty;
        if (bytes.Count < 2)
        {
            return false;
        }

        var opcode = (byte)(bytes[0] & 0x0F);
        var masked = (bytes[1] & 0x80) != 0;
        var len = (long)(bytes[1] & 0x7F);
        var offset = 2;
        if (len == 126)
        {
            if (bytes.Count < offset + 2)
            {
                return false;
            }

            len = (bytes[offset] << 8) | bytes[offset + 1];
            offset += 2;
        }
        else if (len == 127)
        {
            if (bytes.Count < offset + 8)
            {
                return false;
            }

            len = 0;
            for (var i = 0; i < 8; i++)
            {
                len = (len << 8) | bytes[offset + i];
            }

            offset += 8;
        }

        if (masked)
        {
            offset += 4;
        }

        if (bytes.Count < offset + len)
        {
            return false;
        }

        if (opcode != 0x1)
        {
            bytes.RemoveRange(0, offset + (int)len);
            return TryParseTextFrame(bytes, out text);
        }

        var payload = bytes.Skip(offset).Take((int)len).ToArray();
        if (masked && bytes.Count >= offset + 4 + len)
        {
            var mask = bytes.Skip(offset - 4).Take(4).ToArray();
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i % 4];
            }
        }

        text = Encoding.UTF8.GetString(payload);
        return true;
    }

    private static byte[] BuildMaskedTextFrame(string text) => BuildMaskedFrame(0x1, Encoding.UTF8.GetBytes(text));

    private static byte[] BuildMaskedFrame(byte opcode, byte[] payload)
    {
        var header = new List<byte> { (byte)(0x80 | opcode) };
        if (payload.Length < 126)
        {
            header.Add((byte)(0x80 | payload.Length));
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header.Add((byte)(0x80 | 126));
            header.Add((byte)(payload.Length >> 8));
            header.Add((byte)(payload.Length & 0xFF));
        }
        else
        {
            header.Add((byte)(0x80 | 127));
            var len = (long)payload.Length;
            for (var i = 7; i >= 0; i--)
            {
                header.Add((byte)((len >> (8 * i)) & 0xFF));
            }
        }

        var mask = RandomNumberGenerator.GetBytes(4);
        var result = new byte[header.Count + 4 + payload.Length];
        header.CopyTo(result, 0);
        mask.CopyTo(result, header.Count);
        for (var i = 0; i < payload.Length; i++)
        {
            result[header.Count + 4 + i] = (byte)(payload[i] ^ mask[i % 4]);
        }

        return result;
    }

    internal static X509Certificate2 CreateSelfSigned()
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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elsie;
using Elsie.Web;

namespace Elsie.Fuzz;

/// <summary>
/// Black-box random-input fuzzer + soak for the Elsie HTTP server.
/// Not part of the normal test suite — run nightly (see .github/workflows/nightly.yml).
/// </summary>
public static class Program
{
    private static readonly Random _rng = new();
    private static readonly byte[] _h2Preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();
    private static readonly string[] _methods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"];
    private static readonly string[] _paths =
    [
        "/", "/json", "/echo", "/files/big.bin", "/" + new string('a', 200),
        "/../etc/passwd", "/%2e%2e/", "/./foo/./bar", "//foo", "/foo%00bar",
        "/" + new string('x', 1024), "/path;param=value", "/path?query=1&b=2",
        "/a/b/c/d/e/f/g/h/i/j/k/l", "/你好"
    ];
    private static readonly string[] _headerNames = [
        "X-Foo", "X-Bar", "Content-Type", "Accept", "Authorization",
        "X-Custom-" + new string('x', 100), "", "Host", "User-Agent", "Referer",
        "Cookie"
    ];
    private static readonly string[] _headerValues = [
        "value", "", new string('x', 500), "application/json",
        "text/html; charset=utf-8", "Bearer " + new string('x', 200),
        "\0\0\0", "line1\nline2", "leading space", "trailing space "
    ];

    private static int _totalFuzzIterations;
    private static int _fuzzErrors;
    private static int _fuzzHangs;
    private static readonly ConcurrentBag<string> _errorDetails = new();
    private static readonly object _consoleLock = new();
    private static long _peakWorkingSet;
    private static long _baselineWorkingSet;

    public static async Task<int> Main(string[] args)
    {
        var seed = 42;
        var totalSeconds = 0;
        var iterations = 5000;
        var protocols = "all"; // h1, h2, all
        var soakOnly = false;
        var fuzzOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed" when i + 1 < args.Length:
                    seed = int.Parse(args[++i]);
                    break;
                case "--seconds" when i + 1 < args.Length:
                    totalSeconds = int.Parse(args[++i]);
                    break;
                case "--iterations" when i + 1 < args.Length:
                    iterations = int.Parse(args[++i]);
                    break;
                case "--protocols" when i + 1 < args.Length:
                    protocols = args[++i].ToLowerInvariant();
                    break;
                case "--soak-only":
                    soakOnly = true;
                    break;
                case "--fuzz-only":
                    fuzzOnly = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    Console.Error.WriteLine("Usage: Elsie.Fuzz [--seed N] [--seconds N] [--iterations N] [--protocols h1|h2|all] [--soak-only] [--fuzz-only]");
                    return 1;
            }
        }

        var h1 = protocols is "all" or "h1";
        var h2 = protocols is "all" or "h2";

        // With --seconds N the budget is split 50/50 between fuzz and soak; with
        // --fuzz-only / --soak-only the full budget goes to the selected phase.
        var start = DateTime.UtcNow;
        var split = totalSeconds > 0 && !fuzzOnly && !soakOnly;
        var fuzzSeconds = totalSeconds > 0 ? (split ? totalSeconds / 2 : totalSeconds) : 0;
        var fuzzDeadline = soakOnly ? start : start.AddSeconds(fuzzSeconds);
        var soakDeadline = totalSeconds > 0
            ? start.AddSeconds(totalSeconds)
            : start.AddHours(1);

        Console.WriteLine($"Elsie.Fuzz seed={seed} seconds={totalSeconds} iterations={iterations} protocols={protocols}");

        // --- Build server ---
        using var cert = CreateSelfSignedCert();
        var staticDir = PrepareStaticDir();
        var app = ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0) // h1 cleartext
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<FuzzModule>()
            .StaticFiles(s =>
            {
                s.Root = staticDir;
                s.RequestPath = "/files";
            });

        await using var server = await app.StartAsync();
        var h1Ep = server.Endpoints[0];
        var h2Ep = server.Endpoints[1];
        // CreateClient() caches a single HttpClient on the test server; own it here so
        // warmup/canary/soak all share one live instance and it is disposed exactly once.
        using var client = server.CreateClient();

        // --- Warmup & baseline ---
        for (var i = 0; i < 5; i++)
        {
            try
            {
                using var r = await client.GetAsync("/");
                r.EnsureSuccessStatusCode();
            }
            catch
            {
                // ignore warmup glitches
            }
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
        _baselineWorkingSet = Environment.WorkingSet;
        _peakWorkingSet = _baselineWorkingSet;
        Console.WriteLine($"Baseline working set: {_baselineWorkingSet >> 20} MB");

        // --- Fuzz phase ---
        if (!soakOnly)
        {
            // Connection setup dominates per-iteration cost, so run several connection
            // loops concurrently (the server handles 10k concurrent connections).
            const int fuzzWorkers = 8;
            var fuzzCount = 0;
            var workers = Enumerable.Range(0, fuzzWorkers).Select(_ => Task.Run(async () =>
            {
                while (DateTime.UtcNow < fuzzDeadline && Volatile.Read(ref fuzzCount) < iterations)
                {
                    if (h1) await FuzzHttp1Async(h1Ep.Port, fuzzDeadline);
                    if (h2) await FuzzHttp2Async(h2Ep.Port, cert, fuzzDeadline);

                    var n = Interlocked.Increment(ref fuzzCount);
                    // Canary every 50 fuzz connections across all workers
                    if (n % 50 == 0)
                    {
                        await CanaryAsync(client);
                        ReportMemory();
                    }
                }
            }, CancellationToken.None)).ToArray();

            await Task.WhenAll(workers);
            await CanaryAsync(client);
            ReportMemory();
            Console.WriteLine($"Fuzz complete: {_totalFuzzIterations} iterations, {_fuzzErrors} errors, {_fuzzHangs} hangs");
        }

        // --- Soak phase ---
        if (!fuzzOnly && totalSeconds > 0)
        {
            Console.WriteLine("Soak phase starting...");
            await SoakAsync(server, soakDeadline);
            ReportMemory();
        }

        // --- Final canary ---
        await CanaryAsync(client);

        // --- Summary ---
        var exitCode = 0;
        if (_fuzzErrors > 0)
        {
            Console.Error.WriteLine($"FAIL: {_fuzzErrors} fuzz errors detected");
            foreach (var detail in _errorDetails.Take(20))
                Console.Error.WriteLine($"  {detail}");
            exitCode = 1;
        }

        if (_fuzzHangs > 0)
        {
            Console.Error.WriteLine($"WARN: {_fuzzHangs} potential hangs (timeouts)");
        }

        var wsDelta = _peakWorkingSet - _baselineWorkingSet;
        Console.WriteLine($"Peak working set delta: {wsDelta >> 20} MB (baseline: {_baselineWorkingSet >> 20} MB, peak: {_peakWorkingSet >> 20} MB)");
        if (wsDelta > 1L << 30) // 1 GiB
        {
            Console.Error.WriteLine($"FAIL: working set grew more than 1 GiB ({wsDelta >> 20} MB)");
            exitCode = 1;
        }

        Console.WriteLine(exitCode == 0 ? "PASS: Elsie.Fuzz" : "FAIL: Elsie.Fuzz");
        return exitCode;
    }

    // ================================================================
    //  HTTP/1.1 fuzz
    // ================================================================

    private static async Task FuzzHttp1Async(int port, DateTime deadline)
    {
        if (DateTime.UtcNow >= deadline) return;

        try
        {
            using var tcp = new TcpClient();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync("127.0.0.1", port, connectCts.Token);
            using var stream = tcp.GetStream();

            // Batch many inputs over one keep-alive connection: connection setup dominates
            // per-iteration cost, so reuse it and keep per-request read budgets short.
            for (var i = 0; i < 16 && DateTime.UtcNow < deadline; i++)
            {
                var raw = GenerateHttp1Request();
                try
                {
                    await stream.WriteAsync(raw, connectCts.Token);

                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token);
                    readCts.CancelAfter(TimeSpan.FromMilliseconds(400));
                    var response = await TryReadResponseAsync(stream, readCts.Token);
                    if (!response.HasValue)
                    {
                        break; // connection closed / unparseable / timeout — reconnect next iteration
                    }

                    Interlocked.Increment(ref _totalFuzzIterations);
                    if (response.Value.statusCode >= 500)
                    {
                        Interlocked.Increment(ref _fuzzErrors);
                        _errorDetails.Add($"H1 5xx: {response.Value.statusCode} (request {DescribeFirstLine(raw)})");
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _fuzzHangs);
                    break;
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    break; // server closed the connection — reconnect next iteration
                }
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _fuzzHangs);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            // connection reset/refused is normal for malformed input
        }
    }

    private static byte[] GenerateHttp1Request()
    {
        using var ms = new MemoryStream();
        var method = _methods[_rng.Next(_methods.Length)];
        var path = _paths[_rng.Next(_paths.Length)];

        // 20% chance: random bytes (pure garbage)
        if (_rng.NextDouble() < 0.2)
        {
            var len = _rng.Next(1, 512);
            var buf = new byte[len];
            _rng.NextBytes(buf);
            return buf;
        }

        // 10% chance: HTTP/0.9 style
        if (_rng.NextDouble() < 0.1)
        {
            ms.Write(Encoding.ASCII.GetBytes($"{method} {path}\r\n"));
            return ms.ToArray();
        }

        // Request line
        var version = _rng.NextDouble() < 0.9 ? "HTTP/1.1" : (_rng.NextDouble() < 0.5 ? "HTTP/1.0" : "HTTP/0.9");
        // 10%: malformed request line
        if (_rng.NextDouble() < 0.1)
        {
            var malformed = new[] { "GET /", "GET / HTTP/1.1", "GET / HTTP/1.1\r\n", "GET / HTTP/1.1\r\n\r\n", "GET / HTTP/1.1\r\nHost: \r\n\r\n", "GET / HTTP/1.1\r\nContent-Length: 0\r\n\r\n" };
            ms.Write(Encoding.ASCII.GetBytes(malformed[_rng.Next(malformed.Length)]));
            return ms.ToArray();
        }

        ms.Write(Encoding.ASCII.GetBytes($"{method} {path} {version}\r\n"));

        // Headers
        var headerCount = _rng.Next(0, 8);
        var hasContentLength = false;
        long contentLength = 0;
        var hasTransferEncoding = false;

        for (var i = 0; i < headerCount; i++)
        {
            var name = _headerNames[_rng.Next(_headerNames.Length)];
            var value = _headerValues[_rng.Next(_headerValues.Length)];

            // 10%: random header line (weird control chars etc.)
            if (_rng.NextDouble() < 0.1)
            {
                var junk = new byte[_rng.Next(1, 50)];
                _rng.NextBytes(junk);
                ms.Write(junk);
                ms.WriteByte((byte)'\r');
                ms.WriteByte((byte)'\n');
                continue;
            }

            // Track Content-Length and Transfer-Encoding
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                hasContentLength = true;
                contentLength = _rng.Next(0, 1000);
                value = contentLength.ToString();
            }

            if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                hasTransferEncoding = true;
                value = _rng.NextDouble() < 0.5 ? "chunked" : "identity";
            }

            if (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase))
            {
                value = _rng.NextDouble() < 0.5 ? "keep-alive" : "close";
            }

            // 5%: header folding (obsolete but allowed)
            if (_rng.NextDouble() < 0.05 && _rng.NextDouble() < 0.5)
            {
                ms.Write(Encoding.ASCII.GetBytes($"{name}: {value}\r\n {_rng.NextDouble()}\r\n"));
            }
            else
            {
                var lineEnding = _rng.NextDouble() < 0.95 ? "\r\n" : (_rng.NextDouble() < 0.5 ? "\n" : "\r");
                ms.Write(Encoding.ASCII.GetBytes($"{name}: {value}{lineEnding}"));
            }
        }

        // 10%: smuggling attempt — both Content-Length and Transfer-Encoding
        if (_rng.NextDouble() < 0.1 && !hasContentLength && !hasTransferEncoding)
        {
            ms.Write(Encoding.ASCII.GetBytes($"Content-Length: 0\r\n"));
            ms.Write(Encoding.ASCII.GetBytes($"Transfer-Encoding: chunked\r\n"));
        }

        // Duplicate Content-Length attempt
        if (_rng.NextDouble() < 0.05 && hasContentLength)
        {
            ms.Write(Encoding.ASCII.GetBytes($"Content-Length: {_rng.Next(0, 100)}\r\n"));
        }

        // End headers
        ms.Write(Encoding.ASCII.GetBytes("\r\n"));

        // Body (only for POST/PUT/PATCH)
        if (method is "POST" or "PUT" or "PATCH" && hasContentLength && contentLength > 0)
        {
            var body = new byte[contentLength];
            _rng.NextBytes(body);
            ms.Write(body);
        }

        return ms.ToArray();
    }

    // ================================================================
    //  HTTP/2 fuzz
    // ================================================================

    private static async Task FuzzHttp2Async(int port, X509Certificate2 cert, DateTime deadline)
    {
        if (DateTime.UtcNow >= deadline) return;

        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token);

            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [SslApplicationProtocol.Http2],
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            };
            await ssl.AuthenticateAsClientAsync(sslOptions, cts.Token);

            // Send client preface + SETTINGS
            await ssl.WriteAsync(_h2Preface, cts.Token);
            // Empty SETTINGS frame: 9-byte header, length=0, type=0x4 (SETTINGS), flags=0, stream-id=0
            var settingsFrame = new byte[9];
            settingsFrame[3] = 0x04; // type SETTINGS (index 3; 0-2 are the 24-bit length)
            await ssl.WriteAsync(settingsFrame, cts.Token);

            // Batch several random-frame groups over one TLS connection (connection setup is
            // the dominant cost; each valid response leaves the connection reusable).
            for (var i = 0; i < 8 && DateTime.UtcNow < deadline; i++)
            {
                var frames = GenerateH2Frames();
                await ssl.WriteAsync(frames, cts.Token);

                // Short budget: keep-alive connections stay open after a valid response, so
                // never wait long for the next frame.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                readCts.CancelAfter(TimeSpan.FromMilliseconds(150));
                var status = await TryReadH2ResponseAsync(ssl, readCts.Token);
                if (!status.HasValue)
                {
                    break; // connection closed / GOAWAY / timeout — reconnect next iteration
                }

                Interlocked.Increment(ref _totalFuzzIterations);
                if (status.Value >= 500)
                {
                    Interlocked.Increment(ref _fuzzErrors);
                    _errorDetails.Add($"H2 5xx: {status.Value}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _fuzzHangs);
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            // Connection reset / TLS failure is normal for malformed input
        }
    }

    private static byte[] GenerateH2Frames()
    {
        using var ms = new MemoryStream();
        var streamId = _rng.Next(1, 1000) | 0x01; // odd-numbered client streams

        // 30%: pure random bytes
        if (_rng.NextDouble() < 0.3)
        {
            var len = _rng.Next(1, 1024);
            var buf = new byte[len];
            _rng.NextBytes(buf);
            // Ensure the first 9 bytes could be a valid-ish frame header (but random)
            buf[2] = (byte)_rng.Next(0x01, 0x0A); // type
            return buf;
        }

        // 30%: valid-ish HEADERS frame with HPACK block
        if (_rng.NextDouble() < 0.3)
        {
            var hpack = GenerateHpackBlock();
            var frameLen = hpack.Length;
            // Frame header: length(24-bit BE) | type(0x01) | flags(END_STREAM=0x01 | END_HEADERS=0x04 = 0x05) | stream-id(31-bit)
            WriteFrameHeader(ms, frameLen, 0x01, 0x05, streamId);
            ms.Write(hpack);
            return ms.ToArray();
        }

        // 20%: valid HEADERS with random HPACK mixed with garbage
        if (_rng.NextDouble() < 0.2)
        {
            var hpack = GenerateHpackBlock();
            // Truncate at random point
            var truncate = _rng.Next(0, Math.Max(1, hpack.Length));
            var frameLen = truncate;
            WriteFrameHeader(ms, frameLen, 0x01, 0x05, streamId);
            ms.Write(hpack, 0, truncate);
            return ms.ToArray();
        }

        // 20%: random frames (DATA, RST_STREAM, PING, GOAWAY, WINDOW_UPDATE, PRIORITY, SETTINGS)
        var frameType = (byte)_rng.Next(0x00, 0x0A);
        var payloadLen = _rng.Next(0, 256);
        var flags = (byte)(frameType == 0x01 ? 0x05 : _rng.Next(0, 256));
        WriteFrameHeader(ms, payloadLen, frameType, flags, streamId);
        var payload = new byte[payloadLen];
        _rng.NextBytes(payload);
        ms.Write(payload);
        return ms.ToArray();
    }

    private static void WriteFrameHeader(MemoryStream ms, int length, byte type, byte flags, int streamId)
    {
        // 24-bit length (big-endian)
        ms.WriteByte((byte)((length >> 16) & 0xFF));
        ms.WriteByte((byte)((length >> 8) & 0xFF));
        ms.WriteByte((byte)(length & 0xFF));
        // type
        ms.WriteByte(type);
        // flags
        ms.WriteByte(flags);
        // 31-bit stream-id (big-endian)
        ms.WriteByte((byte)((streamId >> 24) & 0x7F));
        ms.WriteByte((byte)((streamId >> 16) & 0xFF));
        ms.WriteByte((byte)((streamId >> 8) & 0xFF));
        ms.WriteByte((byte)(streamId & 0xFF));
    }

    /// <summary>Generates a minimal HPACK-encoded header block with random values.</summary>
    private static byte[] GenerateHpackBlock()
    {
        using var ms = new MemoryStream();

        // 50%: use indexed header field for :method
        if (_rng.NextDouble() < 0.5)
        {
            // Indexed header field: 1xxxxxxx — index 2 = :method GET, 3 = POST
            ms.WriteByte((byte)(0x80 | (_rng.NextDouble() < 0.5 ? 2 : 3)));
        }
        else
        {
            // Literal header field with incremental indexing: 01xxxxxx name index, then value
            WriteHpackLiteral(ms, incremental: true, nameIndex: 2, value: "GET");
        }

        // :path = literal (index 4 = /, index 5 = /index.html, name index 4 with literal value)
        var path = _paths[_rng.Next(_paths.Length)];
        WriteHpackLiteral(ms, incremental: false, nameIndex: 4, value: path);

        // :scheme = index 6 (http) — always use indexed
        ms.WriteByte((byte)(0x80 | 6));

        // :authority = literal
        var authority = _rng.NextDouble() < 0.5 ? "localhost" : "127.0.0.1:" + _rng.Next(1024, 65535);
        WriteHpackLiteral(ms, incremental: false, nameIndex: 1, value: authority);

        // Random extra headers
        var extraHeaders = _rng.Next(0, 4);
        for (var i = 0; i < extraHeaders; i++)
        {
            var name = _headerNames[_rng.Next(_headerNames.Length)];
            var value = _headerValues[_rng.Next(_headerValues.Length)];
            // Literal with no indexing (name as literal string)
            WriteHpackLiteralValue(ms, incremental: false, name, value);
        }

        return ms.ToArray();
    }

    private static void WriteHpackLiteral(MemoryStream ms, bool incremental, int nameIndex, string value)
    {
        // 01xxxxxx (incremental) or 0000xxxx (no indexing)
        var prefix = incremental ? 0x40 : 0x00;
        // 4-bit prefix for the value
        var valueBytes = Encoding.ASCII.GetBytes(value);
        if (valueBytes.Length < 15)
        {
            ms.WriteByte((byte)(prefix | valueBytes.Length));
        }
        else
        {
            ms.WriteByte((byte)(prefix | 15));
            WriteInteger(ms, valueBytes.Length - 15, 0xFF);
        }
        ms.Write(valueBytes);
    }

    private static void WriteHpackLiteralValue(MemoryStream ms, bool incremental, string name, string value)
    {
        var prefix = incremental ? 0x40 : 0x00;
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var valueBytes = Encoding.ASCII.GetBytes(value);

        // Name length (4-bit prefix)
        if (nameBytes.Length < 15)
        {
            ms.WriteByte((byte)(prefix | nameBytes.Length));
        }
        else
        {
            ms.WriteByte((byte)(prefix | 15));
            WriteInteger(ms, nameBytes.Length - 15, 0xFF);
        }
        ms.Write(nameBytes);

        // Value length (8-bit prefix, starts with 0x00)
        if (valueBytes.Length < 127)
        {
            ms.WriteByte((byte)valueBytes.Length);
        }
        else
        {
            ms.WriteByte(127);
            WriteInteger(ms, valueBytes.Length - 127, 0xFF);
        }
        ms.Write(valueBytes);
    }

    private static void WriteInteger(MemoryStream ms, int value, byte prefixMask)
    {
        while (value >= prefixMask)
        {
            ms.WriteByte((byte)(prefixMask | (value & 0x7F)));
            value = (value >> 7) - 1;
            // Continue with 8-bit prefix after first byte
            prefixMask = 0xFF;
        }
        ms.WriteByte((byte)value);
    }

    // ================================================================
    //  Response parsing
    // ================================================================

    private static async Task<int?> TryReadH2ResponseAsync(SslStream stream, CancellationToken ct)
    {
        // Read frames until we find HEADERS (0x1) or GOAWAY (0x7) or timeout
        var headerBuf = new byte[9];
        var accumulatedPayload = new MemoryStream();

        while (!ct.IsCancellationRequested)
        {
            var read = await ReadExactAsync(stream, headerBuf, ct);
            if (read == 0) return null; // connection closed

            var length = (headerBuf[0] << 16) | (headerBuf[1] << 8) | headerBuf[2];
            var type = headerBuf[3];
            var flags = headerBuf[4];
            // Stream ID (31 bits)
            // var streamId = ((headerBuf[5] & 0x7F) << 24) | (headerBuf[6] << 16) | (headerBuf[7] << 8) | headerBuf[8];

            if (length > 64 * 1024)
                return null; // frame too large, skip

            var frame = new byte[length];
            read = await ReadExactAsync(stream, frame, ct);
            if (read == 0) return null;

            if (type == 0x01) // HEADERS
            {
                var hasEndHeaders = (flags & 0x04) != 0;
                accumulatedPayload.Write(frame);

                if (hasEndHeaders)
                {
                    var data = accumulatedPayload.ToArray();
                    accumulatedPayload.SetLength(0);
                    return ParseH2Status(data);
                }
                // else: wait for CONTINUATION
            }
            else if (type == 0x09) // CONTINUATION
            {
                accumulatedPayload.Write(frame);
                var hasEndHeaders = (flags & 0x04) != 0;
                if (hasEndHeaders)
                {
                    var data = accumulatedPayload.ToArray();
                    accumulatedPayload.SetLength(0);
                    return ParseH2Status(data);
                }
            }
            else if (type == 0x07) // GOAWAY
            {
                return null; // connection closing, no response
            }
            // else skip other frame types
        }

        return null;
    }

    private static int? ParseH2Status(byte[] hpackData)
    {
        if (hpackData.Length == 0) return null;

        // Try to parse the first HPACK entry as an indexed header field (:status)
        // 1xxxxxxx = indexed header field
        if ((hpackData[0] & 0x80) != 0)
        {
            var index = hpackData[0] & 0x7F;
            // Static table indices 8-14 correspond to :status values
            // 8=200, 9=204, 10=206, 11=304, 12=400, 13=404, 14=500
            return index switch
            {
                8 => 200,
                9 => 204,
                10 => 206,
                11 => 304,
                12 => 400,
                13 => 404,
                14 => 500,
                _ => null
            };
        }

        // 01xxxxxx = literal with incremental indexing (name indexed)
        if ((hpackData[0] & 0xC0) == 0x40)
        {
            // Skip name index prefix + value
            // The name index is in the low 6 bits (or extended)
            // We can't easily decode the value, but we know it's not :status because
            // :status is always sent as indexed header field (from the static table)
            return null;
        }

        // 0000xxxx = literal without/never indexing (name as literal or indexed)
        // These are unlikely to be :status, skip
        return null;
    }

    private static async Task<(int statusCode, string body)?> TryReadResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var buffer = new byte[8192];
            var total = 0;

            // Read until end of headers (\r\n\r\n), bounded.
            while (total < buffer.Length - 1)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total - 1), linked.Token);
                if (read == 0) break;
                total += read;
                if (ContainsEndOfHeaders(buffer, total)) break;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, total);
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                // Header terminator not seen — treat what we got as a partial status line only.
                headerEnd = total;
            }

            var head = text[..Math.Min(headerEnd, text.Length)];
            var statusLine = head.Split('\r', '\n')[0];
            if (!statusLine.StartsWith("HTTP/", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = statusLine.Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[1], out var code))
            {
                return null;
            }

            // Read the body only if a small Content-Length is present (keep-alive connections
            // never reach EOF, so never ReadToEnd).
            var contentLength = 0L;
            foreach (var line in head.Split('\r', '\n'))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(line["Content-Length:".Length..].Trim(), out contentLength))
                {
                    break;
                }
            }

            var body = string.Empty;
            if (contentLength > 0 && contentLength <= 4096)
            {
                var alreadyRead = Math.Max(0, total - headerEnd - 4);
                var need = (int)contentLength - alreadyRead;
                if (need > 0)
                {
                    var bodyBuf = new byte[need];
                    var got = await ReadExactAsync(stream, bodyBuf, linked.Token);
                    body = Encoding.UTF8.GetString(bodyBuf, 0, got);
                }
            }

            return (code, body);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool ContainsEndOfHeaders(byte[] buffer, int length)
    {
        for (var i = 0; i < length - 3; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' &&
                buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }

    // ================================================================
    //  Soak
    // ================================================================

    private static async Task SoakAsync(ElsieTestServer server, DateTime deadline)
    {
        // h1 client (shared/cached instance — do not dispose here)
        var httpClient = server.CreateClient();
        // h2 client
        var h2Handler = CreateH2Handler();
        using var h2Client = new HttpClient(h2Handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{server.Endpoints[1].Port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var clients = new[] { httpClient, h2Client };
        var soakErrors = new ConcurrentBag<(string path, int status, string? message)>();
        var soakCount = 0;
        using var semaphore = new SemaphoreSlim(16, 16);
        var inflight = new List<Task>();
        var lastReport = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            await semaphore.WaitAsync();
            inflight.Add(Task.Run(async () =>
            {
                try
                {
                    var client = clients[_rng.Next(clients.Length)];
                    var path = _paths[_rng.Next(_paths.Length)];
                    var method = _methods[_rng.Next(_methods.Length)];
                    var body = _rng.NextDouble() < 0.3 ? "large:" + new string('x', _rng.Next(100, 2000)) : "ok";
                    var (code, msg) = await MakeRequestAsync(client, path, method, body);
                    if (code >= 500)
                    {
                        soakErrors.Add((path, code, msg));
                    }
                    else
                    {
                        Interlocked.Increment(ref soakCount);
                    }
                }
                catch (Exception ex)
                {
                    soakErrors.Add(("_", 0, ex.GetType().Name));
                }
                finally
                {
                    semaphore.Release();
                }
            }));

            if (inflight.Count >= 64)
            {
                inflight.RemoveAll(t => t.IsCompleted);
            }

            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 5)
            {
                lastReport = DateTime.UtcNow;
                ReportMemory();
                Console.WriteLine($"  soak: {soakCount} requests so far");
            }
        }

        await Task.WhenAll(inflight);
        ReportMemory();

        Console.WriteLine($"Soak: {soakCount} requests, {soakErrors.Count} errors");
        foreach (var err in soakErrors.Take(10))
        {
            Console.Error.WriteLine($"  Soak error: {err.path} = {err.status} {err.message}");
        }

        if (soakErrors.Any(e => e.status >= 500))
        {
            Interlocked.Increment(ref _fuzzErrors);
            _errorDetails.Add("Soak: 5xx responses detected");
        }
    }

    private static async Task<(int statusCode, string? message)> MakeRequestAsync(HttpClient client, string path, string method, string? body)
    {
        try
        {
            HttpResponseMessage res;
            if (body is not null)
            {
                var content = new StringContent(body, Encoding.UTF8, "application/octet-stream");
                res = method switch
                {
                    "POST" => await client.PostAsync(path, content),
                    "PUT" => await client.PutAsync(path, content),
                    "PATCH" => await client.PatchAsync(path, content),
                    _ => await client.SendAsync(new HttpRequestMessage(HttpMethod.Parse(method), path) { Content = content })
                };
            }
            else
            {
                res = await client.GetAsync(path);
            }

            return ((int)res.StatusCode, null);
        }
        catch (HttpRequestException ex)
        {
            return (0, ex.Message);
        }
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static async Task CanaryAsync(HttpClient client)
    {
        try
        {
            using var res = await client.GetAsync("/");
            if (res.StatusCode != HttpStatusCode.OK)
            {
                Interlocked.Increment(ref _fuzzErrors);
                var body = await res.Content.ReadAsStringAsync();
                _errorDetails.Add($"Canary FAILED: {res.StatusCode} {body}");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _fuzzErrors);
            _errorDetails.Add($"Canary CRASHED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ReportMemory()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
        var ws = Environment.WorkingSet;
        InterlockedHelper.SetMax(ref _peakWorkingSet, ws);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) return offset;
            offset += read;
        }
        return offset;
    }

    private static string DescribeFirstLine(byte[] raw)
    {
        // Extract first line for logging
        var end = Array.IndexOf(raw, (byte)'\n');
        if (end < 0) end = Math.Min(raw.Length, 80);
        return Encoding.ASCII.GetString(raw, 0, end).Replace("\r", "").Replace("\n", "");
    }

    private static string PrepareStaticDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "elsie-fuzz-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        // Create a ~512KB file for streaming soak tests
        var bigFile = Path.Combine(dir, "big.bin");
        if (!File.Exists(bigFile))
        {
            var data = new byte[512 * 1024];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(bigFile, data);
        }
        return dir;
    }

    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), (string?)null);
    }

    private static SocketsHttpHandler CreateH2Handler()
    {
        return new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
            },
            EnableMultipleHttp2Connections = true
        };
    }
}

/// <summary>Thread-safe max helper.</summary>
internal static class InterlockedHelper
{
    public static void SetMax(ref long field, long value)
    {
        long prior;
        do
        {
            prior = Interlocked.Read(ref field);
            if (value <= prior) return;
        } while (Interlocked.CompareExchange(ref field, value, prior) != prior);
    }
}

/// <summary>Fuzz target module — simple routes for the fuzzer to hit.</summary>
public sealed class FuzzModule : ElsieModule
{
    public FuzzModule()
    {
        Get("/", static () => ElsieResult.Text("ok"));
        Get("/json", static () => ElsieResult.Json(new { status = "ok" }));
        Post("/echo", static async (ctx, ct) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var body = await sr.ReadToEndAsync(ct);
            return ElsieResult.Text(body);
        });
    }
}

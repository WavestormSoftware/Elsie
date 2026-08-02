using System.Buffers;
using System.Globalization;
using System.Text;

namespace Elsie.Web.Http;

/// <summary>Reads one HTTP/1.x request from a duplex stream (supports leftover buffer for keep-alive).</summary>
internal sealed class Http1RequestReader
{
    private readonly Stream _stream;
    private readonly int _maxRequestLineLength;
    private readonly int _maxHeaderBytes;
    private long _maxBodyBytes = 10 * 1024 * 1024;
    private readonly bool _send100Continue;
    private readonly TimeSpan _bodyIdleTimeout;
    private byte[] _buffer;
    private int _offset;
    private int _count;

    public Http1RequestReader(
        Stream stream,
        int maxRequestLineLength = 8 * 1024,
        int maxHeaderBytes = 32 * 1024,
        long maxBodyBytes = 10 * 1024 * 1024,
        bool send100Continue = true,
        TimeSpan? requestBodyIdleTimeout = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _maxRequestLineLength = maxRequestLineLength;
        _maxHeaderBytes = maxHeaderBytes;
        _maxBodyBytes = maxBodyBytes > 0 ? maxBodyBytes : 10 * 1024 * 1024;
        _send100Continue = send100Continue;
        _bodyIdleTimeout = requestBodyIdleTimeout is { } t && t > TimeSpan.Zero
            ? t
            : Timeout.InfiniteTimeSpan;
        _buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
    }

    public void DisposeBuffer()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }
    }

    public async Task<ParsedHttpRequest?> ReadAsync(CancellationToken cancellationToken)
    {
        var headerBytes = await ReadHeadersBlockAsync(cancellationToken).ConfigureAwait(false);
        if (headerBytes is null)
        {
            return null; // EOF before any bytes
        }

        var text = Encoding.ASCII.GetString(headerBytes);
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrEmpty(lines[0]))
        {
            throw new InvalidOperationException("Empty request line.");
        }

        var requestLine = lines[0];
        if (requestLine.Length > _maxRequestLineLength)
        {
            throw new InvalidOperationException("Request line too long.");
        }

        var parts = requestLine.Split(' ');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Malformed request line.");
        }

        var method = parts[0];
        var target = parts[1];
        var protocol = parts[2];

        if (!protocol.StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported protocol '{protocol}'.");
        }

        var (path, queryString, queryValues) = SplitTarget(target);

        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidOperationException("Malformed header line.");
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!headers.TryGetValue(name, out var list))
            {
                list = new List<string>(1);
                headers[name] = list;
            }

            list.Add(value);
        }

        var hasContentLength = headers.TryGetValue("Content-Length", out var clValues) && clValues.Count > 0;
        var hasTransferEncoding = headers.TryGetValue("Transfer-Encoding", out var teValues) && teValues.Count > 0;

        // Request smuggling: never accept both framing headers.
        if (hasContentLength && hasTransferEncoding)
        {
            throw new InvalidOperationException(
                "Request smuggling attempt: both Content-Length and Transfer-Encoding.");
        }

        long? contentLength = null;
        if (hasContentLength)
        {
            contentLength = ParseContentLength(clValues!);
        }

        var chunked = false;
        if (hasTransferEncoding)
        {
            chunked = IsChunkedOnly(teValues!);
        }

        var contentType = headers.TryGetValue("Content-Type", out var ct) && ct.Count > 0 ? ct[0] : null;
        var keepAlive = IsKeepAlive(protocol, headers);

        var expectsBody = contentLength is > 0 || chunked;
        if (_send100Continue && expectsBody && HasExpect100Continue(headers))
        {
            await _stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        Stream body;
        if (contentLength is > 0)
        {
            body = CreateContentLengthBody(contentLength.Value);
        }
        else if (chunked)
        {
            // SC2 replaces this with streaming ChunkedReadStream.
            body = await ReadChunkedBodyAsync(cancellationToken).ConfigureAwait(false);
            contentLength = body.Length;
        }
        else
        {
            body = Stream.Null;
        }

        // Materialize header dictionary as IReadOnlyList values
        var headerRo = new Dictionary<string, IReadOnlyList<string>>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers)
        {
            headerRo[k] = v;
        }

        // Stash raw headers for writer path via returning mutable + ro
        return new ParsedHttpRequest
        {
            Method = method,
            Path = path,
            QueryString = queryString,
            QueryValues = queryValues,
            Protocol = protocol,
            Headers = headers,
            Body = body,
            ContentLength = contentLength,
            ContentType = contentType,
            KeepAlive = keepAlive
        };
    }

    private static long ParseContentLength(List<string> values)
    {
        long? parsed = null;
        foreach (var raw in values)
        {
            // A single header value may be comma-separated (proxy join).
            foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var cl) || cl < 0)
                {
                    throw new InvalidOperationException("Invalid Content-Length.");
                }

                if (parsed is null)
                {
                    parsed = cl;
                }
                else if (parsed.Value != cl)
                {
                    throw new InvalidOperationException("Invalid Content-Length.");
                }
            }
        }

        if (parsed is null)
        {
            throw new InvalidOperationException("Invalid Content-Length.");
        }

        return parsed.Value;
    }

    /// <summary>
    /// TE must be exactly <c>chunked</c> (optionally repeated). Any other coding → 400.
    /// </summary>
    private static bool IsChunkedOnly(List<string> values)
    {
        var sawChunked = false;
        foreach (var raw in values)
        {
            foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // Strip transfer-extension parameters (e.g. chunked;foo=bar).
                var semi = part.IndexOf(';');
                var coding = (semi >= 0 ? part[..semi] : part).Trim();
                if (coding.Length == 0)
                {
                    continue;
                }

                if (!coding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Unsupported Transfer-Encoding.");
                }

                sawChunked = true;
            }
        }

        if (!sawChunked)
        {
            throw new InvalidOperationException("Unsupported Transfer-Encoding.");
        }

        return true;
    }

    private static bool HasExpect100Continue(Dictionary<string, List<string>> headers)
    {
        if (!headers.TryGetValue("Expect", out var values) || values.Count == 0)
        {
            return false;
        }

        foreach (var raw in values)
        {
            foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsKeepAlive(string protocol, Dictionary<string, List<string>> headers)
    {
        if (headers.TryGetValue("Connection", out var conn) && conn.Count > 0)
        {
            var v = conn[0];
            if (v.Contains("close", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (v.Contains("keep-alive", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // HTTP/1.1 default keep-alive; HTTP/1.0 default close
        return protocol.StartsWith("HTTP/1.1", StringComparison.Ordinal);
    }

    private static (string Path, string QueryString, IReadOnlyDictionary<string, IReadOnlyList<string>> QueryValues)
        SplitTarget(string target)
    {
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var abs))
            {
                target = abs.PathAndQuery;
            }
        }

        if (target == "*")
        {
            return ("/", string.Empty, new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));
        }

        var q = target.IndexOf('?');
        string path;
        string queryString;
        if (q < 0)
        {
            path = target.Length == 0 ? "/" : target;
            queryString = string.Empty;
        }
        else
        {
            path = q == 0 ? "/" : target[..q];
            queryString = target[q..]; // includes ?
        }

        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        var queryValues = ParseQuery(queryString);
        return (path, queryString, queryValues);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseQuery(string queryString)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(queryString) || queryString == "?")
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        var span = queryString.AsSpan();
        if (span[0] == '?')
        {
            span = span[1..];
        }

        foreach (var segment in span.ToString().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string key;
            string value;
            var eq = segment.IndexOf('=');
            if (eq < 0)
            {
                key = Uri.UnescapeDataString(segment.Replace('+', ' '));
                value = string.Empty;
            }
            else
            {
                key = Uri.UnescapeDataString(segment[..eq].Replace('+', ' '));
                value = Uri.UnescapeDataString(segment[(eq + 1)..].Replace('+', ' '));
            }

            if (!result.TryGetValue(key, out var list))
            {
                list = new List<string>(1);
                result[key] = list;
            }

            list.Add(value);
        }

        var ro = new Dictionary<string, IReadOnlyList<string>>(result.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in result)
        {
            ro[k] = v;
        }

        return ro;
    }

    private async Task<byte[]?> ReadHeadersBlockAsync(CancellationToken cancellationToken)
    {
        // Ensure we have data
        if (_count == 0)
        {
            var read = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            _offset = 0;
            _count = read;
        }

        using var ms = new MemoryStream();
        while (true)
        {
            var available = _buffer.AsSpan(_offset, _count);
            var idx = IndexOfDoubleCrlf(available);
            if (idx >= 0)
            {
                var blockLen = idx + 4;
                if (ms.Length + blockLen > _maxHeaderBytes)
                {
                    throw new InvalidOperationException("Request headers too large.");
                }

                ms.Write(available[..blockLen]);
                _offset += blockLen;
                _count -= blockLen;
                if (_count == 0)
                {
                    _offset = 0;
                }

                return ms.ToArray();
            }

            // Need more data
            if (ms.Length + _count > _maxHeaderBytes)
            {
                throw new InvalidOperationException("Request headers too large.");
            }

            ms.Write(available);
            _offset = 0;
            _count = 0;
            var n = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                if (ms.Length == 0)
                {
                    return null;
                }

                throw new InvalidOperationException("Unexpected EOF in headers.");
            }

            _count = n;
        }
    }

    private static int IndexOfDoubleCrlf(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i < span.Length - 3; i++)
        {
            if (span[i] == (byte)'\r' && span[i + 1] == (byte)'\n' &&
                span[i + 2] == (byte)'\r' && span[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Build a streaming Content-Length body. Does not read from the network yet;
    /// leftover header-buffer bytes are captured as a prefix.
    /// </summary>
    private Stream CreateContentLengthBody(long contentLength)
    {
        if (contentLength > _maxBodyBytes)
        {
            throw new InvalidOperationException("Body too large.");
        }

        byte[]? prefix = null;
        if (_count > 0)
        {
            var take = (int)Math.Min(_count, contentLength);
            if (take > 0)
            {
                prefix = new byte[take];
                _buffer.AsSpan(_offset, take).CopyTo(prefix);
                _offset += take;
                _count -= take;
                if (_count == 0)
                {
                    _offset = 0;
                }
            }
        }

        return new ElsieRequestBodyStream(_stream, contentLength, _bodyIdleTimeout, prefix);
    }

    private async Task<Stream> ReadChunkedBodyAsync(CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (sizeLine is null)
            {
                throw new InvalidOperationException("Unexpected EOF in chunk size.");
            }

            var semi = sizeLine.IndexOf(';');
            var hex = semi >= 0 ? sizeLine[..semi] : sizeLine;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new InvalidOperationException("Invalid chunk size.");
            }

            if (ms.Length + size > _maxBodyBytes)
            {
                throw new InvalidOperationException("Body too large.");
            }

            if (size == 0)
            {
                // Trailer headers until empty line
                while (true)
                {
                    var trailer = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (trailer is null || trailer.Length == 0)
                    {
                        break;
                    }
                }

                break;
            }

            var chunk = new byte[size];
            var filled = 0;
            while (filled < size)
            {
                if (_count > 0)
                {
                    var take = Math.Min(_count, size - filled);
                    _buffer.AsSpan(_offset, take).CopyTo(chunk.AsSpan(filled, take));
                    filled += take;
                    _offset += take;
                    _count -= take;
                    if (_count == 0)
                    {
                        _offset = 0;
                    }

                    continue;
                }

                var n = await ReadSocketAsync(chunk.AsMemory(filled, size - filled), cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    throw new InvalidOperationException("Unexpected EOF in chunk.");
                }

                filled += n;
            }

            ms.Write(chunk, 0, size);
            // Consume trailing CRLF
            var crlf = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (crlf is null)
            {
                throw new InvalidOperationException("Unexpected EOF after chunk.");
            }
        }

        ms.Position = 0;
        return new MemoryStream(ms.ToArray(), writable: false);
    }

    private async Task<int> ReadSocketAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_bodyIdleTimeout == Timeout.InfiniteTimeSpan)
        {
            return await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(_bodyIdleTimeout);
        try
        {
            return await _stream.ReadAsync(destination, idleCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Request body read timed out.");
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            if (_count == 0)
            {
                var n = await ReadSocketAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray());
                }

                _offset = 0;
                _count = n;
            }

            var span = _buffer.AsSpan(_offset, _count);
            var crlf = span.IndexOf((byte)'\n');
            if (crlf >= 0)
            {
                var end = crlf;
                if (end > 0 && span[end - 1] == (byte)'\r')
                {
                    end--;
                }

                if (ms.Length + end > _maxRequestLineLength)
                {
                    throw new InvalidOperationException("Line too long.");
                }

                ms.Write(span[..end]);
                var consumed = crlf + 1;
                _offset += consumed;
                _count -= consumed;
                if (_count == 0)
                {
                    _offset = 0;
                }

                return Encoding.ASCII.GetString(ms.ToArray());
            }

            if (ms.Length + span.Length > _maxRequestLineLength)
            {
                throw new InvalidOperationException("Line too long.");
            }

            ms.Write(span);
            _offset = 0;
            _count = 0;
        }
    }
}

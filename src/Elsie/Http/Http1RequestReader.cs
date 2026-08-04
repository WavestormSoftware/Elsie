using System.Globalization;
using System.Text;

namespace Elsie.Web.Http;

/// <summary>Reads one HTTP/1.x request from a duplex stream (supports leftover buffer for keep-alive).</summary>
internal sealed class Http1RequestReader
{
    private readonly Stream _stream;
    private readonly int _maxRequestLineLength;
    private readonly int _maxHeaderBytes;
    private readonly long _maxBodyBytes;
    private readonly bool _send100Continue;
    private readonly TimeSpan _bodyIdleTimeout;
    private readonly Http1ConnectionBuffer _input;

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
        _input = new Http1ConnectionBuffer(stream, capacity: 16 * 1024, _bodyIdleTimeout);
    }

    public void DisposeBuffer() => _input.Dispose();

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

        var (path, queryString, queryValues, isAbsoluteForm) = SplitTarget(target);

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

        // RFC 7230 §5.4: HTTP/1.1 requests require exactly one Host header unless the
        // request-target is absolute-form (the target itself carries the host). A missing,
        // empty/whitespace, or duplicate Host header is a 400 — the server MUST NOT guess.
        if (protocol.StartsWith("HTTP/1.1", StringComparison.Ordinal))
        {
            var hasHost = headers.TryGetValue("Host", out var hostValues) && hostValues!.Count > 0;
            if (hasHost && string.IsNullOrWhiteSpace(hostValues![0]))
            {
                throw new InvalidOperationException("Empty Host header.");
            }

            if (!hasHost && !isAbsoluteForm)
            {
                throw new InvalidOperationException("Missing Host header.");
            }

            if (hasHost && hostValues!.Count > 1)
            {
                throw new InvalidOperationException("Duplicate Host header.");
            }
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
            body = new ChunkedReadStream(
                _input,
                _maxBodyBytes,
                _maxRequestLineLength,
                maxTrailerBytes: _maxHeaderBytes);
            contentLength = null;
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

    private static (string Path, string QueryString, IReadOnlyDictionary<string, IReadOnlyList<string>> QueryValues, bool IsAbsoluteForm)
        SplitTarget(string target)
    {
        // RFC 7230 §5.3.2: absolute-form carries the host in the target itself, so the Host
        // header is not required (RFC 7230 §5.4). Track it for the Host-header validation.
        var isAbsoluteForm = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (isAbsoluteForm)
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var abs))
            {
                target = abs.PathAndQuery;
            }
        }

        if (target == "*")
        {
            return ("/", string.Empty, new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), isAbsoluteForm);
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
        return (path, queryString, queryValues, isAbsoluteForm);
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
        if (_input.Count == 0)
        {
            var read = await _input.FillAsync(cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
        }

        using var ms = new MemoryStream();
        while (true)
        {
            var available = _input.AvailableSpan;
            var idx = IndexOfDoubleCrlf(available);
            if (idx >= 0)
            {
                var blockLen = idx + 4;
                if (ms.Length + blockLen > _maxHeaderBytes)
                {
                    throw new InvalidOperationException("Request headers too large.");
                }

                ms.Write(available[..blockLen]);
                _input.Advance(blockLen);
                return ms.ToArray();
            }

            if (ms.Length + available.Length > _maxHeaderBytes)
            {
                throw new InvalidOperationException("Request headers too large.");
            }

            ms.Write(available);
            _input.ClearAvailable();
            var n = await _input.FillAsync(cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (ms.Length == 0)
                {
                    return null;
                }

                throw new InvalidOperationException("Unexpected EOF in headers.");
            }
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

        var prefix = contentLength > int.MaxValue
            ? _input.TakePrefix(int.MaxValue)
            : _input.TakePrefix((int)contentLength);

        return new ElsieRequestBodyStream(_stream, contentLength, _bodyIdleTimeout, prefix);
    }
}

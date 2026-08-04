using System.Globalization;
using System.Text;

namespace Elsie.Web.Http;

internal static class Http1ResponseWriter
{
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();
    private static readonly byte[] HeaderSep = ": "u8.ToArray();

    public static async Task WriteAsync(
        Stream stream,
        ElsieHttpResponse response,
        string protocol,
        bool keepAlive,
        bool headRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(response);

        await WriteEarlyHintsAsync(stream, response, cancellationToken).ConfigureAwait(false);

        var statusLine =
            $"{protocol} {response.StatusCode} {HttpReasonPhrases.Get(response.StatusCode)}\r\n";
        var statusBytes = Encoding.ASCII.GetBytes(statusLine);
        await stream.WriteAsync(statusBytes, cancellationToken).ConfigureAwait(false);

        var hasConnection = false;
        var hasContentLength = false;
        var hasContentType = false;
        var hasTransferEncoding = false;
        var hasDate = false;

        foreach (var (name, values) in response.Headers)
        {
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                hasTransferEncoding = true;
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                hasContentLength = true;
            }

            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                hasContentType = true;
            }

            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                hasConnection = true;
            }

            if (name.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                hasDate = true;
            }

            foreach (var value in values)
            {
                await WriteHeaderAsync(stream, name, value, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!hasDate)
        {
            await WriteHeaderAsync(stream, "Date", FormatHttpDate(DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(response.ContentType) && !hasContentType)
        {
            await WriteHeaderAsync(stream, "Content-Type", response.ContentType!, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!hasConnection)
        {
            await WriteHeaderAsync(
                    stream,
                    "Connection",
                    keepAlive ? "keep-alive" : "close",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var noBodyStatus = response.StatusCode is 204 or 304;

        byte[]? buffered = null;
        var streamBodyWriter = false;
        if (noBodyStatus)
        {
            buffered = Array.Empty<byte>();
        }
        else if (response.Body is { } memory)
        {
            buffered = memory.ToArray();
        }
        else if (response.BodyWriter is not null && !headRequest)
        {
            // Known Content-Length (e.g. static files): stream without buffering.
            // Unknown length: buffer so we can emit CL (callers that want true streaming
            // should use WriteChunkedAsync).
            if (hasContentLength)
            {
                streamBodyWriter = true;
            }
            else
            {
                await using var ms = new MemoryStream();
                await response.BodyWriter(ms, cancellationToken).ConfigureAwait(false);
                buffered = ms.ToArray();
            }
        }
        else if (response.BodyWriter is not null && headRequest)
        {
            // Don't run body writer for HEAD if it has side effects — prefer Body if present.
            buffered = Array.Empty<byte>();
        }

        // RFC 9110 §8.6: no Content-Length on 204; on 304 only when it equals the 200 payload (caller-set).
        if (buffered is not null && !noBodyStatus && !hasContentLength && !hasTransferEncoding)
        {
            await WriteHeaderAsync(
                    stream,
                    "Content-Length",
                    buffered.Length.ToString(CultureInfo.InvariantCulture),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (buffered is null && !streamBodyWriter && !hasContentLength && !hasTransferEncoding)
        {
            await WriteHeaderAsync(stream, "Content-Length", "0", cancellationToken).ConfigureAwait(false);
        }

        await stream.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);

        if (!headRequest && !noBodyStatus)
        {
            if (streamBodyWriter && response.BodyWriter is not null)
            {
                await response.BodyWriter(stream, cancellationToken).ConfigureAwait(false);
            }
            else if (buffered is { Length: > 0 })
            {
                await stream.WriteAsync(buffered, cancellationToken).ConfigureAwait(false);
            }
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes <c>103 Early Hints</c> (RFC 9118) with each pending Link before the final
    /// response, when the handler called <see cref="ElsieContext.SendEarlyHints"/>.</summary>
    private static async Task WriteEarlyHintsAsync(
        Stream stream,
        ElsieHttpResponse response,
        CancellationToken cancellationToken)
    {
        if (response.EarlyHints.Count == 0 || response.WebSocketHandler is not null)
        {
            return; // no hints, or an upgrade — skip
        }

        foreach (var link in response.EarlyHints)
        {
            var statusLine = "HTTP/1.1 103 Early Hints\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(statusLine), cancellationToken).ConfigureAwait(false);
            await WriteHeaderAsync(stream, "Link", link, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Write a streaming body with chunked transfer encoding (SSE, etc.).</summary>
    public static async Task WriteChunkedAsync(
        Stream stream,
        ElsieHttpResponse response,
        string protocol,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(response);
        if (response.BodyWriter is null)
        {
            await WriteAsync(stream, response, protocol, keepAlive, headRequest: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var statusLine =
            $"{protocol} {response.StatusCode} {HttpReasonPhrases.Get(response.StatusCode)}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(statusLine), cancellationToken).ConfigureAwait(false);

        var hasDate = false;
        foreach (var (name, values) in response.Headers)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                hasDate = true;
            }

            foreach (var value in values)
            {
                await WriteHeaderAsync(stream, name, value, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!hasDate)
        {
            await WriteHeaderAsync(stream, "Date", FormatHttpDate(DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(response.ContentType))
        {
            await WriteHeaderAsync(stream, "Content-Type", response.ContentType!, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteHeaderAsync(stream, "Transfer-Encoding", "chunked", cancellationToken).ConfigureAwait(false);
        await WriteHeaderAsync(
                stream,
                "Connection",
                keepAlive ? "keep-alive" : "close",
                cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);

        await using var chunked = new ChunkedWriteStream(stream);
        await response.BodyWriter(chunked, cancellationToken).ConfigureAwait(false);
        await chunked.CompleteAsync(cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatHttpDate(DateTimeOffset dto) =>
        dto.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

    private static async Task WriteHeaderAsync(
        Stream stream,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        await stream.WriteAsync(nameBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(HeaderSep, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(valueBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);
    }

    private sealed class ChunkedWriteStream : Stream
    {
        private readonly Stream _inner;
        private bool _completed;

        public ChunkedWriteStream(Stream inner) => _inner = inner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0 || _completed)
            {
                return;
            }

            // Sync path: no sync-over-async. Write chunk framing directly.
            var sizeLine = Encoding.ASCII.GetBytes($"{count:X}\r\n");
            _inner.Write(sizeLine, 0, sizeLine.Length);
            _inner.Write(buffer, offset, count);
            _inner.Write(Crlf, 0, Crlf.Length);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0 || _completed)
            {
                return;
            }

            var sizeLine = $"{buffer.Length:X}\r\n";
            await _inner.WriteAsync(Encoding.ASCII.GetBytes(sizeLine), cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _inner.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            await _inner.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }
}

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

        var statusLine =
            $"{protocol} {response.StatusCode} {HttpReasonPhrases.Get(response.StatusCode)}\r\n";
        var statusBytes = Encoding.ASCII.GetBytes(statusLine);
        await stream.WriteAsync(statusBytes, cancellationToken).ConfigureAwait(false);

        var hasConnection = false;
        var hasContentLength = false;
        var hasContentType = false;
        var hasTransferEncoding = false;

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

            foreach (var value in values)
            {
                await WriteHeaderAsync(stream, name, value, cancellationToken).ConfigureAwait(false);
            }
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

        byte[]? buffered = null;
        if (response.Body is { } memory)
        {
            buffered = memory.ToArray();
        }
        else if (response.BodyWriter is not null && !headRequest)
        {
            // Buffer streaming writers so we can set Content-Length (simpler than chunked for v1).
            // SSE / long streams: use chunked when Content-Length unknown — detect via flag later.
            await using var ms = new MemoryStream();
            await response.BodyWriter(ms, cancellationToken).ConfigureAwait(false);
            buffered = ms.ToArray();
        }
        else if (response.BodyWriter is not null && headRequest)
        {
            // Don't run body writer for HEAD if it has side effects — prefer Body if present.
            buffered = Array.Empty<byte>();
        }

        if (buffered is not null && !hasContentLength && !hasTransferEncoding)
        {
            await WriteHeaderAsync(
                    stream,
                    "Content-Length",
                    buffered.Length.ToString(CultureInfo.InvariantCulture),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (buffered is null && !hasContentLength && !hasTransferEncoding)
        {
            await WriteHeaderAsync(stream, "Content-Length", "0", cancellationToken).ConfigureAwait(false);
        }

        await stream.WriteAsync(Crlf, cancellationToken).ConfigureAwait(false);

        if (!headRequest && buffered is { Length: > 0 })
        {
            await stream.WriteAsync(buffered, cancellationToken).ConfigureAwait(false);
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

        foreach (var (name, values) in response.Headers)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                await WriteHeaderAsync(stream, name, value, cancellationToken).ConfigureAwait(false);
            }
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

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

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

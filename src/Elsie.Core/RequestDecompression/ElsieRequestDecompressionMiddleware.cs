using System.IO.Compression;
using Elsie.Middleware;

namespace Elsie.RequestDecompression;

/// <summary>
/// Thrown by <see cref="ElsieRequestDecompressionMiddleware"/> when a decoded request body exceeds
/// <see cref="ElsieRequestDecompressionOptions.MaxDecompressedBodySize"/> mid-stream. Distinct from
/// <see cref="InvalidOperationException"/> so core binders (which map that to 400) let it reach the
/// middleware, which maps it to <c>413 Payload Too Large</c>.
/// </summary>
public sealed class ElsieRequestDecompressionSizeExceededException : Exception
{
    /// <summary>Create the exception with the configured cap.</summary>
    public ElsieRequestDecompressionSizeExceededException(long maxDecompressedBodySize)
        : base($"Decompressed request body exceeds the limit of {maxDecompressedBodySize} bytes.")
    {
        MaxDecompressedBodySize = maxDecompressedBodySize;
    }

    /// <summary>The configured decompressed body cap that was exceeded.</summary>
    public long MaxDecompressedBodySize { get; }
}

/// <summary>
/// Decodes <c>Content-Encoding: gzip</c> / <c>deflate</c> / <c>br</c> request bodies so downstream
/// handlers and binders see plain (decompressed) bytes. Unsupported codings produce
/// <c>415 Unsupported Media Type</c>; stacked codings (e.g. <c>gzip, br</c>) are decoded in reverse
/// application order. A decompression-bomb cap (<see cref="ElsieRequestDecompressionOptions.MaxDecompressedBodySize"/>,
/// default 10 MiB) fails the request with <c>413 Payload Too Large</c> the moment the decoded stream
/// exceeds it — no full-body buffering. Requests without a <c>Content-Encoding</c> header (or with
/// <c>identity</c>) pass through untouched. Operates on <see cref="ElsieRequest.Body"/> (Core level),
/// so it applies to HTTP/1.1, HTTP/2, and HTTP/3 alike.
///
/// <para>Because decoding is streaming (never buffered), <see cref="ElsieRequest.ContentLength"/>
/// is left unchanged and continues to report the wire (compressed) size the client sent. Downstream
/// handlers and binders must read the decoded stream rather than trust <c>ContentLength</c> for the
/// decoded byte count.</para>
/// </summary>
public sealed class ElsieRequestDecompressionMiddleware : IElsieMiddleware
{
    private static readonly string[] None = [];

    private readonly ElsieRequestDecompressionOptions _options;

    /// <summary>Create the middleware (DI; see <c>AddRequestDecompression</c>).</summary>
    public ElsieRequestDecompressionMiddleware(ElsieRequestDecompressionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.MaxDecompressedBodySize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxDecompressedBodySize must be greater than zero.");
        }
    }

    /// <inheritdoc />
    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var codings = ParseCodings(context.Request.GetHeader("Content-Encoding"));
        if (codings.Length == 0)
        {
            // No coding (or identity) → pass the request through untouched.
            await next(context).ConfigureAwait(false);
            return;
        }

        // Validate every coding up front: an unsupported coding is a 415 without touching the body.
        foreach (var coding in codings)
        {
            if (!IsSupported(coding))
            {
                context.Result = ElsieResult.Problem(
                    415,
                    "Unsupported Media Type",
                    $"Request body Content-Encoding '{coding}' is not supported (supported: gzip, deflate, br).");
                return;
            }
        }

        var originalBody = context.Request.Body;
        var decoders = new List<Stream>(codings.Length + 1);
        try
        {
            // RFC 9110: codings are listed in the order applied, so decoding reverses the order —
            // the last listed coding is the outermost decoder wrapped around the raw body.
            Stream current = originalBody;
            for (var i = codings.Length - 1; i >= 0; i--)
            {
                current = CreateDecompressor(current, codings[i]);
                decoders.Add(current);
            }

            var limited = new DecompressionLimitStream(current, _options.MaxDecompressedBodySize);
            decoders.Add(limited);
            context.Request.ReplaceBody(limited);

            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (ElsieRequestDecompressionSizeExceededException ex)
            {
                context.Result = ElsieResult.Problem(
                    413,
                    "Payload Too Large",
                    ex.Message);
            }
            catch (InvalidDataException ex)
            {
                context.Result = ElsieResult.Problem(
                    400,
                    "Bad Request",
                    $"Malformed compressed request body: {ex.Message}");
            }
        }
        finally
        {
            context.Request.ReplaceBody(originalBody);
            for (var i = decoders.Count - 1; i >= 0; i--)
            {
                decoders[i].Dispose();
            }
        }
    }

    private static string[] ParseCodings(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return None;
        }

        var codings = new List<string>(2);
        foreach (var part in header.Split(','))
        {
            var token = part.Trim();
            if (token.Length == 0 || token.Equals("identity", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            codings.Add(token);
        }

        return codings.ToArray();
    }

    private static bool IsSupported(string coding) =>
        coding.Equals("gzip", StringComparison.OrdinalIgnoreCase)
        || coding.Equals("deflate", StringComparison.OrdinalIgnoreCase)
        || coding.Equals("br", StringComparison.OrdinalIgnoreCase);

    private static Stream CreateDecompressor(Stream inner, string coding)
    {
        if (coding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return new GZipStream(inner, CompressionMode.Decompress, leaveOpen: true);
        }

        if (coding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
        {
            return new DeflateStream(inner, CompressionMode.Decompress, leaveOpen: true);
        }

        return new BrotliStream(inner, CompressionMode.Decompress, leaveOpen: true);
    }

    /// <summary>
    /// Streaming read-only byte counter over a decoded body. Throws
    /// <see cref="ElsieRequestDecompressionSizeExceededException"/> the moment the running total
    /// passes the cap — mid-stream, before the whole body is buffered. Never disposes the inner
    /// stream (lifetime is managed by the middleware so the raw request body survives).
    /// </summary>
    private sealed class DecompressionLimitStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public DecompressionLimitStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            Accumulate(n);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Accumulate(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Accumulate(n);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Intentionally does not dispose _inner: the middleware owns decoder lifetimes and the
            // raw transport body must stay open for the host's post-dispatch drain/dispose.
        }

        private void Accumulate(int n)
        {
            if (n <= 0)
            {
                return;
            }

            _read += n;
            if (_read > _limit)
            {
                throw new ElsieRequestDecompressionSizeExceededException(_limit);
            }
        }
    }
}

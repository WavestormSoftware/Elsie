using System.Globalization;
using Elsie;
using Grpc.Core;

namespace Elsie.Grpc;

/// <summary>
/// <see cref="ServerCallContext"/> subclass that adapts an <see cref="ElsieContext"/> to the
/// gRPC server API: deadline (from the <c>grpc-timeout</c> request header) drives cancellation
/// through <see cref="ElsieRequest.RequestAborted"/>, request metadata maps from HTTP headers,
/// and response trailers (grpc-status / grpc-message) flow through <see cref="ElsieResponse"/>.
/// </summary>
public sealed class ElsieServerCallContext : ServerCallContext
{
    private readonly ElsieContext _context;
    private readonly CancellationTokenSource _deadlineCts;
    private readonly CancellationToken _cancellationToken;
    private readonly Metadata _requestHeaders;
    private readonly Metadata _responseTrailers = [];
    private readonly bool _writeResponseHeaders;
    private readonly DateTime _deadline;
    private Status _status = Status.DefaultSuccess;
    private WriteOptions? _writeOptions;

    internal ElsieServerCallContext(
        ElsieContext context,
        string method,
        string? peer,
        ElsieGrpcOptions options)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _fullMethodName = method;
        _writeResponseHeaders = options.WriteResponseHeaders;
        _requestHeaders = MetadataFromHeaders(context.Request.Headers);

        var timeoutHeader = context.Request.GetHeader("grpc-timeout");
        _deadline = DateTime.MaxValue;
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        _deadlineCts = linkedCts;
        _cancellationToken = linkedCts.Token;
        if (TryParseTimeout(timeoutHeader, out var timeout))
        {
            _deadline = DateTime.UtcNow + timeout;
            linkedCts.CancelAfter(timeout);
        }
    }

    /// <summary>True when the call was cancelled because the deadline elapsed.</summary>
    public bool IsDeadlineExceeded =>
        _deadline != DateTime.MaxValue && DateTime.UtcNow >= _deadline;

    private readonly string _fullMethodName;

    internal string FullMethodName => _fullMethodName;

    internal string? PeerName => PeerCore;

    /// <inheritdoc />
    protected override string MethodCore => _fullMethodName;

    /// <inheritdoc />
    protected override string HostCore => _context.Request.Host ?? string.Empty;

    /// <inheritdoc />
    protected override string PeerCore => _context.Request.RemoteIp ?? string.Empty;

    /// <inheritdoc />
    protected override DateTime DeadlineCore => _deadline;

    /// <inheritdoc />
    protected override Metadata RequestHeadersCore => _requestHeaders;

    /// <inheritdoc />
    protected override CancellationToken CancellationTokenCore => _cancellationToken;

    /// <inheritdoc />
    protected override Metadata ResponseTrailersCore => _responseTrailers;

    /// <inheritdoc />
    protected override Status StatusCore
    {
        get => _status;
        set => _status = value;
    }

    /// <inheritdoc />
    protected override WriteOptions? WriteOptionsCore
    {
        get => _writeOptions;
        set => _writeOptions = value;
    }

    /// <inheritdoc />
    protected override AuthContext AuthContextCore =>
        new(_context.Request.GetHeader("Authorization") ?? string.Empty, new Dictionary<string, List<AuthProperty>>());

    /// <inheritdoc />
    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
        throw new NotSupportedException("Context propagation tokens are not supported by Elsie gRPC.");

    /// <inheritdoc />
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        if (_writeResponseHeaders && responseHeaders is not null)
        {
            foreach (var entry in responseHeaders)
            {
                if (entry.IsBinary)
                {
                    _context.Response.Headers.Add(
                        entry.Key,
                        Convert.ToBase64String(entry.ValueBytes is { Length: > 0 } bytes ? bytes : Array.Empty<byte>()));
                }
                else
                {
                    _context.Response.Headers.Add(entry.Key, entry.Value);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Flushes pending response metadata from ResponseTrailers onto the
    /// HTTP response trailers (grpc-status / grpc-message are added by the transport wrapper).</summary>
    internal void FlushResponseTrailers()
    {
        foreach (var entry in _responseTrailers)
        {
            if (entry.Key.Equals("grpc-status", StringComparison.Ordinal) ||
                entry.Key.Equals("grpc-message", StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.IsBinary)
            {
                var bytes = entry.ValueBytes;
                _context.Response.AddTrailer(entry.Key, Convert.ToBase64String(bytes is null ? [] : bytes));
            }
            else
            {
                _context.Response.AddTrailer(entry.Key, entry.Value);
            }
        }
    }

    internal void Dispose() => _deadlineCts.Dispose();

    private static Metadata MetadataFromHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var metadata = new Metadata();
        foreach (var (key, value) in headers)
        {
            if (key.Length == 0 || key[0] == ':' || key.StartsWith("grpc-", StringComparison.Ordinal))
            {
                continue; // pseudo-headers and grpc-specific headers are handled separately
            }

            if (key.EndsWith("-bin", StringComparison.Ordinal))
            {
                try
                {
                    metadata.Add(key, Convert.FromBase64String(value));
                }
                catch (FormatException)
                {
                    // malformed binary header — skip
                }
            }
            else
            {
                metadata.Add(key, value);
            }
        }

        return metadata;
    }

    /// <summary>Parses a gRPC timeout header value (e.g. "500m", "1S", "2H").</summary>
    internal static bool TryParseTimeout(string? value, out TimeSpan timeout)
    {
        timeout = TimeSpan.Zero;
        if (string.IsNullOrEmpty(value) || value.Length < 2)
        {
            return false;
        }

        var unit = value[^1];
        if (!long.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
            amount <= 0)
        {
            return false;
        }

        timeout = unit switch
        {
            'H' => TimeSpan.FromHours(amount),
            'M' => TimeSpan.FromMinutes(amount),
            'S' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMilliseconds(amount),
            'u' => TimeSpan.FromTicks(amount * 10),
            'n' => TimeSpan.FromTicks(amount / 100),
            _ => TimeSpan.Zero
        };

        return timeout > TimeSpan.Zero;
    }
}

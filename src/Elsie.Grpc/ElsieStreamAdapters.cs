using Grpc.Core;

namespace Elsie.Grpc;

/// <summary>
/// <see cref="IAsyncStreamReader{T}"/> that decodes gRPC-framed request messages from the HTTP
/// request body stream.
/// </summary>
internal sealed class ElsieRequestStreamReader<TRequest> : IAsyncStreamReader<TRequest>
    where TRequest : class
{
    private readonly Stream _body;
    private readonly Func<byte[], TRequest> _deserializer;
    private readonly int _maxMessageSize;
    private readonly ElsieServerCallContext _context;

    public ElsieRequestStreamReader(
        Stream body,
        Func<byte[], TRequest> deserializer,
        int maxMessageSize,
        ElsieServerCallContext context)
    {
        _body = body;
        _deserializer = deserializer;
        _maxMessageSize = maxMessageSize;
        _context = context;
    }

    public TRequest Current { get; private set; } = null!;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        var bytes = await GrpcFraming.ReadMessageAsync(_body, _maxMessageSize, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            Current = null!;
            return false;
        }

        Current = _deserializer(bytes);
        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// <see cref="IServerStreamWriter{T}"/> that encodes gRPC-framed response messages onto the
/// HTTP response body stream.
/// </summary>
internal sealed class ElsieResponseStreamWriter<TResponse> : IServerStreamWriter<TResponse>
    where TResponse : class
{
    private readonly Stream _body;
    private readonly Func<TResponse, byte[]> _serializer;
    private readonly int _maxMessageSize;
    private readonly ElsieServerCallContext _context;

    public ElsieResponseStreamWriter(
        Stream body,
        Func<TResponse, byte[]> serializer,
        int maxMessageSize,
        ElsieServerCallContext context)
    {
        _body = body;
        _serializer = serializer;
        _maxMessageSize = maxMessageSize;
        _context = context;
    }

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(TResponse message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = _serializer(message);
        if (payload.Length > _maxMessageSize)
        {
            throw new RpcException(new Status(
                StatusCode.ResourceExhausted,
                $"Response message of {payload.Length} bytes exceeds the {_maxMessageSize}-byte limit."));
        }

        return GrpcFraming.WriteMessageAsync(_body, payload, _context.CancellationToken);
    }
}

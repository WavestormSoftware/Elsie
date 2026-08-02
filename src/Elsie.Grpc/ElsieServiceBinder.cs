using System.Buffers;
using System.Globalization;
using Elsie;
using Grpc.Core;

namespace Elsie.Grpc;

/// <summary>
/// Describes one registered gRPC method (service + name + marshallers + invoker), collected by
/// <see cref="ElsieServiceBinder"/> from the generated <c>BindService</c> call.
/// </summary>
internal sealed class ElsieGrpcMethod
{
    public required string FullName { get; init; }
    public required MethodType MethodType { get; init; }
    public required Func<object, byte[]> Serialize { get; init; }
    public required Func<byte[], object> Deserialize { get; init; }
    public required Func<ElsieServerCallContext, Stream, Stream, ElsieGrpcOptions, Task<Status>> InvokeAsync { get; init; }

    /// <summary>The gRPC route path, e.g. <c>/package.Service/Method</c>.</summary>
    public string RoutePath => "/" + FullName;
}

/// <summary>
/// <see cref="ServiceBinderBase"/> implementation that maps generated service methods onto Elsie
/// routes. The collected methods are registered by <see cref="ElsieGrpcExtensions.MapGrpcService{TService}"/>.
/// </summary>
public sealed class ElsieServiceBinder : ServiceBinderBase
{
    private readonly List<ElsieGrpcMethod> _methods = [];

    internal IReadOnlyList<ElsieGrpcMethod> Methods => _methods;

    internal void Add(ElsieGrpcMethod method) => _methods.Add(method);
    /// <inheritdoc />
    public override void AddMethod<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        UnaryServerMethod<TRequest, TResponse>? handler)
        where TRequest : class
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        _methods.Add(new ElsieGrpcMethod
        {
            FullName = method.FullName,
            MethodType = method.Type,
            Serialize = o => GrpcMarshaller.Serialize(method.ResponseMarshaller, (TResponse)o),
            Deserialize = b => GrpcMarshaller.Deserialize(method.RequestMarshaller, b),
            InvokeAsync = async (ctx, requestBody, responseBody, options) =>
            {
                try
                {
                    var requestBytes = await GrpcFraming.ReadMessageAsync(
                        requestBody, options.MaxReceiveMessageSize, ctx.CancellationToken).ConfigureAwait(false);
                    if (requestBytes is null)
                    {
                        return new Status(StatusCode.InvalidArgument, "Missing request message.");
                    }

                    var request = GrpcMarshaller.Deserialize(method.RequestMarshaller, requestBytes);
                    var response = await handler(request, ctx).ConfigureAwait(false);
                    if (response is null)
                    {
                        return new Status(StatusCode.Internal, "Handler returned a null response message.");
                    }

                    var payload = GrpcMarshaller.Serialize(method.ResponseMarshaller, response);
                    if (payload.Length > options.MaxSendMessageSize)
                    {
                        return new Status(StatusCode.ResourceExhausted,
                            $"Response message of {payload.Length} bytes exceeds the {options.MaxSendMessageSize}-byte limit.");
                    }

                    await GrpcFraming.WriteMessageAsync(responseBody, payload, ctx.CancellationToken)
                        .ConfigureAwait(false);
                    return ctx.Status;
                }
                catch (RpcException ex)
                {
                    return ex.Status;
                }
                catch (OperationCanceledException) when (ctx.IsDeadlineExceeded)
                {
                    return new Status(StatusCode.DeadlineExceeded, "Deadline exceeded.");
                }
                catch (OperationCanceledException)
                {
                    return new Status(StatusCode.Cancelled, "Call cancelled.");
                }
                catch (GrpcFrameException ex)
                {
                    return new Status(StatusCode.ResourceExhausted, ex.Message);
                }
                catch (Exception ex)
                {
                    return new Status(StatusCode.Internal, ex.Message);
                }
            }
        });
    }

    /// <inheritdoc />
    public override void AddMethod<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        ClientStreamingServerMethod<TRequest, TResponse>? handler)
        where TRequest : class
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        _methods.Add(new ElsieGrpcMethod
        {
            FullName = method.FullName,
            MethodType = method.Type,
            Serialize = o => GrpcMarshaller.Serialize(method.ResponseMarshaller, (TResponse)o),
            Deserialize = b => GrpcMarshaller.Deserialize(method.RequestMarshaller, b),
            InvokeAsync = async (ctx, requestBody, responseBody, options) =>
            {
                try
                {
                    var requestStream = new ElsieRequestStreamReader<TRequest>(
                        requestBody, b => GrpcMarshaller.Deserialize(method.RequestMarshaller, b), options.MaxReceiveMessageSize, ctx);
                    var response = await handler(requestStream, ctx).ConfigureAwait(false);
                    if (response is null)
                    {
                        return new Status(StatusCode.Internal, "Handler returned a null response message.");
                    }

                    var payload = GrpcMarshaller.Serialize(method.ResponseMarshaller, response);
                    await GrpcFraming.WriteMessageAsync(responseBody, payload, ctx.CancellationToken)
                        .ConfigureAwait(false);
                    return ctx.Status;
                }
                catch (RpcException ex)
                {
                    return ex.Status;
                }
                catch (OperationCanceledException) when (ctx.IsDeadlineExceeded)
                {
                    return new Status(StatusCode.DeadlineExceeded, "Deadline exceeded.");
                }
                catch (OperationCanceledException)
                {
                    return new Status(StatusCode.Cancelled, "Call cancelled.");
                }
                catch (GrpcFrameException ex)
                {
                    return new Status(StatusCode.ResourceExhausted, ex.Message);
                }
                catch (Exception ex)
                {
                    return new Status(StatusCode.Internal, ex.Message);
                }
            }
        });
    }

    /// <inheritdoc />
    public override void AddMethod<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        ServerStreamingServerMethod<TRequest, TResponse>? handler)
        where TRequest : class
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        _methods.Add(new ElsieGrpcMethod
        {
            FullName = method.FullName,
            MethodType = method.Type,
            Serialize = o => GrpcMarshaller.Serialize(method.ResponseMarshaller, (TResponse)o),
            Deserialize = b => GrpcMarshaller.Deserialize(method.RequestMarshaller, b),
            InvokeAsync = async (ctx, requestBody, responseBody, options) =>
            {
                try
                {
                    var requestBytes = await GrpcFraming.ReadMessageAsync(
                        requestBody, options.MaxReceiveMessageSize, ctx.CancellationToken).ConfigureAwait(false);
                    if (requestBytes is null)
                    {
                        return new Status(StatusCode.InvalidArgument, "Missing request message.");
                    }

                    var request = GrpcMarshaller.Deserialize(method.RequestMarshaller, requestBytes);
                    var responseStream = new ElsieResponseStreamWriter<TResponse>(
                        responseBody, msg => GrpcMarshaller.Serialize(method.ResponseMarshaller, msg), options.MaxSendMessageSize, ctx);
                    await handler(request, responseStream, ctx).ConfigureAwait(false);
                    return ctx.Status;
                }
                catch (RpcException ex)
                {
                    return ex.Status;
                }
                catch (OperationCanceledException) when (ctx.IsDeadlineExceeded)
                {
                    return new Status(StatusCode.DeadlineExceeded, "Deadline exceeded.");
                }
                catch (OperationCanceledException)
                {
                    return new Status(StatusCode.Cancelled, "Call cancelled.");
                }
                catch (GrpcFrameException ex)
                {
                    return new Status(StatusCode.ResourceExhausted, ex.Message);
                }
                catch (Exception ex)
                {
                    return new Status(StatusCode.Internal, ex.Message);
                }
            }
        });
    }

    /// <inheritdoc />
    public override void AddMethod<TRequest, TResponse>(
        Method<TRequest, TResponse> method,
        DuplexStreamingServerMethod<TRequest, TResponse>? handler)
        where TRequest : class
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(handler);
        _methods.Add(new ElsieGrpcMethod
        {
            FullName = method.FullName,
            MethodType = method.Type,
            Serialize = o => GrpcMarshaller.Serialize(method.ResponseMarshaller, (TResponse)o),
            Deserialize = b => GrpcMarshaller.Deserialize(method.RequestMarshaller, b),
            InvokeAsync = async (ctx, requestBody, responseBody, options) =>
            {
                try
                {
                    var requestStream = new ElsieRequestStreamReader<TRequest>(
                        requestBody, b => GrpcMarshaller.Deserialize(method.RequestMarshaller, b), options.MaxReceiveMessageSize, ctx);
                    var responseStream = new ElsieResponseStreamWriter<TResponse>(
                        responseBody, msg => GrpcMarshaller.Serialize(method.ResponseMarshaller, msg), options.MaxSendMessageSize, ctx);
                    await handler(requestStream, responseStream, ctx).ConfigureAwait(false);
                    return ctx.Status;
                }
                catch (RpcException ex)
                {
                    return ex.Status;
                }
                catch (OperationCanceledException) when (ctx.IsDeadlineExceeded)
                {
                    return new Status(StatusCode.DeadlineExceeded, "Deadline exceeded.");
                }
                catch (OperationCanceledException)
                {
                    return new Status(StatusCode.Cancelled, "Call cancelled.");
                }
                catch (GrpcFrameException ex)
                {
                    return new Status(StatusCode.ResourceExhausted, ex.Message);
                }
                catch (Exception ex)
                {
                    return new Status(StatusCode.Internal, ex.Message);
                }
            }
        });
    }
}

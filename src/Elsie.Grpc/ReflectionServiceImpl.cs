using global::Grpc.Core;
using global::Grpc.Reflection.V1Alpha;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Elsie.Grpc;

/// <summary>
/// Reflection-lite implementation of grpc.reflection.v1alpha.ServerReflection for grpcurl:
/// lists the registered service names and returns their file descriptors when the application
/// registered them via MapGrpcService.
/// </summary>
internal sealed class ReflectionServiceImpl : ServerReflection.ServerReflectionBase
{
    private readonly IReadOnlyDictionary<string, FileDescriptor?> _descriptors;

    public ReflectionServiceImpl(IReadOnlyDictionary<string, FileDescriptor?> descriptors)
    {
        _descriptors = descriptors;
    }

    public override async Task ServerReflectionInfo(
        IAsyncStreamReader<ServerReflectionRequest> requestStream,
        IServerStreamWriter<ServerReflectionResponse> responseStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
        {
            var request = requestStream.Current;
            var response = new ServerReflectionResponse
            {
                ValidHost = request.Host,
                OriginalRequest = request
            };

            switch (request.MessageRequestCase)
            {
                case ServerReflectionRequest.MessageRequestOneofCase.ListServices:
                    {
                        var list = new ListServiceResponse();
                        foreach (var (name, _) in _descriptors)
                        {
                            list.Service.Add(new ServiceResponse { Name = name });
                        }

                        response.ListServicesResponse = list;
                        break;
                    }

                case ServerReflectionRequest.MessageRequestOneofCase.FileByFilename:
                    if (_descriptors.TryGetValue(request.FileByFilename, out var byName) && byName is not null)
                    {
                        response.FileDescriptorResponse = ToDescriptorResponse(byName);
                    }
                    else
                    {
                        response.ErrorResponse = new ErrorResponse
                        {
                            ErrorCode = (int)StatusCode.NotFound,
                            ErrorMessage = $"File not found: {request.FileByFilename}"
                        };
                    }

                    break;

                case ServerReflectionRequest.MessageRequestOneofCase.FileContainingSymbol:
                    {
                        var descriptor = FindDescriptorForSymbol(request.FileContainingSymbol);
                        if (descriptor is not null)
                        {
                            response.FileDescriptorResponse = ToDescriptorResponse(descriptor);
                        }
                        else
                        {
                            response.ErrorResponse = new ErrorResponse
                            {
                                ErrorCode = (int)StatusCode.NotFound,
                                ErrorMessage = $"Symbol not found: {request.FileContainingSymbol}"
                            };
                        }

                        break;
                    }

                default:
                    response.ErrorResponse = new ErrorResponse
                    {
                        ErrorCode = (int)StatusCode.Unimplemented,
                        ErrorMessage = $"Reflection request type '{request.MessageRequestCase}' is not implemented."
                    };
                    break;
            }

            await responseStream.WriteAsync(response).ConfigureAwait(false);
        }
    }

    private FileDescriptor? FindDescriptorForSymbol(string symbol)
    {
        foreach (var descriptor in _descriptors.Values)
        {
            if (descriptor is null)
            {
                continue;
            }

            foreach (var service in descriptor.Services)
            {
                if (service.FullName == symbol || symbol.StartsWith(service.FullName + ".", StringComparison.Ordinal))
                {
                    return descriptor;
                }
            }
        }

        return null;
    }

    private static FileDescriptorResponse ToDescriptorResponse(FileDescriptor descriptor)
    {
        var response = new FileDescriptorResponse();
        response.FileDescriptorProto.Add(descriptor.SerializedData);
        return response;
    }
}

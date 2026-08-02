using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web.Hosting;

/// <summary>Shared request construction for HTTP/1.1 and HTTP/2 paths.</summary>
internal static class ElsieRequestFactory
{
    public static ElsieRequest Create(
        string method,
        string path,
        string queryString,
        IReadOnlyDictionary<string, IReadOnlyList<string>> queryValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headerValues,
        Stream body,
        long? contentLength,
        string? contentType,
        IServiceProvider requestServices,
        CancellationToken requestAborted,
        string? scheme,
        string? host,
        string? protocol,
        string? remoteIp,
        bool useForwardedHeaders)
    {
        string? GetHeader(string name) =>
            headerValues.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

        (scheme, host, remoteIp) = ForwardedHeaders.Apply(
            useForwardedHeaders,
            scheme,
            host,
            remoteIp,
            GetHeader);

        path = PathNormalizer.Canonicalize(path);

        return new ElsieRequest(
            method: method,
            path: path,
            body: body,
            contentLength: contentLength,
            contentType: contentType,
            requestServices: requestServices,
            requestAborted: requestAborted,
            queryValues: queryValues,
            headerValues: headerValues,
            scheme: scheme,
            host: host,
            pathBase: null,
            protocol: protocol,
            remoteIp: remoteIp,
            queryString: queryString);
    }

    public static string? RemoteIpFromEndPoint(EndPoint? remote) =>
        remote switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            _ => null
        };
}

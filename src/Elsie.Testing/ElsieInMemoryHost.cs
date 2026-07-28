using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Testing;

// Uses Elsie.AddElsie / ElsieDispatcher (no ASP.NET).

/// <summary>
/// Host-agnostic in-process Elsie runner (no ASP.NET). Uses <see cref="ElsieDispatcher"/>.
/// </summary>
public sealed class ElsieInMemoryHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;

    private ElsieInMemoryHost(ServiceProvider services, ElsieDispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;
    }

    public static ElsieInMemoryHost Create(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        configure(services);
        var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        return new ElsieInMemoryHost(sp, dispatcher);
    }

    public Task<ElsieInMemoryResponse> GetAsync(string path) =>
        SendAsync("GET", path);

    public Task<ElsieInMemoryResponse> DeleteAsync(string path) =>
        SendAsync("DELETE", path);

    public Task<ElsieInMemoryResponse> PostJsonAsync<T>(string path, T body) =>
        SendJsonAsync("POST", path, body);

    public Task<ElsieInMemoryResponse> PutJsonAsync<T>(string path, T body) =>
        SendJsonAsync("PUT", path, body);

    public async Task<ElsieInMemoryResponse> SendAsync(
        string method,
        string pathAndQuery,
        Stream? body = null,
        long? contentLength = null,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var (path, query) = SplitPathAndQuery(pathAndQuery);
        var request = new ElsieRequest(
            method: method,
            path: path,
            query: query,
            headers: headers,
            body: body,
            contentLength: contentLength ?? body?.Length,
            contentType: contentType,
            requestServices: _services);

        var outcome = await _dispatcher.DispatchAsync(request).ConfigureAwait(false);
        return await ElsieInMemoryResponse.FromDispatchAsync(outcome).ConfigureAwait(false);
    }

    private async Task<ElsieInMemoryResponse> SendJsonAsync<T>(string method, string path, T body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, ElsieJson.DefaultOptions);
        await using var stream = new MemoryStream(bytes);
        return await SendAsync(
            method,
            path,
            body: stream,
            contentLength: bytes.Length,
            contentType: "application/json; charset=utf-8").ConfigureAwait(false);
    }

    private static (string Path, IReadOnlyDictionary<string, string> Query) SplitPathAndQuery(string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        var qIndex = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (qIndex < 0)
        {
            return (pathAndQuery, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var path = pathAndQuery[..qIndex];
        var queryString = pathAndQuery[(qIndex + 1)..];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                query[Uri.UnescapeDataString(part)] = string.Empty;
            }
            else
            {
                var key = Uri.UnescapeDataString(part[..eq]);
                var value = Uri.UnescapeDataString(part[(eq + 1)..]);
                query[key] = value;
            }
        }

        return (path, query);
    }

    public ValueTask DisposeAsync()
    {
        _services.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory dispatch response for core tests.</summary>
public sealed class ElsieInMemoryResponse
{
    private ElsieInMemoryResponse(
        int statusCode,
        string? contentType,
        byte[] body,
        IReadOnlyDictionary<string, string> headers,
        ElsieDispatchStatus dispatchStatus,
        IReadOnlyList<string> allowedMethods)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
        Headers = headers;
        DispatchStatus = dispatchStatus;
        AllowedMethods = allowedMethods;
    }

    public int StatusCode { get; }
    public string? ContentType { get; }
    public byte[] Body { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public ElsieDispatchStatus DispatchStatus { get; }
    public IReadOnlyList<string> AllowedMethods { get; }

    public string ReadAsString() => Encoding.UTF8.GetString(Body);

    internal static async Task<ElsieInMemoryResponse> FromDispatchAsync(ElsieDispatchResult outcome)
    {
        if (outcome.Status == ElsieDispatchStatus.NotFound)
        {
            return new(404, null, Array.Empty<byte>(), EmptyHeaders(), outcome.Status, outcome.AllowedMethods);
        }

        if (outcome.Status == ElsieDispatchStatus.MethodNotAllowed)
        {
            var allow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Allow"] = string.Join(", ", outcome.AllowedMethods)
            };
            return new(405, null, Array.Empty<byte>(), allow, outcome.Status, outcome.AllowedMethods);
        }

        var result = outcome.Result!;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (outcome.Response is not null)
        {
            foreach (var h in outcome.Response.Headers)
            {
                headers[h.Key] = h.Value;
            }
        }

        foreach (var h in result.Headers)
        {
            headers[h.Key] = h.Value;
        }

        byte[] body;
        if (result.BodyWriter is not null)
        {
            await using var ms = new MemoryStream();
            await result.BodyWriter(ms, CancellationToken.None).ConfigureAwait(false);
            body = ms.ToArray();
        }
        else if (result.Body is { } memory)
        {
            body = memory.ToArray();
        }
        else
        {
            body = Array.Empty<byte>();
        }

        return new(result.StatusCode, result.ContentType, body, headers, outcome.Status, outcome.AllowedMethods);
    }

    private static IReadOnlyDictionary<string, string> EmptyHeaders() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

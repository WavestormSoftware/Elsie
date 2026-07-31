using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Testing;

// Uses Elsie.AddElsie / ElsieDispatcher (no sockets).

/// <summary>
/// Host-agnostic in-process Elsie runner (no TCP). Uses <see cref="ElsieDispatcher"/>.
/// Creates an <see cref="IServiceScope"/> per request (ValidateScopes = true).
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
        var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
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
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headerValues = null)
    {
        var (path, queryValues, queryString) = SplitPathAndQuery(pathAndQuery);

        await using var scope = _services.CreateAsyncScope();
        var request = new ElsieRequest(
            method: method,
            path: path,
            body: body,
            contentLength: contentLength ?? body?.Length,
            contentType: contentType,
            requestServices: scope.ServiceProvider,
            queryValues: queryValues,
            headerValues: headerValues ?? Promote(headers),
            queryString: queryString);

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

    private static (
        string Path,
        IReadOnlyDictionary<string, IReadOnlyList<string>> QueryValues,
        string QueryString)
        SplitPathAndQuery(string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        var qIndex = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (qIndex < 0)
        {
            return (
                pathAndQuery,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                string.Empty);
        }

        var path = pathAndQuery[..qIndex];
        var queryString = pathAndQuery[qIndex..]; // includes '?'
        var raw = pathAndQuery[(qIndex + 1)..];
        var multi = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string key;
            string value;
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                key = Uri.UnescapeDataString(part);
                value = string.Empty;
            }
            else
            {
                key = Uri.UnescapeDataString(part[..eq]);
                value = Uri.UnescapeDataString(part[(eq + 1)..]);
            }

            if (!multi.TryGetValue(key, out var list))
            {
                list = [];
                multi[key] = list;
            }

            list.Add(value);
        }

        var queryValues = new Dictionary<string, IReadOnlyList<string>>(multi.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in multi)
        {
            queryValues[key] = list;
        }

        return (path, queryValues, queryString);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? Promote(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, IReadOnlyList<string>>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            map[key] = new[] { value };
        }

        return map;
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
        ElsieHeaders headers,
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
    public ElsieHeaders Headers { get; }
    public ElsieDispatchStatus DispatchStatus { get; }
    public IReadOnlyList<string> AllowedMethods { get; }

    public string ReadAsString() => Encoding.UTF8.GetString(Body);

    internal static async Task<ElsieInMemoryResponse> FromDispatchAsync(ElsieDispatchResult outcome)
    {
        var baked = ElsieHttpResponse.FromDispatch(outcome);
        if (baked is null)
        {
            return new(
                404,
                null,
                Array.Empty<byte>(),
                new ElsieHeaders(),
                outcome.Status,
                outcome.AllowedMethods);
        }

        var body = await baked.BufferBodyAsync().ConfigureAwait(false);
        return new(
            baked.StatusCode,
            baked.ContentType,
            body,
            baked.Headers,
            outcome.Status,
            outcome.AllowedMethods);
    }
}

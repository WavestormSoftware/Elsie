using System.Net;
using System.Net.Http.Json;
using Elsie.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Testing;

/// <summary>
/// Loopback HTTP test host over the real Elsie server (HTTP/1.1).
/// Disables entry-assembly module scan by default; register modules explicitly.
/// </summary>
public sealed class ElsieTestHost : IAsyncDisposable
{
    private readonly ElsieTestServer _server;

    private ElsieTestHost(ElsieTestServer server, HttpClient client)
    {
        _server = server;
        Client = client;
    }

    public HttpClient Client { get; }

    /// <summary>Bound loopback endpoints (raw-socket tests that must inspect wire framing).</summary>
    public IReadOnlyList<System.Net.IPEndPoint> Endpoints => _server.Endpoints;

    public static ElsieTestHost Create(Action<IServiceCollection> configure) =>
        CreateAsync(configure).GetAwaiter().GetResult();

    public static ElsieTestHost Create(
        Action<IServiceCollection> configure,
        Action<ElsieServerOptions>? serverOptions) =>
        CreateAsync(configure, serverOptions).GetAwaiter().GetResult();

    public static async Task<ElsieTestHost> CreateAsync(Action<IServiceCollection> configure) =>
        await CreateAsync(configure, serverOptions: null).ConfigureAwait(false);

    public static async Task<ElsieTestHost> CreateAsync(
        Action<IServiceCollection> configure,
        Action<ElsieServerOptions>? serverOptions)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var app = ElsieApp.Create()
            .QuietConsole(false)
            .Listen(System.Net.IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Services(configure);
        if (serverOptions is not null)
        {
            app = app.Server(serverOptions);
        }

        var server = await app.StartAsync().ConfigureAwait(false);
        return new ElsieTestHost(server, server.CreateClient());
    }

    public Task<HttpResponseMessage> GetAsync(string path) => Client.GetAsync(path);

    public Task<HttpResponseMessage> DeleteAsync(string path) => Client.DeleteAsync(path);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => Client.SendAsync(request);

    public Task<HttpResponseMessage> PostJsonAsync<T>(string path, T body) =>
        Client.PostAsJsonAsync(path, body, ElsieJson.DefaultOptions);

    public Task<HttpResponseMessage> PutJsonAsync<T>(string path, T body) =>
        Client.PutAsJsonAsync(path, body, ElsieJson.DefaultOptions);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

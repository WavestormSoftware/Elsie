using System.Net.Http.Json;
using Elsie.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// AddElsie lives in Elsie core; MapElsie in Elsie.AspNetCore.

namespace Elsie.Testing;

/// <summary>
/// Lightweight in-process host for exercising Elsie modules in tests.
/// Disables entry-assembly module scan by default; register modules explicitly.
/// </summary>
public sealed class ElsieTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private ElsieTestHost(IHost host, HttpClient client)
    {
        _host = host;
        Client = client;
    }

    public HttpClient Client { get; }

    public static ElsieTestHost Create(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddElsie(o => o.ScanEntryAssembly = false);
                    configure(services);
                });
                web.Configure(app => app.MapElsie());
            });

        var host = builder.Start();
        var client = host.GetTestClient();
        return new ElsieTestHost(host, client);
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
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }
}

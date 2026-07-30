using System.Net.Http.Json;
using Elsie.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// AddElsie lives in Elsie core; MapElsie in Elsie.Web.

namespace Elsie.Testing;

/// <summary>
/// Lightweight in-process host for exercising Elsie modules in tests.
/// Disables entry-assembly module scan by default; register modules explicitly.
/// Uses <c>ValidateScopes = true</c>.
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

    public static ElsieTestHost Create(Action<IServiceCollection> configure) =>
        Create(configure, app => app.MapElsie());

    /// <summary>
    /// Create a test host with a custom ASP.NET pipeline (static files, terminal MapElsie, etc.).
    /// </summary>
    public static ElsieTestHost Create(
        Action<IServiceCollection> configure,
        Action<IApplicationBuilder> configureApp)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(configureApp);

        var builder = new HostBuilder()
            .UseDefaultServiceProvider(o =>
            {
                o.ValidateScopes = true;
                o.ValidateOnBuild = true;
            })
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddElsie(o => o.ScanEntryAssembly = false);
                    configure(services);
                });
                web.Configure(configureApp);
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

using Elsie.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elsie.Web;

/// <summary>
/// Microsoft.Extensions.Hosting integration for Elsie (<c>HostApplicationBuilder</c> / Generic Host).
/// </summary>
public static class ElsieHostingExtensions
{
    /// <summary>
    /// Registers Elsie on <paramref name="builder"/> and returns the fluent <see cref="ElsieApp"/> for modules/listen/etc.
    /// Call <c>host.RunAsync()</c> on the built host to start the server.
    /// </summary>
    public static ElsieApp UseElsie(
        this HostApplicationBuilder builder,
        Action<ElsieApp>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = ElsieApp.CreateForHost(builder.Services, builder.Configuration, builder.Environment);
        configure?.Invoke(app);
        app.RegisterWithHost(builder.Services, builder.Configuration, builder.Environment);
        return app;
    }

    /// <summary>
    /// DI registration entrypoint when composing services manually (advanced). Prefer <see cref="UseElsie"/>.
    /// </summary>
    public static IServiceCollection AddElsieApp(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null,
        Action<ElsieApp>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var app = ElsieApp.CreateForHost(services, configuration, environment);
        configure?.Invoke(app);
        app.RegisterWithHost(services, configuration, environment);
        return services;
    }
}

/// <summary>Hosted service that owns the Elsie TCP server lifetime inside Generic Host.</summary>
internal sealed class ElsieHostedService : IHostedService, IAsyncDisposable
{
    private readonly ElsieApp _app;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime? _lifetime;
    private ElsieServer? _server;
    private CancellationTokenRegistration _stoppingReg;

    public ElsieHostedService(
        ElsieApp app,
        IServiceProvider services,
        IHostApplicationLifetime? lifetime = null)
    {
        _app = app;
        _services = services;
        _lifetime = lifetime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _server = _app.BuildServerFromProvider(_services, ownsServices: false);
        if (_lifetime is not null)
        {
            _stoppingReg = _lifetime.ApplicationStopping.Register(() =>
            {
                // StopAsync will also be invoked by the host; this accelerates cancel.
                try { _ = _server.StopAsync(); } catch { /* ignore */ }
            });
        }

        await _server.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            await _server.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stoppingReg.DisposeAsync().ConfigureAwait(false);
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }
}

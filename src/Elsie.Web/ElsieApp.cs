using System.Net;
using Elsie.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web;

/// <summary>
/// Fluent host for Elsie apps. Prefer <see cref="Run{TModule}"/> for the smallest entrypoint.
/// </summary>
public sealed class ElsieApp
{
    private readonly string[] _args;
    private readonly ServiceCollection _services = new();
    private readonly List<Action<IServiceCollection>> _serviceConfigs = new();
    private readonly List<Action<ElsieOptions>> _optionConfigs = new();
    private readonly List<ElsieListenOptions> _listen = new();
    private ElsieStaticFileOptions? _staticFiles;
    private ElsieOpenApiHostOptions? _openApi;
    private Action<ElsieServerFeatures, IServiceProvider>? _featureSetup;
    private string _contentRoot = Directory.GetCurrentDirectory();
    private bool _quietConsole = true;
    private bool _configured;

    private ElsieApp(string[] args)
    {
        _args = args;
    }

    /// <summary>Build, map, and run with a single explicit module.</summary>
    public static void Run<TModule>(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true)
        where TModule : ElsieModule
    {
        Create(args)
            .QuietConsole(quietConsole)
            .Configure(configure ?? (_ => { }))
            .Module<TModule>()
            .Run();
    }

    /// <summary>Build and run using modules discovered via <see cref="ElsieOptions"/> scan.</summary>
    public static void Run(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true)
    {
        Create(args)
            .QuietConsole(quietConsole)
            .Configure(configure ?? (_ => { }))
            .Run();
    }

    /// <summary>Async variant of <see cref="Run{TModule}"/>.</summary>
    public static Task RunAsync<TModule>(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true,
        CancellationToken cancellationToken = default)
        where TModule : ElsieModule
    {
        return Create(args)
            .QuietConsole(quietConsole)
            .Configure(configure ?? (_ => { }))
            .Module<TModule>()
            .RunAsync(cancellationToken);
    }

    /// <summary>Async variant of scan-based Run(args).</summary>
    public static Task RunAsync(
        string[]? args = null,
        Action<ElsieOptions>? configure = null,
        bool quietConsole = true,
        CancellationToken cancellationToken = default)
    {
        return Create(args)
            .QuietConsole(quietConsole)
            .Configure(configure ?? (_ => { }))
            .RunAsync(cancellationToken);
    }

    public static ElsieApp Create(string[]? args = null) => new(args ?? []);

    public ElsieApp QuietConsole(bool quiet = true)
    {
        _quietConsole = quiet;
        return this;
    }

    public ElsieApp ContentRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _contentRoot = Path.GetFullPath(path);
        return this;
    }

    public ElsieApp Module<TModule>() where TModule : ElsieModule
    {
        _serviceConfigs.Add(s => s.AddElsieModule<TModule>());
        return this;
    }

    public ElsieApp Services(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceConfigs.Add(configure);
        return this;
    }

    public ElsieApp Configure(Action<ElsieOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _optionConfigs.Add(configure);
        return this;
    }

    public ElsieApp Listen(string url)
    {
        _listen.Add(ElsieListenOptions.Parse(url));
        return this;
    }

    public ElsieApp Listen(string url, Action<ElsieListenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = ElsieListenOptions.Parse(url);
        configure(options);
        _listen.Add(options);
        return this;
    }

    public ElsieApp Listen(IPAddress address, int port, Action<ElsieListenOptions>? configure = null)
    {
        var options = new ElsieListenOptions { Address = address, Port = port };
        configure?.Invoke(options);
        _listen.Add(options);
        return this;
    }

    public ElsieApp StaticFiles(Action<ElsieStaticFileOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElsieStaticFileOptions { ContentRoot = _contentRoot };
        configure(options);
        _staticFiles = options;
        return this;
    }

    public ElsieApp OpenApi(Action<ElsieOpenApiHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElsieOpenApiHostOptions();
        configure(options);
        _openApi = options;
        return this;
    }

    /// <summary>Extension hook for packages (Auth, CORS, Views) to wire host features and DI.</summary>
    public ElsieApp Use(Action<ElsieApp, IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceConfigs.Add(s => configure(this, s));
        return this;
    }

    internal void SetFeatureSetup(Action<ElsieServerFeatures, IServiceProvider> setup)
    {
        var prior = _featureSetup;
        _featureSetup = prior is null
            ? setup
            : (f, sp) =>
            {
                prior(f, sp);
                setup(f, sp);
            };
    }

    internal void RegisterFeatureSetup(Action<ElsieServerFeatures, IServiceProvider> setup) =>
        SetFeatureSetup(setup);

    public void Run()
    {
        RunHostAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        RunHostAsync(cancellationToken);

    private async Task RunHostAsync(CancellationToken cancellationToken)
    {
        await using var host = BuildServer();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            linked.Cancel();
        };

        await host.RunAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>Build a runnable server without blocking (tests).</summary>
    public async Task<ElsieTestServer> StartAsync(CancellationToken cancellationToken = default)
    {
        var server = BuildServer();
        await server.StartAsync(cancellationToken).ConfigureAwait(false);
        return new ElsieTestServer(server);
    }

    private ElsieServer BuildServer()
    {
        EnsureConfigured();
        var endpoints = _listen.Count > 0
            ? _listen
            : new List<ElsieListenOptions> { ElsieListenOptions.Parse("http://127.0.0.1:5000") };

        _services.AddElsie(o =>
        {
            foreach (var cfg in _optionConfigs)
            {
                cfg(o);
            }
        });

        foreach (var cfg in _serviceConfigs)
        {
            cfg(_services);
        }

        var sp = _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        var features = new ElsieServerFeatures
        {
            StaticFiles = _staticFiles,
            OpenApi = _openApi,
            ContentRoot = _contentRoot
        };
        _featureSetup?.Invoke(features, sp);

        Action<string>? log = _quietConsole
            ? msg => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}")
            : null;

        return new ElsieServer(sp, dispatcher, features, endpoints, log);
    }

    private void EnsureConfigured()
    {
        if (_configured)
        {
            return;
        }

        _configured = true;
        // Parse simple --urls from args if present and no Listen calls
        if (_listen.Count == 0)
        {
            for (var i = 0; i < _args.Length; i++)
            {
                if (_args[i] is "--urls" or "--url" && i + 1 < _args.Length)
                {
                    foreach (var url in _args[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        _listen.Add(ElsieListenOptions.Parse(url));
                    }
                }
                else if (_args[i].StartsWith("--urls=", StringComparison.Ordinal))
                {
                    var value = _args[i]["--urls=".Length..];
                    foreach (var url in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        _listen.Add(ElsieListenOptions.Parse(url));
                    }
                }
            }
        }
    }
}

/// <summary>Running server handle for tests (loopback HttpClient).</summary>
public sealed class ElsieTestServer : IAsyncDisposable
{
    private readonly ElsieServer _server;
    private HttpClient? _client;

    internal ElsieTestServer(ElsieServer server)
    {
        _server = server;
    }

    public IReadOnlyList<IPEndPoint> Endpoints => _server.BoundEndpoints;

    public HttpClient CreateClient()
    {
        if (_client is not null)
        {
            return _client;
        }

        var ep = _server.BoundEndpoints[0];
        var host = ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any)
            ? "127.0.0.1"
            : ep.Address.ToString();
        if (ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            !ep.Address.Equals(IPAddress.IPv6Any))
        {
            host = $"[{ep.Address}]";
        }

        _client = new HttpClient(new HttpClientHandler { UseCookies = true })
        {
            BaseAddress = new Uri($"http://{host}:{ep.Port}/")
        };
        return _client;
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

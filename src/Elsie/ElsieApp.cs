using System.Net;
using System.Net.Sockets;
using Elsie.Web.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsie.Web;

/// <summary>
/// Fluent host for Elsie apps. Prefer <see cref="Run{TModule}"/> for the smallest entrypoint.
/// For Generic Host, use <see cref="ElsieHostingExtensions.UseElsie"/>.
/// </summary>
public sealed class ElsieApp
{
    private readonly string[] _args;
    private readonly IServiceCollection _services;
    private readonly bool _ownsServiceCollection;
    private readonly List<Action<IServiceCollection>> _serviceConfigs = new();
    private readonly List<Action<ElsieOptions>> _optionConfigs = new();
    private readonly List<ElsieListenOptions> _listen = new();
    private ElsieStaticFileOptions? _staticFiles;
    private ElsieOpenApiHostOptions? _openApi;
    private ElsieServerOptions _serverOptions = new();
    private Action<ElsieServerFeatures, IServiceProvider>? _featureSetup;
    private string _contentRoot = Directory.GetCurrentDirectory();
    private bool _quietConsole = true;
    private bool _configured;
    private bool _hostRegistered;
    private bool _configurationApplied;
    private ILoggerFactory? _loggerFactory;
    private IConfiguration? _configuration;
    private IHostEnvironment? _environment;

    private ElsieApp(string[] args, IServiceCollection? externalServices = null)
    {
        _args = args;
        if (externalServices is null)
        {
            _services = new ServiceCollection();
            _ownsServiceCollection = true;
        }
        else
        {
            _services = externalServices;
            _ownsServiceCollection = false;
        }
    }

    /// <summary>Create an app bound to an external DI container (Generic Host).</summary>
    internal static ElsieApp CreateForHost(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var app = new ElsieApp(Array.Empty<string>(), services)
        {
            _configuration = configuration,
            _environment = environment
        };
        return app;
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

    /// <summary>
    /// Bind <see cref="ElsieOptions"/> from configuration (e.g. the <c>Elsie</c> section).
    /// </summary>
    public ElsieApp Configure(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _optionConfigs.Add(o => configuration.Bind(o));
        return this;
    }

    /// <summary>Register a background <see cref="IHostedService"/> with the app DI container.</summary>
    public ElsieApp HostedService<TService>() where TService : class, IHostedService
    {
        _serviceConfigs.Add(s => s.AddHostedService<TService>());
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

    /// <summary>Configure server limits (header/body sizes, H2 concurrency, timeouts).</summary>
    public ElsieApp Server(Action<ElsieServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_serverOptions);
        return this;
    }

    /// <summary>Enable gzip/brotli response compression for compressible buffered bodies.</summary>
    public ElsieApp Compression(bool enable = true, int minBodyBytes = 1024)
    {
        _serverOptions.EnableResponseCompression = enable;
        _serverOptions.CompressionMinBodyBytes = minBodyBytes;
        return this;
    }

    /// <summary>Optional Microsoft.Extensions.Logging factory for host diagnostics.</summary>
    public ElsieApp Logging(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
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

    /// <summary>Wire DI + hosted service into a Generic Host container (called by <see cref="ElsieHostingExtensions"/>).</summary>
    internal void RegisterWithHost(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment)
    {
        if (_hostRegistered)
        {
            return;
        }

        _hostRegistered = true;
        _configuration = configuration;
        _environment = environment;
        ApplyConfigurationDefaults();

        services.AddSingleton(this);
        services.AddElsie(o =>
        {
            if (environment?.IsDevelopment() == true)
            {
                o.ShowExceptionDetails = true;
            }

            foreach (var cfg in _optionConfigs)
            {
                cfg(o);
            }
        });

        foreach (var cfg in _serviceConfigs)
        {
            cfg(services);
        }

        services.AddSingleton<ElsieServerFeatures>(sp =>
        {
            var features = new ElsieServerFeatures
            {
                StaticFiles = _staticFiles,
                OpenApi = _openApi,
                ContentRoot = _contentRoot
            };
            _featureSetup?.Invoke(features, sp);
            var routes = sp.GetRequiredService<Routing.RouteTable>();
            features.WarmOpenApi(routes);
            return features;
        });

        services.AddHostedService<ElsieHostedService>();
    }

    /// <summary>Build server from an already-built root <see cref="IServiceProvider"/> (Generic Host).</summary>
    internal ElsieServer BuildServerFromProvider(IServiceProvider sp, bool ownsServices)
    {
        EnsureConfigured();
        var endpoints = ResolveEndpoints();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        var features = sp.GetRequiredService<ElsieServerFeatures>();

        Action<string>? log = _quietConsole
            ? msg => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}")
            : null;

        var loggerFactory = _loggerFactory
            ?? sp.GetService<ILoggerFactory>()
            ?? NullLoggerFactory.Instance;

        return new ElsieServer(
            sp,
            dispatcher,
            features,
            endpoints,
            _serverOptions,
            log,
            loggerFactory,
            ownsServices);
    }

    private ElsieServer BuildServer()
    {
        EnsureConfigured();
        ApplyConfigurationDefaults();
        var endpoints = ResolveEndpoints();

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

        if (!_ownsServiceCollection)
        {
            throw new InvalidOperationException(
                "Cannot BuildServer() on a host-bound ElsieApp. Use Generic Host RunAsync instead.");
        }

        var sp = ((ServiceCollection)_services).BuildServiceProvider(new ServiceProviderOptions
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

        var loggerFactory = _loggerFactory ?? NullLoggerFactory.Instance;
        return new ElsieServer(sp, dispatcher, features, endpoints, _serverOptions, log, loggerFactory, ownsServices: true);
    }

    private List<ElsieListenOptions> ResolveEndpoints()
    {
        if (_listen.Count > 0)
        {
            return _listen;
        }

        return new List<ElsieListenOptions> { ElsieListenOptions.Parse("http://127.0.0.1:5000") };
    }

    private void ApplyConfigurationDefaults()
    {
        if (_configurationApplied || _configuration is null)
        {
            return;
        }

        _configurationApplied = true;

        // Bind ElsieOptions section once if present.
        var section = _configuration.GetSection("Elsie");
        if (section.Exists())
        {
            _optionConfigs.Insert(0, o => section.Bind(o));
        }

        // Listen URLs: Elsie:Urls or urls (host-style).
        if (_listen.Count == 0)
        {
            var urls = _configuration["Elsie:Urls"] ?? _configuration["urls"];
            if (!string.IsNullOrWhiteSpace(urls))
            {
                foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    _listen.Add(ElsieListenOptions.Parse(url));
                }
            }
        }
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

    /// <summary>Unix domain socket paths the server is listening on (if any).</summary>
    public IReadOnlyList<string> UnixSocketPaths => _server.BoundUnixSocketPaths;

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
        if (ep.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !ep.Address.Equals(IPAddress.IPv6Any))
        {
            host = $"[{ep.Address}]";
        }

        // Prefer https when the bound endpoint came from an https Listen URL.
        // ElsieServer only exposes IPEndPoint — detect via ServerCertificateCustomValidationCallback always-allow for loopback tests.
        var scheme = "http";
        var handler = new HttpClientHandler { UseCookies = true };
        _client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"{scheme}://{host}:{ep.Port}/")
        };
        return _client;
    }

    /// <summary>Create a client for HTTPS loopback tests (accepts any server certificate).</summary>
    public HttpClient CreateHttpsClient()
    {
        var ep = _server.BoundEndpoints[0];
        var host = ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any)
            ? "127.0.0.1"
            : ep.Address.ToString();
        if (ep.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !ep.Address.Equals(IPAddress.IPv6Any))
        {
            host = $"[{ep.Address}]";
        }

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{host}:{ep.Port}/")
        };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}

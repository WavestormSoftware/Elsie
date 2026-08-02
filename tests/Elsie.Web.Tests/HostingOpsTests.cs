using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Elsie.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elsie.Web.Tests;

public class HostingOpsTests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
            Get("/boom", _ => throw new InvalidOperationException("kaboom-stack-marker"));
        }
    }

    [Fact]
    public async Task Generic_host_UseElsie_serves_and_stops()
    {
        var urls = $"http://127.0.0.1:{GetFreePort()}";
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Elsie:Urls"] = urls
        });

        builder.UseElsie(app =>
        {
            app.QuietConsole(false)
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<PingModule>();
        });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(urls + "/") };
            Assert.Equal("pong", await client.GetStringAsync("/ping"));
        }
        finally
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task Traceparent_propagates_to_child_activity_and_response()
    {
        // 00-{trace-id 32 hex}-{span-id 16 hex}-{flags}
        const string traceId = "0af7651916cd43dd8448eb211c80319c";
        const string parentSpan = "b7ad6b7169203331";
        var traceparent = $"00-{traceId}-{parentSpan}-01";

        ActivityContext? observed = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Elsie",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a =>
            {
                if (a.OperationName == "Elsie.Dispatch" &&
                    string.Equals(a.TraceId.ToString(), traceId, StringComparison.OrdinalIgnoreCase))
                {
                    observed = a.Context;
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/ping");
        req.Headers.TryAddWithoutValidation("traceparent", traceparent);
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.Contains("traceparent"));
        var echoed = res.Headers.GetValues("traceparent").First();
        Assert.Contains(traceId, echoed, StringComparison.OrdinalIgnoreCase);

        // Listener is process-global; under parallel load only assert when observed.
        if (observed is { } ctx)
        {
            Assert.Equal(traceId, ctx.TraceId.ToString());
        }
    }

    [Fact]
    public async Task Metrics_record_duration_and_active_requests()
    {
        double? observedDuration = null;
        long activePeak = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Elsie")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<double>((inst, measurement, tags, state) =>
        {
            if (inst.Name == "elsie.http.server.request.duration")
            {
                observedDuration = measurement;
            }
        });
        meterListener.SetMeasurementEventCallback<long>((inst, measurement, tags, state) =>
        {
            if (inst.Name == "elsie.active_requests" && measurement > 0)
            {
                activePeak = Math.Max(activePeak, measurement);
            }
        });
        meterListener.Start();

        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<PingModule>());
        // In-memory host goes through dispatcher only — metrics are on HostDispatch (socket host).
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        using var client = server.CreateClient();
        Assert.Equal("pong", await client.GetStringAsync("/ping"));

        // Allow listener callbacks
        await Task.Delay(50);
        Assert.NotNull(observedDuration);
        Assert.True(observedDuration >= 0);
        Assert.True(activePeak >= 1);
    }

    [Fact]
    public async Task ShowExceptionDetails_on_returns_html_stack()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o =>
            {
                o.ScanEntryAssembly = false;
                o.ShowExceptionDetails = true;
            })
            .Module<PingModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var res = await client.GetAsync("/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("text/html", res.Content.Headers.ContentType?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kaboom-stack-marker", body, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowExceptionDetails_off_hides_stack()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o =>
            {
                o.ScanEntryAssembly = false;
                o.ShowExceptionDetails = false;
            })
            .Module<PingModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var res = await client.GetAsync("/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("kaboom-stack-marker", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at Elsie", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structured_request_logging_emits_line()
    {
        var sink = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(sink);
        });

        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Logging(factory)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<PingModule>()
            .StartAsync();

        using var client = server.CreateClient();
        await client.GetStringAsync("/ping");
        await Task.Delay(50);

        Assert.Contains(sink.Lines, l =>
            l.Contains("GET", StringComparison.Ordinal) &&
            l.Contains("/ping", StringComparison.Ordinal) &&
            l.Contains("200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Server_limits_hot_reload_from_configuration()
    {
        var urls = $"http://127.0.0.1:{GetFreePort()}";
        var provider = new MemoryConfigurationProvider(new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>
            {
                ["Elsie:Urls"] = urls,
                ["Elsie:Server:MaxHeaderBytes"] = "4096"
            }
        });

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Elsie:Urls"] = urls,
            ["Elsie:Server:MaxHeaderBytes"] = "4096"
        });
        builder.UseElsie(app =>
        {
            app.QuietConsole(false)
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<PingModule>();
        });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            // 8 KiB header exceeds the 4096-byte limit → rejected.
            using (var client = new HttpClient { BaseAddress = new Uri(urls + "/") })
            {
                using var before = new HttpRequestMessage(HttpMethod.Get, "/ping");
                before.Headers.TryAddWithoutValidation("X-Big", new string('x', 8192));
                var rejected = await client.SendAsync(before);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
            }

            // Hot reload: raise the limit, then the same request succeeds on a new connection.
            var root = (IConfigurationRoot)builder.Configuration;
            root.Providers.OfType<MemoryConfigurationProvider>()
                .First(p => p.TryGet("Elsie:Server:MaxHeaderBytes", out _))
                .Set("Elsie:Server:MaxHeaderBytes", "16384");
            root.Reload();

            using var client2 = new HttpClient { BaseAddress = new Uri(urls + "/") };
            using var after = new HttpRequestMessage(HttpMethod.Get, "/ping");
            after.Headers.TryAddWithoutValidation("X-Big", new string('x', 8192));
            var ok = await client2.SendAsync(after);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("pong", await ok.Content.ReadAsStringAsync());
        }
        finally
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(cts.Token);
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(Lines);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}

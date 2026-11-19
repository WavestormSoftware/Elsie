using Elsie.Endpoints;
using Elsie.Jobs;
using Elsie.Localization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class BackgroundAndLocalizationTests
{
    private sealed class ScopedCounter
    {
        public int Value;
    }

    [Fact]
    public async Task Queue_processes_items_in_a_fresh_scope()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedCounter>();
        await using var sp = services.BuildServiceProvider();
        var queue = new ElsieBackgroundQueue(sp);

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await queue.EnqueueAsync(async (providerScope, ct) =>
        {
            var counter = providerScope.GetRequiredService<ScopedCounter>();
            counter.Value++;
            await Task.Delay(1, ct);
            done.TrySetResult(counter.Value == 1);
        });
        Assert.True(first);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = queue.RunAsync(cts.Token);
        var ok = await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Complete();
        await run;

        Assert.True(ok);
        Assert.Equal(1, queue.Processed);
        Assert.Equal(0, queue.Failed);
    }

    [Fact]
    public async Task Failing_items_are_counted_and_do_not_stop_the_worker()
    {
        var services = new ServiceCollection();
        await using var sp = services.BuildServiceProvider();
        var queue = new ElsieBackgroundQueue(sp);

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.EnqueueAsync((_, _) => throw new InvalidOperationException("boom"));
        await queue.EnqueueAsync((_, _) =>
        {
            completed.TrySetResult();
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = queue.RunAsync(cts.Token);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Complete();
        await run;

        Assert.Equal(1, queue.Failed);
        Assert.Equal(1, queue.Processed);
    }

    [Fact]
    public async Task Enqueue_fails_when_the_queue_is_full_and_cancelled()
    {
        var services = new ServiceCollection();
        await using var sp = services.BuildServiceProvider();
        var queue = new ElsieBackgroundQueue(sp, new ElsieBackgroundQueueOptions { Capacity = 1 });

        Assert.True(await queue.EnqueueAsync((_, _) => Task.CompletedTask));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.False(await queue.EnqueueAsync((_, _) => Task.CompletedTask, cts.Token));
        Assert.Equal(1, queue.Count);
    }

    // ---- Localization --------------------------------------------------------------

    private static ServiceProvider BuildLocalized(Action<IElsieEndpointRouteBuilder> map, Action<ElsieLocalizationOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddElsieLocalization(o =>
        {
            o.DefaultCulture = "en";
            o.SupportedCultures.AddRange(["en", "fr", "de"]);
            o.Resources["fr"] = new Dictionary<string, string> { ["hello"] = "bonjour" };
            o.Resources["de"] = new Dictionary<string, string> { ["hello"] = "hallo", ["bye"] = "tschüss" };
            configure?.Invoke(o);
        });
        services.AddElsieEndpoints(map, o => o.ScanEntryAssembly = false);
        return services.BuildServiceProvider();
    }

    private static ElsieRequest LocalizedRequest(
        IServiceProvider services,
        string path,
        string? acceptLanguage = null,
        string? query = null)
    {
        var headers = new Dictionary<string, string>();
        if (acceptLanguage is not null)
        {
            headers["Accept-Language"] = acceptLanguage;
        }

        var queryValues = query is null ? null : new Dictionary<string, string> { ["culture"] = query };
        return new ElsieRequest("GET", path, headers: headers, query: queryValues, requestServices: services);
    }

    [Fact]
    public async Task Accept_language_picks_the_best_supported_culture()
    {
        await using var sp = BuildLocalized(app => app.MapGet("/x", (ElsieContext ctx) => ctx.GetCulture().Name));

        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        var outcome = await dispatcher.DispatchAsync(LocalizedRequest(sp, "/x", "de;q=0.4, fr;q=0.9"));

        Assert.Equal("fr", System.Text.Encoding.UTF8.GetString(outcome.Result!.Body!.Value.Span));
        Assert.Equal("fr", outcome.Result.Headers["Content-Language"]);
        Assert.Equal("Accept-Language", outcome.Result.Headers["Vary"]);
    }

    [Fact]
    public async Task Query_override_wins_and_unsupported_falls_back()
    {
        await using var sp = BuildLocalized(app => app.MapGet("/x", (ElsieContext ctx) => ctx.GetCulture().Name));
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var overridden = await dispatcher.DispatchAsync(LocalizedRequest(sp, "/x", "fr", query: "de"));
        var unsupported = await dispatcher.DispatchAsync(LocalizedRequest(sp, "/x", "ja"));

        Assert.Equal("de", System.Text.Encoding.UTF8.GetString(overridden.Result!.Body!.Value.Span));
        Assert.Equal("en", System.Text.Encoding.UTF8.GetString(unsupported.Result!.Body!.Value.Span));
    }

    [Fact]
    public async Task Translations_fall_back_from_key_to_culture_to_default()
    {
        await using var sp = BuildLocalized(app => app.MapGet("/x", (ElsieContext ctx) =>
            $"{ctx.T("hello")}|{ctx.T("bye")}|{ctx.T("missing")}"));
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var french = await dispatcher.DispatchAsync(LocalizedRequest(sp, "/x", "fr"));
        var german = await dispatcher.DispatchAsync(LocalizedRequest(sp, "/x", "de"));
        var english = await dispatcher.DispatchAsync(LocalizedRequest(sp, "/x", "en"));

        Assert.Equal("bonjour|bye|missing", System.Text.Encoding.UTF8.GetString(french.Result!.Body!.Value.Span));
        Assert.Equal("hallo|tschüss|missing", System.Text.Encoding.UTF8.GetString(german.Result!.Body!.Value.Span));
        Assert.Equal("hello|bye|missing", System.Text.Encoding.UTF8.GetString(english.Result!.Body!.Value.Span));
    }

    [Fact]
    public async Task Set_culture_overrides_for_the_rest_of_the_request()
    {
        await using var sp = BuildLocalized(app => app.MapGet("/x", (ElsieContext ctx) =>
        {
            ctx.SetCulture(System.Globalization.CultureInfo.GetCultureInfo("de"));
            return ctx.T("bye");
        }));
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(LocalizedRequest(sp, "/x", "fr"));

        Assert.Equal("tschüss", System.Text.Encoding.UTF8.GetString(outcome.Result!.Body!.Value.Span));
    }
}

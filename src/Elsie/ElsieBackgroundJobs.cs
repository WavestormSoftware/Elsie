using Elsie.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elsie.Web;

/// <summary>Host wiring for <see cref="ElsieBackgroundQueue"/>.</summary>
public static class ElsieBackgroundJobExtensions
{
    /// <summary>
    /// Register the background queue and the hosted workers that drain it. Each work item runs in
    /// its own DI scope.
    /// </summary>
    public static IServiceCollection AddElsieBackgroundJobs(
        this IServiceCollection services,
        Action<ElsieBackgroundQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieBackgroundQueueOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new ElsieBackgroundQueue(
            sp,
            sp.GetRequiredService<ElsieBackgroundQueueOptions>()));
        services.TryAddSingleton<IElsieBackgroundQueue>(sp => sp.GetRequiredService<ElsieBackgroundQueue>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ElsieBackgroundWorkerService>());
        return services;
    }

    /// <summary>Fluent shorthand for <c>Services(s =&gt; s.AddElsieBackgroundJobs(...))</c>.</summary>
    public static ElsieApp BackgroundJobs(
        this ElsieApp app,
        Action<ElsieBackgroundQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services(s => s.AddElsieBackgroundJobs(configure));
    }
}

/// <summary>Runs the configured number of queue workers for the lifetime of the host.</summary>
public sealed class ElsieBackgroundWorkerService : IHostedService
{
    private readonly ElsieBackgroundQueue _queue;
    private readonly ElsieBackgroundQueueOptions _options;
    private readonly List<Task> _workers = [];
    private CancellationTokenSource? _stopping;

    public ElsieBackgroundWorkerService(ElsieBackgroundQueue queue, ElsieBackgroundQueueOptions options)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        for (var i = 0; i < Math.Max(1, _options.WorkerCount); i++)
        {
            _workers.Add(Task.Run(() => _queue.RunAsync(_stopping.Token), CancellationToken.None));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();
        try
        {
            await Task.WhenAll(_workers).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _stopping?.Cancel();
        }
        finally
        {
            _workers.Clear();
            _stopping?.Dispose();
            _stopping = null;
        }
    }
}

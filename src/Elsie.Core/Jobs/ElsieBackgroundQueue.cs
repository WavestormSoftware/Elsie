using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Jobs;

/// <summary>Options for <see cref="ElsieBackgroundQueue"/>.</summary>
public sealed class ElsieBackgroundQueueOptions
{
    /// <summary>Maximum queued work items (default 1024). Enqueue fails when full.</summary>
    public int Capacity { get; set; } = 1024;

    /// <summary>Concurrent workers draining the queue (default 1).</summary>
    public int WorkerCount { get; set; } = 1;
}

/// <summary>
/// Fire-and-forget work queue. Each item runs in its own DI scope so scoped services behave like
/// they do in a request.
/// </summary>
public interface IElsieBackgroundQueue
{
    /// <summary>Queue work. Returns false when the queue is full or shutting down.</summary>
    ValueTask<bool> EnqueueAsync(
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);

    /// <summary>Items waiting to be processed.</summary>
    int Count { get; }

    /// <summary>Items completed successfully.</summary>
    long Processed { get; }

    /// <summary>Items that threw.</summary>
    long Failed { get; }
}

/// <summary>Bounded channel-backed background queue.</summary>
public sealed class ElsieBackgroundQueue : IElsieBackgroundQueue
{
    private readonly Channel<WorkItem> _channel;
    private readonly IServiceProvider _services;
    private readonly ElsieBackgroundQueueOptions _options;
    private long _processed;
    private long _failed;

    public ElsieBackgroundQueue(IServiceProvider services, ElsieBackgroundQueueOptions? options = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? new ElsieBackgroundQueueOptions();
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(Math.Max(1, _options.Capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _options.WorkerCount == 1,
            SingleWriter = false
        });
    }

    /// <inheritdoc />
    public int Count => _channel.Reader.Count;

    /// <inheritdoc />
    public long Processed => Interlocked.Read(ref _processed);

    /// <inheritdoc />
    public long Failed => Interlocked.Read(ref _failed);

    /// <inheritdoc />
    public ValueTask<bool> EnqueueAsync(
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_channel.Writer.TryWrite(new WorkItem(work)))
        {
            return ValueTask.FromResult(true);
        }

        return WriteSlowAsync(work, cancellationToken);
    }

    /// <summary>Drain the queue until <paramref name="stoppingToken"/> fires or <see cref="Complete"/> is called.</summary>
    public async Task RunAsync(CancellationToken stoppingToken = default)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ExecuteAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>Stop accepting work; running readers drain remaining items and finish.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    private async ValueTask<bool> WriteSlowAsync(
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(new WorkItem(work), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ExecuteAsync(WorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();
            await item.Work(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _processed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
        }
        catch
        {
            // A failing job must not kill the worker loop; counts are exposed for monitoring.
            Interlocked.Increment(ref _failed);
        }
    }

    private readonly record struct WorkItem(Func<IServiceProvider, CancellationToken, Task> Work);
}

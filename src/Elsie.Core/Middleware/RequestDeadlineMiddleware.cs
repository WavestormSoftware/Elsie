using Elsie.Middleware;

namespace Elsie.Middleware;

/// <summary>
/// Opt-in per-request deadline middleware. When enabled via <c>UseRequestDeadline</c>, each
/// request is aborted with <c>408 Request Timeout</c> if its handler exceeds the configured
/// <see cref="ElsieRequestDeadlineOptions.Deadline"/>. The deadline is linked into the handler's
/// dispatch cancellation token (so the handler observes <see cref="ElsieRequest.RequestAborted"/>
/// cancellation), and a <c>408</c> is produced only for a non-terminal outcome. Upgrades
/// (WebSocket/<see cref="ElsieWebSocket"/>) and streaming <see cref="ElsieResult.BodyWriter"/>
/// responses (SSE, large files) are exempt: because their handler returns a terminal result
/// immediately and the actual streaming runs on the transport after the pipeline, the deadline
/// must not cancel them.
/// </summary>
public sealed class RequestDeadlineMiddleware : IElsieMiddleware
{
    private readonly ElsieRequestDeadlineOptions _options;

    /// <summary>Create the middleware (DI; see <c>AddRequestDeadline</c>).</summary>
    public RequestDeadlineMiddleware(ElsieRequestDeadlineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var deadline = _options.Deadline;
        if (deadline <= TimeSpan.Zero)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(deadline);

        // Swap in a deadline-linked token so the handler's dispatch token observes cancellation
        // when the deadline fires. The terminal reads DispatchCancellationToken at invocation time.
        var original = context.DispatchCancellationToken;
        context.DispatchCancellationToken = cts.Token;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The deadline fired while the handler was running — it was cancelled before
            // producing a response. Only materialize a 408 if the result is not an
            // upgrade/streaming (which would mean the handler already bound a terminal result).
            if (!IsUpgradeOrStreaming(context.Result))
            {
                context.Result = ElsieResult.Problem(408, "Request Timeout", "The request handler exceeded the configured deadline.");
            }

            return;
        }
        finally
        {
            context.DispatchCancellationToken = original;
        }

        // The deadline may have fired just as the handler completed (handler did not observe the
        // token). A slow handler that exceeded the deadline should still be a 408 — unless it
        // produced an upgrade/streaming (WebSocket / SSE) result, which is exempt.
        if (cts.IsCancellationRequested && !IsUpgradeOrStreaming(context.Result))
        {
            context.Result = ElsieResult.Problem(408, "Request Timeout", "The request handler exceeded the configured deadline.");
        }
    }

    /// <summary>Upgrades (WebSocket) and streaming (BodyWriter) responses are exempt from the 408.</summary>
    private static bool IsUpgradeOrStreaming(ElsieResult? result)
    {
        if (result is null)
        {
            return false;
        }

        return result.WebSocketHandler is not null || result.BodyWriter is not null;
    }
}

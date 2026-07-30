namespace Elsie.Pipelines;

/// <summary>
/// Ordered before/after hooks for application or module scope.
/// </summary>
public sealed class ElsiePipelines
{
    private readonly List<ElsieBeforeDelegate> _before = [];
    private readonly List<ElsieAfterDelegate> _after = [];

    public IReadOnlyList<ElsieBeforeDelegate> Before => _before;
    public IReadOnlyList<ElsieAfterDelegate> After => _after;

    public ElsiePipelines AddBefore(ElsieBeforeDelegate hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _before.Add(hook);
        return this;
    }

    public ElsiePipelines AddBefore(Func<ElsieContext, ElsieResult?> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return AddBefore((ctx, _) => Task.FromResult(hook(ctx)));
    }

    public ElsiePipelines AddAfter(ElsieAfterDelegate hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _after.Add(hook);
        return this;
    }

    /// <summary>Side-effect after hook; returns the incoming result unchanged.</summary>
    public ElsiePipelines AddAfter(Action<ElsieContext, ElsieResult> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return AddAfter((ctx, result, _) =>
        {
            hook(ctx, result);
            return Task.FromResult(result);
        });
    }

    /// <summary>Transforming after hook (sync).</summary>
    public ElsiePipelines AddAfter(Func<ElsieContext, ElsieResult, ElsieResult> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return AddAfter((ctx, result, _) => Task.FromResult(hook(ctx, result)));
    }

    public async Task<ElsieResult?> InvokeBeforeAsync(ElsieContext context, CancellationToken cancellationToken)
    {
        foreach (var hook in _before)
        {
            var shortCircuit = await hook(context, cancellationToken).ConfigureAwait(false);
            if (shortCircuit is not null)
            {
                return shortCircuit;
            }
        }

        return null;
    }

    /// <summary>
    /// Run after hooks in order, threading the (possibly replaced) result.
    /// </summary>
    public async Task<ElsieResult> InvokeAfterAsync(
        ElsieContext context,
        ElsieResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var hook in _after)
        {
            result = await hook(context, result, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("After hook returned a null ElsieResult.");
        }

        return result;
    }
}

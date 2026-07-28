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

    public ElsiePipelines AddAfter(Action<ElsieContext, ElsieResult> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        return AddAfter((ctx, result, _) =>
        {
            hook(ctx, result);
            return Task.CompletedTask;
        });
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

    public async Task InvokeAfterAsync(ElsieContext context, ElsieResult result, CancellationToken cancellationToken)
    {
        foreach (var hook in _after)
        {
            await hook(context, result, cancellationToken).ConfigureAwait(false);
        }
    }
}

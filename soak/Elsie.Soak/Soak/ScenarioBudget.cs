namespace Elsie.Soak.Soak;

/// <summary>
/// Per-scenario wall-clock budget. Threads a deadline token through every operation so a hung
/// server or client op can never stall the whole run indefinitely.
/// </summary>
internal sealed class ScenarioBudget : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly DateTimeOffset _start;
    private readonly TimeSpan _duration;

    public ScenarioBudget(TimeSpan duration, CancellationToken rootToken)
    {
        _duration = duration;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(rootToken);
        _cts.CancelAfter(duration);
        _start = DateTimeOffset.UtcNow;
        Token = _cts.Token;
    }

    public CancellationToken Token { get; }

    public TimeSpan Elapsed => DateTimeOffset.UtcNow - _start;
    public TimeSpan Remaining => _duration - Elapsed;
    public bool Expired => Remaining <= TimeSpan.Zero;

    /// <summary>Opens a sub-window that spans <paramref name="fraction"/> of the scenario duration.</summary>
    public PhaseWindow OpenPhase(double fraction) => new(_start.Add(_duration * fraction), Token);

    public void Dispose() => _cts.Dispose();
}

/// <summary>A sub-window of a scenario budget that a phase runs inside.</summary>
internal sealed class PhaseWindow
{
    private readonly DateTimeOffset _end;

    public PhaseWindow(DateTimeOffset end, CancellationToken token)
    {
        _end = end;
        Token = token;
    }

    public CancellationToken Token { get; }
    public bool IsOpen => DateTimeOffset.UtcNow < _end;
}

/// <summary>Small helpers for per-op deadlines.</summary>
internal static class Cts
{
    /// <summary>Returns a linked token source that also cancels after <paramref name="timeout"/>.</summary>
    public static CancellationTokenSource LinkTimeout(this CancellationToken token, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        return cts;
    }
}
using System.Diagnostics;

namespace Elsie.Soak.Soak;

/// <summary>A snapshot of process memory / handle usage used for leak assertions.</summary>
internal readonly record struct MemorySnapshot(long ManagedBytes, int? OpenFds)
{
    public override string ToString()
    {
        var mb = ManagedBytes / (1024.0 * 1024.0);
        return OpenFds is { } fd
            ? $"{mb:0.0} MiB managed, {fd} fds"
            : $"{mb:0.0} MiB managed, fds n/a";
    }
}

/// <summary>
/// Captures managed-heap size and (on Linux) the open file-descriptor count. Call
/// <see cref="SettleAndCaptureAsync"/> only after a server has been stopped and cleanup has
/// had a moment to run, so the numbers reflect the retained (not transient) state.
/// </summary>
internal static class MemoryProbe
{
    /// <summary>Forces a full GC, waits for finalizers, then captures the baseline snapshot.</summary>
    public static async Task<MemorySnapshot> SettleAndCaptureAsync(CancellationToken ct)
    {
        // Give async cleanup (TLS/QUIC/socket teardown) a moment to unwind before we measure.
        await Task.Delay(400, ct).ConfigureAwait(false);
        return Capture();
    }

    public static MemorySnapshot Capture()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var managed = GC.GetTotalMemory(forceFullCollection: false);
        return new MemorySnapshot(managed, CountOpenFds());
    }

    private static int? CountOpenFds()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles("/proc/self/fd").Count();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Leak assertions evaluated after a scenario settles.</summary>
internal sealed class LeakAssessment
{
    public LeakAssessment(MemorySnapshot baseline, MemorySnapshot after)
    {
        Baseline = baseline;
        After = after;
    }

    public MemorySnapshot Baseline { get; }
    public MemorySnapshot After { get; }

    /// <summary>Retained managed memory must stay within a generous bound of the baseline.</summary>
    public bool ManagedWithinBounds(long generousSlackBytes = 32L * 1024 * 1024)
    {
        var limit = Math.Max(2L * Baseline.ManagedBytes, Baseline.ManagedBytes + generousSlackBytes);
        return After.ManagedBytes <= limit;
    }

    /// <summary>Open file descriptors must not grow beyond a generous bound of the baseline.</summary>
    public bool FdsWithinBounds(int generousSlackFds = 256)
    {
        if (Baseline.OpenFds is not { } baseline || After.OpenFds is not { } after)
        {
            return true; // non-Linux: no fd count available
        }

        return after <= baseline + generousSlackFds;
    }
}

/// <summary>Simple stopwatch wrapper for reporting a phase's wall time.</summary>
internal sealed class PhaseTimer
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public TimeSpan Elapsed => _sw.Elapsed;
}
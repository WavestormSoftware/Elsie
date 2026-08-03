namespace Elsie.Web.Tests;

/// <summary>
/// Deadline helper for HTTP/3 integration tests. These spin up real QUIC connections; if the
/// environment cannot complete QUIC (broken msquic, platform quirk, or a server-side defect)
/// an indefinite await would hang the whole CI job. A hard deadline cancels every QUIC
/// operation through the supplied token, then gives the body a bounded grace period to unwind
/// (disposing the server and the QUIC connection) before failing — no abandoned background
/// work, no orphaned processes.
/// </summary>
internal static class H3TestDeadline
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);

    /// <summary>Bounded grace period for a timed-out body to observe cancellation and dispose.</summary>
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="body"/> with a cancellation token that fires after
    /// <paramref name="timeout"/> (default <see cref="Default"/>); every QUIC operation in the
    /// body must receive that token. On timeout the body's cleanup is awaited for a bounded
    /// grace period before a <see cref="TimeoutException"/> is raised. Exceptions from the
    /// body propagate normally.
    /// </summary>
    public static async Task RunAsync(Func<CancellationToken, Task> body, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Default;
        using var cts = new CancellationTokenSource(limit);
        var bodyTask = body(cts.Token);
        var winner = await Task.WhenAny(bodyTask, Task.Delay(limit, CancellationToken.None)).ConfigureAwait(false);
        if (winner != bodyTask)
        {
            // The deadline fired and cancelled the body's token: wait (bounded) for cleanup
            // to complete so the server/connection are disposed before the failure is raised.
            try
            {
                await Task.WhenAny(bodyTask, Task.Delay(CleanupGrace, CancellationToken.None)).ConfigureAwait(false);
            }
            catch
            {
                // The body's own exception is irrelevant once the deadline fired.
            }

            throw new TimeoutException($"HTTP/3 test did not complete within {limit}.");
        }

        await bodyTask.ConfigureAwait(false);
    }
}

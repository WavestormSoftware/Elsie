using System.Diagnostics;
using System.Net;
using Elsie.Soak.Clients;
using Elsie.Soak.Soak;

namespace Elsie.Soak.Scenarios;

/// <summary>
/// Scenario 1 — HTTP/1.1 churn: rapid serial and parallel keep-alive connections, mixed
/// GET/POST body sizes from 0 B to 2 MiB, and graceful client closes mid-pipeline. Asserts no
/// hung connections at the end and that the server stops cleanly within its drain deadline.
/// </summary>
internal sealed class H1ChurnScenario
{
    private static readonly int[] BodySizes = [0, 1, 512, 64 * 1024, 256 * 1024, 2 * 1024 * 1024];

    public async Task<ScenarioResult> RunAsync(
        TimeSpan duration,
        CancellationToken rootCt,
        ServerMetrics metrics,
        MemorySnapshot baseline)
    {
        using var budget = new ScenarioBudget(duration, rootCt);
        var counters = new ScenarioCounters();
        var result = new ScenarioResult { Name = "h1-churn" };
        var problems = new List<string>();
        var openClients = new List<RawH1Client>();

        await using var server = await SoakServer.StartAsync(_ => { }, budget.Token).ConfigureAwait(false);
        var endpoint = new IPEndPoint(server.Address, server.Port);
        result.MemoryBefore = await MemoryProbe.SettleAndCaptureAsync(budget.Token).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        try
        {
            // ---- Phase 1: serial keep-alive churn on a single connection ----
            var serialPhase = budget.OpenPhase(0.30);
            var serialClient = await RawH1Client.ConnectAsync(endpoint, budget.Token).ConfigureAwait(false);
            openClients.Add(serialClient);

            var iteration = 0;
            while ((serialPhase.IsOpen || iteration < 60) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                var size = BodySizes[iteration % BodySizes.Length];
                var body = size > 0 ? RandomBody(size, iteration) : Array.Empty<byte>();
                var isPost = (iteration / BodySizes.Length) % 2 == 0;
                try
                {
                    using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(5));
                    var op = Stopwatch.StartNew();
                    H1Response res;
                    if (isPost)
                    {
                        res = await serialClient
                            .SendAsync("POST", "/upload", body, timeout.Token)
                            .WaitAsync(timeout.Token)
                            .ConfigureAwait(false);
                        ValidatePostUpload(status: 200, sent: body.Length, res);
                    }
                    else
                    {
                        var path = iteration % 3 == 0 ? "/ping" : iteration % 3 == 1 ? "/slow" : "/big";
                        res = await serialClient
                            .SendAsync("GET", path, default, timeout.Token)
                            .WaitAsync(timeout.Token)
                            .ConfigureAwait(false);
                        ValidateGet(path, res);
                    }

                    counters.Record(op.Elapsed, true);
                }
                catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
                {
                    break;
                }
                catch (Exception ex)
                {
                    counters.Record(budget.Elapsed, false);
                    problems.Add($"serial#{iteration}: {ex.GetType().Name}: {ex.Message}");
                    break; // one broken connection poisons the rest of the serial phase
                }

                iteration++;
            }

            // ---- Phase 2: parallel keep-alive churn (per-connection serial workloads) ----
            var parallelPhase = budget.OpenPhase(0.35);
            var parallelTasks = new List<Task>();
            for (var c = 0; c < 8; c++)
            {
                parallelTasks.Add(RunParallelWorkerAsync(c, endpoint, budget, rootCt, counters, openClients, problems));
            }

            await Task.WhenAll(parallelTasks).ConfigureAwait(false);

            // ---- Phase 3: graceful close mid-pipeline ----
            var midPhase = budget.OpenPhase(0.20);
            var aborts = 0;
            while ((midPhase.IsOpen || aborts < 20) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                try
                {
                    using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(5));
                    var victim = await RawH1Client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    openClients.Add(victim);
                    await victim.AbortMidRequestAsync(timeout.Token).ConfigureAwait(false); // half-close mid-body
                    aborts++;
                    if (aborts % 5 == 0)
                    {
                        // Idle open-then-close with no bytes at all.
                        var idle = await RawH1Client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                        openClients.Add(idle);
                        await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                        await idle.DisposeAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
                {
                    break;
                }
                catch (Exception ex)
                {
                    problems.Add($"abort#{aborts}: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }

            // ---- Phase 4: residual churn until the scenario budget expires ----
            var churnPhase = budget.OpenPhase(0.15);
            while ((churnPhase.IsOpen || iteration < 80) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                try
                {
                    using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(5));
                    await using var fresh = await RawH1Client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    var ping = await fresh.SendAsync("GET", "/ping", default, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    ValidateGet("/ping", ping);
                }
                catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
                {
                    break;
                }
                catch (Exception ex)
                {
                    problems.Add($"churn: {ex.GetType().Name}: {ex.Message}");
                    break;
                }

                iteration++;
            }
        }
        catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
        {
            // Scenario deadline reached mid-phase: fine, downtime is part of the test.
        }
        catch (Exception ex)
        {
            problems.Add($"scenario: {ex}");
        }

        // ---- Sanity: a fresh connection must still work (nothing hung) ----
        try
        {
            using var timeout = rootCt.LinkTimeout(TimeSpan.FromSeconds(5));
            await using var probe = await RawH1Client.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            var ping = await probe.SendAsync("GET", "/ping", default, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (ping.StatusCode != 200 || ping.BodyAsText != "pong")
            {
                problems.Add($"post-traffic ping failed: {ping.StatusCode} {ping.BodyAsText}");
            }
        }
        catch (Exception ex)
        {
            problems.Add($"post-traffic ping: {ex.GetType().Name}: {ex.Message}");
        }

        // Close every client we still hold (none may stay open across the stop).
        foreach (var client in openClients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        // ---- Stop the server: must complete inside the drain deadline ----
        result.ServerStopDuration = await server.StopAsync(rootCt).ConfigureAwait(false);
        result.ServerActiveAfterDrain = await DrainActiveAsync(metrics, rootCt).ConfigureAwait(false);
        result.MemoryAfter = await MemoryProbe.SettleAndCaptureAsync(rootCt).ConfigureAwait(false);

        var (requests, failures, refusals, p50, p99) = counters.Snapshot();
        result.Requests = requests;
        result.Failures = failures;
        result.ExpectedRefusals = refusals;
        result.P50Ms = p50;
        result.P99Ms = p99;
        result.Duration = sw.Elapsed;

        var assessment = new LeakAssessment(baseline, result.MemoryAfter.Value);
        result.Passed = problems.Count == 0
            && failures == 0
            && result.ServerStopDuration.Value <= TimeSpan.FromSeconds(15)
            && result.ServerActiveAfterDrain == 0
            && assessment.ManagedWithinBounds()
            && assessment.FdsWithinBounds();
        result.Details = problems.Count > 0 ? string.Join(" | ", problems).Shorten(500) : null;
        if (result.ServerStopDuration.Value > TimeSpan.FromSeconds(15))
        {
            result.FailureMessage = $"server stop took {result.ServerStopDuration.Value.TotalSeconds:0.0}s (>15s)";
        }
        else if (result.ServerActiveAfterDrain != 0)
        {
            result.FailureMessage = $"server still reports {result.ServerActiveAfterDrain} active connection(s) after drain";
        }
        else if (!assessment.ManagedWithinBounds())
        {
            result.FailureMessage = $"retained managed memory {result.MemoryAfter.Value.ManagedBytes / 1048576.0:0.0} MiB exceeds generous bound of baseline {baseline.ManagedBytes / 1048576.0:0.0} MiB";
        }
        else if (!assessment.FdsWithinBounds())
        {
            result.FailureMessage = $"open fds {result.MemoryAfter.Value.OpenFds} exceed baseline {baseline.OpenFds} + 256";
        }

        return result;
    }

    private static async Task RunParallelWorkerAsync(
        int worker,
        IPEndPoint endpoint,
        ScenarioBudget budget,
        CancellationToken rootCt,
        ScenarioCounters counters,
        List<RawH1Client> openClients,
        List<string> problems)
    {
        var phase = budget.OpenPhase(0.35);
        var client = await RawH1Client.ConnectAsync(endpoint, budget.Token).WaitAsync(TimeSpan.FromSeconds(10), budget.Token).ConfigureAwait(false);
        lock (openClients)
        {
            openClients.Add(client);
        }

        var iteration = 0;
        while ((phase.IsOpen || iteration < 40) &&
               !DeadlineReached(budget, rootCt) &&
               budget.Remaining > TimeSpan.FromSeconds(2))
        {
            try
            {
                using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(5));
                var op = Stopwatch.StartNew();
                if (iteration % 2 == 0)
                {
                    var size = BodySizes[iteration % BodySizes.Length];
                    var body = size > 0 ? RandomBody(size, worker * 1000 + iteration) : Array.Empty<byte>();
                    var res = await client
                        .SendAsync("POST", "/echo", body, timeout.Token)
                        .WaitAsync(timeout.Token)
                        .ConfigureAwait(false);
                    if (res.StatusCode != 200 || !res.Body.AsSpan().SequenceEqual(body))
                    {
                        problems.Add($"w{worker}#{iteration}: echo mismatch (status {res.StatusCode}, {res.Body.Length} bytes vs {body.Length})");
                    }
                }
                else
                {
                    var res = await client
                        .SendAsync("GET", iteration % 4 == 1 ? "/slow" : "/ping", default, timeout.Token)
                        .WaitAsync(timeout.Token)
                        .ConfigureAwait(false);
                    if (res.StatusCode != 200)
                    {
                        problems.Add($"w{worker}#{iteration}: GET status {res.StatusCode}");
                    }
                }

                counters.Record(op.Elapsed, true);
            }
            catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
            {
                break;
            }
            catch (Exception ex)
            {
                if (client.RemoteClosed && budget.Remaining > TimeSpan.FromSeconds(2))
                {
                    problems.Add($"w{worker}#{iteration}: server closed conn mid-keepalive: {ex.GetType().Name}");
                }

                counters.Record(budget.Elapsed, false);
                break; // connection-level failure ends this worker
            }

            iteration++;
        }
    }

    private static void ValidateGet(string path, H1Response res)
    {
        if (res.StatusCode != 200)
        {
            throw new InvalidDataException($"GET {path} → {res.StatusCode}");
        }

        switch (path)
        {
            case "/ping" when res.BodyAsText != "pong":
                throw new InvalidDataException($"GET /ping → '{res.BodyAsText}'");
            case "/slow" when res.BodyAsText != "slow":
                throw new InvalidDataException($"GET /slow → '{res.BodyAsText}'");
            case "/big" when res.Body.Length != 1024 * 1024 || res.Body[0] != 0 || res.Body[^1] != (byte)((1024 * 1024 - 1) % 251):
                throw new InvalidDataException($"GET /big → {res.Body.Length} bytes");
        }
    }

    private static void ValidatePostUpload(int status, int sent, H1Response res)
    {
        if (res.StatusCode != status)
        {
            throw new InvalidDataException($"POST /upload → {res.StatusCode}");
        }

        if (res.BodyAsText != sent.ToString())
        {
            throw new InvalidDataException($"POST /upload echoed '{res.BodyAsText}', expected {sent}");
        }
    }

    private static byte[] RandomBody(int size, int seed)
    {
        var body = new byte[size];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)((i + seed) % 251);
        }

        return body;
    }

    private static bool DeadlineReached(ScenarioBudget budget, CancellationToken rootCt) =>
        budget.Expired || rootCt.IsCancellationRequested;

    private static bool IsDeadline(ScenarioBudget budget, CancellationToken rootCt, Exception ex) =>
        ex is OperationCanceledException && (budget.Token.IsCancellationRequested || rootCt.IsCancellationRequested);

    private static async Task<long> DrainActiveAsync(ServerMetrics metrics, CancellationToken ct)
    {
        // Poll briefly: the final per-connection decrement may race StopAsync's return.
        for (var i = 0; i < 20; i++)
        {
            if (metrics.ActiveConnections == 0)
            {
                return 0;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return metrics.ActiveConnections;
    }
}

internal static class StringShim
{
    public static string Shorten(this string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}
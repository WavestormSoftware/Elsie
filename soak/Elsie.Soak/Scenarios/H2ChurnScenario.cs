using System.Diagnostics;
using System.Net;
using Elsie.Soak.Clients;
using Elsie.Soak.Soak;
using Elsie.Web;

namespace Elsie.Soak.Scenarios;

/// <summary>
/// Scenario 2 — HTTP/2 churn over TLS (ALPN h2): concurrent streams up to the configured
/// stream limit, client-initiated stream resets (RST_STREAM), multi-hundred-KiB bodies through
/// 64 KiB flow-control windows, and 100+ connection open/close cycles.
/// </summary>
internal sealed class H2ChurnScenario
{
    private const int StreamLimit = 100;

    public async Task<ScenarioResult> RunAsync(
        TimeSpan duration,
        CancellationToken rootCt,
        ServerMetrics metrics,
        MemorySnapshot baseline)
    {
        using var budget = new ScenarioBudget(duration, rootCt);
        var counters = new ScenarioCounters();
        var result = new ScenarioResult { Name = "h2-churn" };
        var problems = new List<string>();
        var clients = new List<H2Client>();

        using var cert = CertificateFactory.CreateSelfSigned();
        await using var server = await SoakServer.StartAsync(
            listen =>
            {
                listen.UseHttps = true;
                listen.Certificate = cert;
                listen.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            },
            budget.Token).ConfigureAwait(false);
        result.MemoryBefore = await MemoryProbe.SettleAndCaptureAsync(budget.Token).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        try
        {
            // ---- Phase 1: concurrent streams on one connection, up to the server limit ----
            var muxPhase = budget.OpenPhase(0.35);
            var mux = new H2Client(server.Port);
            clients.Add(mux);

            var batch = 0;
            while ((muxPhase.IsOpen || batch < 3) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                var tasks = new List<Task>();
                var concurrency = batch == 0 ? StreamLimit + 50 : 40; // first batch stresses the 100-stream cap
                using var batchTimeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(20));
                for (var i = 0; i < concurrency; i++)
                {
                    var path = i % 5 == 0 ? "/slow" : "/ping";
                    tasks.Add(MuxRequestAsync(mux, path, i, counters, batchTimeout.Token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                batch++;
            }

            // ---- Phase 2: large bodies through flow control (64 KiB windows) ----
            var fcPhase = budget.OpenPhase(0.25);
            var fcBatch = 0;
            var payload = BuildPayload(512 * 1024, seed: 42);
            while ((fcPhase.IsOpen || fcBatch < 3) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                try
                {
                    using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(20));
                    var op = Stopwatch.StartNew();
                    var echoed = await mux.EchoAsync(payload, expected: payload, timeout.Token).ConfigureAwait(false);
                    if (!echoed.AsSpan().SequenceEqual(payload))
                    {
                        problems.Add("flow-control echo mismatch");
                    }

                    using var big = await mux.GetAsync("/big", timeout.Token).ConfigureAwait(false);
                    var bigBody = await big.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                    if (bigBody.Length != 1024 * 1024 || bigBody[0] != 0)
                    {
                        problems.Add($"flow-control /big mismatch ({bigBody.Length} bytes)");
                    }

                    counters.Record(op.Elapsed, true);
                }
                catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
                {
                    break;
                }
                catch (Exception ex)
                {
                    problems.Add($"flow-control batch {fcBatch}: {ex.GetType().Name}: {ex.Message}");
                    break;
                }

                fcBatch++;
            }

            // ---- Phase 3: client-initiated stream resets ----
            var resetPhase = budget.OpenPhase(0.20);
            var resetBatches = 0;
            while ((resetPhase.IsOpen || resetBatches < 2) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                const int resetCount = 32;
                var resetTasks = new List<Task>();
                for (var i = 0; i < resetCount; i++)
                {
                    using var resetCts = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    resetCts.CancelAfter(15);
                    resetTasks.Add(MuxRequestAsync(
                        mux,
                        i % 2 == 0 ? "/slow" : "/big",
                        i,
                        counters,
                        resetCts.Token,
                        cancellationIsExpected: true));
                }

                await Task.WhenAll(resetTasks).ConfigureAwait(false);

                // Connection must remain healthy after the resets.
                using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(5));
                var ping = await mux.GetTextAsync("/ping", timeout.Token).ConfigureAwait(false);
                if (ping != "pong")
                {
                    problems.Add($"post-reset ping → '{ping}'");
                }

                resetBatches++;
            }

            // ---- Phase 4: connection churn (100+) ----
            var churnPhase = budget.OpenPhase(0.20);
            var churn = 0;
            while ((churnPhase.IsOpen || churn < 120) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                try
                {
                    using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(10));
                    using var churnClient = new H2Client(server.Port);
                    var op = Stopwatch.StartNew();
                    var body = await churnClient.GetTextAsync("/ping", timeout.Token).ConfigureAwait(false);
                    if (body != "pong")
                    {
                        problems.Add($"churn ping → '{body}'");
                    }

                    counters.Record(op.Elapsed, true);
                }
                catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
                {
                    break;
                }
                catch (Exception ex)
                {
                    problems.Add($"churn#{churn}: {ex.GetType().Name}: {ex.Message}");
                    break;
                }

                churn++;
            }

            // Rapid parallel churn burst at the end of the window.
            if (!DeadlineReached(budget, rootCt))
            {
                var bursts = Enumerable.Range(0, 16).Select(_ => ParallelChurnAsync(server.Port, budget, rootCt, counters, problems));
                await Task.WhenAll(bursts).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
        {
            // Scenario deadline reached mid-phase: fine.
        }
        catch (Exception ex)
        {
            problems.Add($"scenario: {ex}");
        }

        // Close all clients before stopping so the stop reflects a clean drain.
        foreach (var client in clients)
        {
            client.Dispose();
        }

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

    private static async Task MuxRequestAsync(
        H2Client client,
        string path,
        int index,
        ScenarioCounters counters,
        CancellationToken token,
        bool cancellationIsExpected = false)
    {
        try
        {
            var op = Stopwatch.StartNew();
            using var res = await client.GetAsync(path, token).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            _ = await res.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            counters.Record(op.Elapsed, true);
        }
        catch (Exception ex) when (cancellationIsExpected && ex is OperationCanceledException)
        {
            counters.Record(TimeSpan.Zero, true, expectedRefusal: true);
        }
        catch (Exception)
        {
            counters.Record(TimeSpan.Zero, false);
        }
    }

    private static async Task ParallelChurnAsync(
        int port,
        ScenarioBudget budget,
        CancellationToken rootCt,
        ScenarioCounters counters,
        List<string> problems)
    {
        try
        {
            using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(10));
            using var client = new H2Client(port);
            var op = Stopwatch.StartNew();
            var body = await client.GetTextAsync("/ping", timeout.Token).ConfigureAwait(false);
            if (body != "pong")
            {
                problems.Add($"burst ping → '{body}'");
            }

            counters.Record(op.Elapsed, true);
        }
        catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
        {
            // scenario over
        }
        catch (Exception ex)
        {
            problems.Add($"burst: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static byte[] BuildPayload(int size, int seed)
    {
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i + seed) % 251);
        }

        return bytes;
    }

    private static bool DeadlineReached(ScenarioBudget budget, CancellationToken rootCt) =>
        budget.Expired || rootCt.IsCancellationRequested;

    private static bool IsDeadline(ScenarioBudget budget, CancellationToken rootCt, Exception ex) =>
        ex is OperationCanceledException && (budget.Token.IsCancellationRequested || rootCt.IsCancellationRequested);

    private static async Task<long> DrainActiveAsync(ServerMetrics metrics, CancellationToken ct)
    {
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
using System.Diagnostics;
using Elsie.Soak.Clients;
using Elsie.Soak.Soak;
using Elsie.Web;
using System.Net.Quic;

namespace Elsie.Soak.Scenarios;

/// <summary>
/// Scenario 3 — HTTP/3 churn over QUIC (UDP). Repeated connect/disconnect, parallel request
/// streams per connection, multi-hundred-KiB bodies through flow control, client stream resets,
/// and rapid reconnects. Skipped gracefully when <see cref="QuicListener.IsSupported"/> is false
/// (no libmsquic).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class H3ChurnScenario
{
    public async Task<ScenarioResult> RunAsync(
        TimeSpan duration,
        CancellationToken rootCt,
        ServerMetrics metrics,
        MemorySnapshot baseline)
    {
        if (!QuicListener.IsSupported)
        {
            return ScenarioResult.SkippedResult("h3-churn", "QuicListener.IsSupported = false (no libmsquic)");
        }

        using var budget = new ScenarioBudget(duration, rootCt);
        var counters = new ScenarioCounters();
        var result = new ScenarioResult { Name = "h3-churn" };
        var problems = new List<string>();
        var clients = new List<RawH3Client>();

        using var cert = CertificateFactory.CreateSelfSigned();
        await using var server = await SoakServer.StartAsync(
            listen =>
            {
                listen.UseHttps = true;
                listen.Certificate = cert;
                listen.EnableHttp3 = true;
                listen.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            },
            budget.Token).ConfigureAwait(false);
        result.MemoryBefore = await MemoryProbe.SettleAndCaptureAsync(budget.Token).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        try
        {
            // ---- Phase 1: parallel request streams on one connection ----
            var streamsPhase = budget.OpenPhase(0.30);
            var streamsClient = await RawH3Client.ConnectAsync(server.Port, budget.Token).ConfigureAwait(false);
            clients.Add(streamsClient);

            var batch = 0;
            while ((streamsPhase.IsOpen || batch < 3) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                var tasks = new List<Task>();
                const int concurrency = 60;
                for (var i = 0; i < concurrency; i++)
                {
                    var path = i % 5 == 0 ? "/slow" : "/ping";
                    tasks.Add(H3RequestAsync(streamsClient, path, i, counters, problems, budget.Token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                batch++;
            }

            // ---- Phase 2: large bodies through flow control ----
            var fcPhase = budget.OpenPhase(0.20);
            var fcBatch = 0;
            var payload = BuildPayload(256 * 1024, seed: 7);
            while ((fcPhase.IsOpen || fcBatch < 3) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                try
                {
                    using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(20));
                    var op = Stopwatch.StartNew();
                    var echoed = await streamsClient.RequestAsync("POST", "/echo", payload, timeout.Token).ConfigureAwait(false);
                    if (echoed.Status != "200" || !echoed.Body.AsSpan().SequenceEqual(payload))
                    {
                        problems.Add($"flow-control echo mismatch (status {echoed.Status}, {echoed.Body.Length} bytes vs {payload.Length})");
                    }

                    var big = await streamsClient.RequestAsync("GET", "/big", default, timeout.Token).ConfigureAwait(false);
                    if (big.Status != "200" || big.Body.Length != 1024 * 1024 || big.Body[0] != 0)
                    {
                        problems.Add($"flow-control /big mismatch ({big.Body.Length} bytes)");
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

            // ---- Phase 3: stream resets ----
            var resetPhase = budget.OpenPhase(0.15);
            var resets = 0;
            while ((resetPhase.IsOpen || resets < 20) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                try
                {
                    using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(10));
                    await streamsClient.ResetStreamAsync(timeout.Token).ConfigureAwait(false);
                    resets++;
                }
                catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
                {
                    break;
                }
                catch (Exception ex)
                {
                    problems.Add($"reset#{resets}: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }

            // Connection must remain healthy after resets.
            if (!DeadlineReached(budget, rootCt))
            {
                try
                {
                    using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(10));
                    var ping = await streamsClient.RequestAsync("GET", "/ping", default, timeout.Token).ConfigureAwait(false);
                    if (ping.Status != "200" || ping.BodyAsText != "pong")
                    {
                        problems.Add($"post-reset h3 ping → {ping.Status} '{ping.BodyAsText}'");
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"post-reset h3 ping: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ---- Phase 4: connection churn (100+) + rapid reopen ----
            var churnPhase = budget.OpenPhase(0.35);
            var churn = 0;
            while ((churnPhase.IsOpen || churn < 100) &&
                   !DeadlineReached(budget, rootCt) &&
                   budget.Remaining > TimeSpan.FromSeconds(2))
            {
                var batchTasks = new List<Task>();
                foreach (var _ in Enumerable.Range(0, 4))
                {
                    batchTasks.Add(ChurnOnceAsync(server.Port, budget, rootCt, counters, problems));
                }

                await Task.WhenAll(batchTasks).ConfigureAwait(false);
                churn += 4;
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

        foreach (var client in clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
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

    private static async Task H3RequestAsync(
        RawH3Client client,
        string path,
        int index,
        ScenarioCounters counters,
        List<string> problems,
        CancellationToken token)
    {
        try
        {
            using var timeout = token.LinkTimeout(TimeSpan.FromSeconds(15));
            var op = Stopwatch.StartNew();
            var res = await client.RequestAsync("GET", path, default, timeout.Token).ConfigureAwait(false);
            if (path == "/slow" && (res.Status != "200" || res.BodyAsText != "slow"))
            {
                problems.Add($"h3 /slow → {res.Status} '{res.BodyAsText}'");
            }
            else if (path == "/ping" && (res.Status != "200" || res.BodyAsText != "pong"))
            {
                problems.Add($"h3 /ping → {res.Status} '{res.BodyAsText}'");
            }

            counters.Record(op.Elapsed, true);
        }
        catch (Exception)
        {
            counters.Record(TimeSpan.Zero, false);
        }
    }

    private static async Task ChurnOnceAsync(
        int port,
        ScenarioBudget budget,
        CancellationToken rootCt,
        ScenarioCounters counters,
        List<string> problems)
    {
        try
        {
            using var timeout = budget.Token.LinkTimeout(TimeSpan.FromSeconds(10));
            var op = Stopwatch.StartNew();
            await using var client = await RawH3Client.ConnectAsync(port, timeout.Token).ConfigureAwait(false);
            var ping = await client.RequestAsync("GET", "/ping", default, timeout.Token).ConfigureAwait(false);
            if (ping.Status != "200" || ping.BodyAsText != "pong")
            {
                problems.Add($"churn h3 ping → {ping.Status} '{ping.BodyAsText}'");
            }

            counters.Record(op.Elapsed, true);
        }
        catch (Exception ex) when (IsDeadline(budget, rootCt, ex))
        {
            // scenario over
        }
        catch (Exception ex)
        {
            problems.Add($"churn h3: {ex.GetType().Name}: {ex.Message}");
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
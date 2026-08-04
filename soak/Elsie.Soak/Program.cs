using System.Diagnostics;
using System.Net;
using Elsie.Soak.Clients;
using Elsie.Soak.Scenarios;
using Elsie.Soak.Soak;
using Elsie.Web;
using System.Net.Quic;

namespace Elsie.Soak;

internal static class Program
{
#pragma warning disable CA1416 // guarded by QuicListener.IsSupported + OS checks at runtime
    public static async Task<int> Main(string[] args)
    {
        var perScenario = ParseDuration(args);
        Console.WriteLine(
            $"Elsie soak: HTTP server lifecycle stress (per-scenario duration {perScenario.TotalSeconds:0}s), " +
            $"pid {Environment.ProcessId}, QuicListener.IsSupported={QuicListener.IsSupported}");

        // Global hard deadline: warmup + all scenarios + generous stop/settle margin.
        var overallSeconds = 60 + (perScenario.TotalSeconds * 3.5) + 120;
        using var rootCts = new CancellationTokenSource(TimeSpan.FromSeconds(overallSeconds));
        var rootCt = rootCts.Token;

        var failures = new List<string>();
        var results = new List<ScenarioResult>();
        using var metrics = new ServerMetrics();

        // ---- Warmup: one full lifecycle per transport so lazy pools are part of the baseline ----
        try
        {
            await WarmupAsync(rootCt).ConfigureAwait(false);
            await WarmupH2Async(rootCt).ConfigureAwait(false);
            if (QuicListener.IsSupported)
            {
                await WarmupH3Async(rootCt).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failures.Add($"warmup failed: {ex}");
        }

        var baseline = await MemoryProbe.SettleAndCaptureAsync(rootCt).ConfigureAwait(false);
        Console.WriteLine($"baseline retained: {baseline}");

        var scenarios = new (string Name, Func<TimeSpan, CancellationToken, ServerMetrics, MemorySnapshot, Task<ScenarioResult>> Run)[]
        {
            ("h1", new H1ChurnScenario().RunAsync),
            ("h2", new H2ChurnScenario().RunAsync),
            ("h3", new H3ChurnScenario().RunAsync)
        };

        foreach (var scenario in scenarios)
        {
            ScenarioResult result;
            try
            {
                result = await scenario.Run(perScenario, rootCt, metrics, baseline).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new ScenarioResult
                {
                    Name = scenario.Name,
                    Passed = false,
                    FailureMessage = $"{ex.GetType().Name}: {ex.Message}"
                };
            }

            results.Add(result);
            Console.WriteLine($"{scenario.Name}: {(result.Skipped ? $"skipped — {result.SkipReason}" : result.Passed ? "PASS" : "FAIL")}");
            if (!result.Passed && !result.Skipped)
            {
                failures.Add($"{scenario.Name}: {result.FailureMessage ?? result.Details ?? "failed"}");
            }
        }

        PrintReport(results, baseline);
        Console.WriteLine(failures.Count == 0
            ? "soak overall: PASS (exit 0)"
            : $"soak overall: FAIL ({failures.Count} failing scenario(s)) — exit 1");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }

        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task WarmupAsync(CancellationToken ct)
    {
        await using var server = await SoakServer.StartAsync(_ => { }, ct).ConfigureAwait(false);
        await using var client = await RawH1Client.ConnectAsync(new IPEndPoint(server.Address, server.Port), ct).ConfigureAwait(false);
        for (var i = 0; i < 5; i++)
        {
            var res = await client.SendAsync("GET", "/ping", default, ct).ConfigureAwait(false);
            if (res.StatusCode != 200)
            {
                throw new InvalidDataException($"warmup h1 ping → {res.StatusCode}");
            }
        }
    }

    private static async Task WarmupH2Async(CancellationToken ct)
    {
        using var cert = CertificateFactory.CreateSelfSigned();
        await using var server = await SoakServer.StartAsync(
            listen =>
            {
                listen.UseHttps = true;
                listen.Certificate = cert;
                listen.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            },
            ct).ConfigureAwait(false);

        using var client = new H2Client(server.Port);
        for (var i = 0; i < 5; i++)
        {
            var body = await client.GetTextAsync("/ping", ct).ConfigureAwait(false);
            if (body != "pong")
            {
                throw new InvalidDataException($"warmup h2 ping → '{body}'");
            }
        }

        var payload = new byte[64 * 1024];
        Array.Fill(payload, (byte)0x5A);
        _ = await client.EchoAsync(payload, expected: payload, ct).ConfigureAwait(false);
    }

    private static async Task WarmupH3Async(CancellationToken ct)
    {
        using var cert = CertificateFactory.CreateSelfSigned();
        await using var server = await SoakServer.StartAsync(
            listen =>
            {
                listen.UseHttps = true;
                listen.Certificate = cert;
                listen.EnableHttp3 = true;
                listen.Protocols = ElsieHttpProtocols.Http1AndHttp2;
            },
            ct).ConfigureAwait(false);

        await using var client = await RawH3Client.ConnectAsync(server.Port, ct).ConfigureAwait(false);
        var ping = await client.RequestAsync("GET", "/ping", default, ct).ConfigureAwait(false);
        if (ping.Status != "200" || ping.BodyAsText != "pong")
        {
            throw new InvalidDataException($"warmup h3 ping → {ping.Status} '{ping.BodyAsText}'");
        }
    }

    private static void PrintReport(List<ScenarioResult> results, MemorySnapshot baseline)
    {
        Console.WriteLine();
        Console.WriteLine("scenario   requests  fail  rej     p50ms   p99ms   mem-before      mem-after       fdΔ   activeΔ  stop     result");
        foreach (var r in results)
        {
            if (r.Skipped)
            {
                var reason = r.SkipReason ?? "";
                var shown = reason.Length > 40 ? reason[..40] : reason;
                Console.WriteLine($"{r.Name,-10} {'-',9} {'-',4} {'-',6} {'-',7} {'-',7} {'-',15} {'-',15} {'-',5} {'-',7} {'-',8} SKIP ({shown})");
                continue;
            }

            var before = r.MemoryBefore is { } mb ? $"{mb.ManagedBytes / 1048576.0,7:0.0} MiB" : "n/a";
            var after = r.MemoryAfter is { } ma ? $"{ma.ManagedBytes / 1048576.0,7:0.0} MiB" : "n/a";
            var fdDelta = r.MemoryBefore is { OpenFds: { } bfd } mb2 && r.MemoryAfter is { OpenFds: { } afd } ma2
                ? $"{afd - bfd,4:+0;-0;0}"
                : "  n/a";
            Console.WriteLine(
                $"{r.Name,-10} {r.Requests,8} {r.Failures,4} {r.ExpectedRefusals,6} {r.P50Ms,7:0.00} {r.P99Ms,7:0.00} {before,15} {after,15} {fdDelta,5} {r.ServerActiveAfterDrain,7} {r.ServerStopDuration?.TotalSeconds,6:0.00}s {(r.Passed ? "PASS" : "FAIL")}");
            if (!r.Passed && !string.IsNullOrEmpty(r.Details))
            {
                Console.WriteLine($"         details: {r.Details}");
            }
        }

        Console.WriteLine($"baseline: {baseline}");
    }

    private static TimeSpan ParseDuration(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--duration", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                int.TryParse(args[i + 1], out var sec))
            {
                return TimeSpan.FromSeconds(Math.Max(1, sec));
            }

            if (args[i].StartsWith("--duration=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i]["--duration=".Length..], out var sec2))
            {
                return TimeSpan.FromSeconds(Math.Max(1, sec2));
            }
        }

        return TimeSpan.FromSeconds(60);
    }
}
# Benchmarks baseline

Machine: local agent host · Runtime: .NET 10 · Job: ShortRun (informational, **not CI-gated**).

## Framework path (route + dispatch + views)

After 0.3 routing/dispatch work (expanded ~60-route table).

### RouteMatchBenchmarks

| Method | Mean | Allocated |
|--------|------|-----------|
| Static | 26.53 ns | 72 B |
| StaticDeep | 188.35 ns | 272 B |
| Constrained | 289.26 ns | 736 B |
| Parameter | 173.12 ns | 432 B |
| CatchAll | 230.44 ns | 576 B |
| Miss | 96.54 ns | 232 B |

### DispatchBenchmarks

| Method | Mean | Allocated |
|--------|------|-----------|
| Ping | 317.5 ns | 880 B |
| Constrained | 570.7 ns | 1224 B |

### ViewRenderBenchmarks

| Method | Mean | Allocated |
|--------|------|-----------|
| RenderHome | 2.220 us | 2.27 KB |

```bash
dotnet run -c Release --project benchmarks/Elsie.Benchmarks -f net10.0 -- --job short
```

## Host (HTTP/1.1) — how to measure

Elsie’s custom host is not yet covered by BenchmarkDotNet jobs. For **end-to-end** RPS (honest comparison vs Kestrel/other frameworks), use an external load tool against a sample:

```bash
# terminal 1
dotnet run -c Release --project samples/Elsie.Sample.HelloWorld -- --urls http://127.0.0.1:5080

# terminal 2 — example with hey (or wrk / ab / k6)
hey -n 20000 -c 50 http://127.0.0.1:5080/
# or: wrk -t4 -c50 -d10s http://127.0.0.1:5080/
```

**Expectations (honest):**

| Layer | What it measures |
|-------|------------------|
| RouteMatch / Dispatch benches | Framework core only — no sockets |
| `hey` / `wrk` on HelloWorld | Full stack: accept, parse, dispatch, write |

Do **not** claim “always faster than Kestrel” without published side-by-side numbers on the same machine. Dispatch-only numbers already show a light request path; host RPS depends on connection handling, TLS, and body size.

Suggested publish checklist for a future host benchmark:

1. Warmup 2s, then 10s run  
2. Report RPS, p99 latency, errors  
3. Same for HTTP/1.1 cleartext and (optional) HTTPS  
4. Record CPU model / .NET version in this file  

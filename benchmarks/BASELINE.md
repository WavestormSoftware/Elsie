# Benchmarks baseline

Machine: local agent host · Runtime: .NET 10 · Job: ShortRun (informational, not CI-gated).

## After 0.3 routing/dispatch perf (2026-07-30, tip fb85784+)

Expanded route table (~60 routes). Filter: RouteMatch + Dispatch + View.

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

Run:

```bash
dotnet run -c Release --project benchmarks/Elsie.Benchmarks -f net10.0 -- --job short
```

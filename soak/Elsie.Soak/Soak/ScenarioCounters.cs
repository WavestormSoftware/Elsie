namespace Elsie.Soak.Soak;

/// <summary>Per-request latency and outcome collection for one scenario.</summary>
internal sealed class ScenarioCounters
{
    private readonly object _gate = new();
    private readonly List<double> _latenciesMs = [];
    private long _requests;
    private long _failures;
    private long _expectedRefusals;

    /// <summary>Records one request attempt.</summary>
    public void Record(TimeSpan latency, bool success, bool expectedRefusal = false)
    {
        lock (_gate)
        {
            _requests++;
            _latenciesMs.Add(latency.TotalMilliseconds);
            if (!success)
            {
                if (expectedRefusal)
                {
                    _expectedRefusals++;
                }
                else
                {
                    _failures++;
                }
            }
        }
    }

    public (long Requests, long Failures, long ExpectedRefusals, double P50Ms, double P99Ms) Snapshot()
    {
        lock (_gate)
        {
            double p50 = 0, p99 = 0;
            if (_latenciesMs.Count > 0)
            {
                var sorted = _latenciesMs.OrderBy(static x => x).ToArray();
                p50 = Percentile(sorted, 0.50);
                p99 = Percentile(sorted, 0.99);
            }

            return (_requests, _failures, _expectedRefusals, p50, p99);
        }
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var index = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
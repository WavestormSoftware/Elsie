using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Elsie.Soak.Soak;

/// <summary>
/// Observes the host's <see cref="System.Diagnostics.Metrics"/> instruments ("Elsie" meter) so
/// the harness can assert transport-level invariants that are not exposed publicly: the active
/// connection count must return to zero after the server drains, and the request total must
/// advance with traffic.
/// </summary>
internal sealed class ServerMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _values = new();

    public ServerMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Elsie")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Meter.Name == "Elsie")
            {
                _values.AddOrUpdate(instrument.Name, value, (_, old) => old + value);
            }
        });
        _listener.Start();
    }

    /// <summary>Current server-reported active connection count (sum of UpDownCounter deltas).</summary>
    public long ActiveConnections => _values.TryGetValue("elsie.active_connections", out var v) ? v : 0;

    /// <summary>Cumulative requests served across all servers started in this process.</summary>
    public long RequestsTotal => _values.TryGetValue("elsie.requests_total", out var v) ? v : 0;

    public void Dispose() => _listener.Dispose();
}
using System.Diagnostics.Metrics;

namespace Elsie.Web.Hosting;

internal static class ElsieMetrics
{
    public static readonly Meter Meter = new("Elsie", "0.3.0");

    public static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>("elsie.active_connections");

    public static readonly Counter<long> ConnectionsRejected =
        Meter.CreateCounter<long>("elsie.connections_rejected");

    public static readonly Counter<long> RequestsTotal =
        Meter.CreateCounter<long>("elsie.requests_total");
}

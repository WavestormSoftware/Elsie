using System.Diagnostics.Metrics;

namespace Elsie.Web.Hosting;

internal static class ElsieMetrics
{
    public static readonly Meter Meter = new("Elsie", "0.4.0");

    public static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>("elsie.active_connections");

    public static readonly Counter<long> ConnectionsRejected =
        Meter.CreateCounter<long>("elsie.connections_rejected");

    public static readonly Counter<long> RequestsTotal =
        Meter.CreateCounter<long>("elsie.requests_total");

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("elsie.http.server.request.duration", unit: "ms");

    public static readonly UpDownCounter<long> ActiveRequests =
        Meter.CreateUpDownCounter<long>("elsie.active_requests");

    public static readonly Counter<long> RequestBytesRead =
        Meter.CreateCounter<long>("elsie.http.server.request.body.size", unit: "By");

    public static readonly Counter<long> ResponseBytesWritten =
        Meter.CreateCounter<long>("elsie.http.server.response.body.size", unit: "By");

    public static readonly Counter<long> WebSocketConnections =
        Meter.CreateCounter<long>("elsie.websocket.connections");
}

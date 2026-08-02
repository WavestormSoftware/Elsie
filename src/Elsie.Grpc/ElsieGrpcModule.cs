using System.Globalization;
using Elsie;
using Elsie.Web;
using Grpc.Core;

namespace Elsie.Grpc;

/// <summary>
/// Registers collected gRPC methods as Elsie POST routes and hosts the grpc-status trailer
/// plumbing (HTTP/2 + HTTP/3).
/// </summary>
internal sealed class ElsieGrpcModule : ElsieModule
{
    public ElsieGrpcModule(ElsieServiceBinder binder, ElsieGrpcOptions options)
    {
        foreach (var method in binder.Methods)
        {
            var m = method;
            Map("POST", m.RoutePath, (ctx, ct) => HandleGrpcAsync(ctx, m, options, ct));
        }
    }

    internal static async Task<ElsieResult> HandleGrpcAsync(
        ElsieContext ctx,
        ElsieGrpcMethod method,
        ElsieGrpcOptions options,
        CancellationToken cancellationToken)
    {
        var contentType = ctx.Request.ContentType;
        if (contentType is null ||
            !contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
        {
            return ElsieResult.Problem(
                415,
                "Unsupported Media Type",
                $"gRPC requires an application/grpc content type, got '{(contentType ?? "(none)")}'.");
        }

        var callContext = new ElsieServerCallContext(
            ctx,
            method.FullName,
            ctx.Request.RemoteIp,
            options);

        return ElsieResult.Stream(
            async (stream, writeCt) =>
            {
                try
                {
                    var status = await method.InvokeAsync(callContext, ctx.Request.Body, stream, options)
                        .ConfigureAwait(false);

                    // Deadline expiry must surface as DEADLINE_EXCEEDED, not CANCELLED.
                    if (callContext.IsDeadlineExceeded &&
                        status.StatusCode is not StatusCode.DeadlineExceeded and not StatusCode.OK)
                    {
                        status = new Status(StatusCode.DeadlineExceeded, status.Detail);
                    }

                    ctx.Response.AddTrailer(
                        "grpc-status",
                        ((int)status.StatusCode).ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrEmpty(status.Detail))
                    {
                        ctx.Response.AddTrailer("grpc-message", PercentEncode(status.Detail));
                    }

                    callContext.FlushResponseTrailers();
                }
                finally
                {
                    callContext.Dispose();
                }
            },
            "application/grpc");
    }

    private static string PercentEncode(string value) =>
        Uri.EscapeDataString(value);
}

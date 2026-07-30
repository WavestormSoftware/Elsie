using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Elsie.Web;

public sealed class ElsieMiddleware
{
    private static readonly ElsieHttpResponse TerminalNotFound =
        ElsieHttpResponse.FromDispatch(
            ElsieDispatchResult.Handled(ElsieResult.NotFound(), new ElsieResponse()))!;

    private readonly RequestDelegate _next;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ILogger<ElsieMiddleware> _logger;
    private readonly bool _terminal;

    public ElsieMiddleware(
        RequestDelegate next,
        ElsieDispatcher dispatcher,
        ILogger<ElsieMiddleware> logger,
        bool terminal = false)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _terminal = terminal;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var start = Stopwatch.GetTimestamp();
        var request = HttpContextElsieRequestFactory.Create(context);
        var outcome = await _dispatcher.DispatchAsync(request, context.RequestAborted).ConfigureAwait(false);
        var response = ElsieHttpResponse.FromDispatch(outcome);

        if (response is null)
        {
            if (_terminal)
            {
                await AspNetCoreElsieResponseWriter.WriteAsync(
                    context,
                    TerminalNotFound,
                    context.RequestAborted).ConfigureAwait(false);
                Log(context, TerminalNotFound.StatusCode, start);
                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        await AspNetCoreElsieResponseWriter.WriteAsync(context, response, context.RequestAborted)
            .ConfigureAwait(false);
        Log(context, response.StatusCode, start);
    }

    private void Log(HttpContext context, int statusCode, long startTimestamp)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        _logger.LogInformation(
            "{Method} {Path} → {StatusCode} {ElapsedMs:0}ms",
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            statusCode,
            elapsedMs);
    }
}

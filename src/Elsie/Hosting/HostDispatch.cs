using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsie.Web.Hosting;

/// <summary>OpenAPI + static + filters + dispatcher — shared by HTTP/1.1 and HTTP/2.</summary>
internal sealed class HostDispatch
{
    private static readonly ActivitySource ActivitySource = new("Elsie");

    private readonly IServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly ElsieServerOptions _serverOptions;
    private readonly IElsiePrincipalAttacher[] _attachers;
    private readonly ILogger _logger;

    public HostDispatch(
        IServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        ElsieServerOptions? serverOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        _services = services;
        _dispatcher = dispatcher;
        _features = features;
        _serverOptions = serverOptions ?? new ElsieServerOptions();
        _attachers = services.GetServices<IElsiePrincipalAttacher>().ToArray();
        var factory = loggerFactory ?? services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger("Elsie.Request");
    }

    public async Task<ElsieHttpResponse> ProcessAsync(
        ElsieRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTraceIdentifier(request);

        ActivityContext parentContext = default;
        var hasParent = false;
        var traceparent = request.GetHeader("traceparent");
        if (!string.IsNullOrWhiteSpace(traceparent))
        {
            var tracestate = request.GetHeader("tracestate");
            hasParent = ActivityContext.TryParse(traceparent, tracestate, out parentContext);
        }

        using var activity = hasParent
            ? ActivitySource.StartActivity("Elsie.Dispatch", ActivityKind.Server, parentContext)
            : ActivitySource.StartActivity("Elsie.Dispatch", ActivityKind.Server);

        if (activity is not null)
        {
            activity.SetTag("http.method", request.Method);
            activity.SetTag("http.route", request.Path);
            activity.SetTag("elsie.trace_id", activity.TraceId.ToString());
            // Align request id with W3C when activity is parented/created.
            if (hasParent || string.IsNullOrWhiteSpace(request.TraceIdentifier))
            {
                request.TraceIdentifier = activity.TraceId.ToString();
            }
        }

        var start = Stopwatch.GetTimestamp();
        ElsieMetrics.ActiveRequests.Add(1);
        if (request.ContentLength is > 0)
        {
            ElsieMetrics.RequestBytesRead.Add(request.ContentLength.Value);
        }

        ElsieHttpResponse response;
        try
        {
            response = await ProcessCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Response-header CR/LF injection rejected at bake time (FromDispatch merge) is a
            // client error — surface 400, never 500. The injection stays blocked by ElsieHeaders.
            if (ex is ElsieHeaderValidationException headerValidation)
            {
                ElsieMetrics.ActiveRequests.Add(-1);
                return FromResult(ElsieResult.Problem(400, "Bad Request", headerValidation.Message));
            }

            ElsieMetrics.RequestsTotal.Add(1, new KeyValuePair<string, object?>("status", 500));
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            ElsieMetrics.RequestDuration.Record(
                elapsedMs,
                new KeyValuePair<string, object?>("method", request.Method),
                new KeyValuePair<string, object?>("status", 500),
                new KeyValuePair<string, object?>("route", request.Path));
            if (_serverOptions.LogRequests)
            {
                _logger.LogError(
                    ex,
                    "Unhandled exception {Method} {Path} trace={TraceId} client={Client}",
                    request.Method,
                    request.Path,
                    request.TraceIdentifier,
                    request.RemoteIp);
            }

            ElsieMetrics.ActiveRequests.Add(-1);
            throw;
        }

        var durationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        ElsieMetrics.ActiveRequests.Add(-1);
        ElsieMetrics.RequestsTotal.Add(1, new KeyValuePair<string, object?>("status", response.StatusCode));
        ElsieMetrics.RequestDuration.Record(
            durationMs,
            new KeyValuePair<string, object?>("method", request.Method),
            new KeyValuePair<string, object?>("status", response.StatusCode),
            new KeyValuePair<string, object?>("route", request.Path));

        if (response.Body is { Length: var bodyLen } && bodyLen > 0)
        {
            ElsieMetrics.ResponseBytesWritten.Add(bodyLen);
        }

        activity?.SetTag("http.status_code", response.StatusCode);

        if (!string.IsNullOrEmpty(request.TraceIdentifier) &&
            !response.Headers.Contains("X-Request-Id"))
        {
            response.Headers.Set("X-Request-Id", request.TraceIdentifier!);
        }

        if (activity is not null)
        {
            // W3C response propagation
            if (!response.Headers.Contains("traceparent"))
            {
                response.Headers.Set(
                    "traceparent",
                    $"00-{activity.TraceId}-{activity.SpanId}-{(activity.Recorded ? "01" : "00")}");
            }

            if (!string.IsNullOrEmpty(activity.TraceStateString) &&
                !response.Headers.Contains("tracestate"))
            {
                response.Headers.Set("tracestate", activity.TraceStateString);
            }
        }

        if (_serverOptions.EnableResponseCompression)
        {
            response = ResponseCompression.MaybeCompress(request, response, _serverOptions.CompressionMinBodyBytes);
        }

        if (_serverOptions.LogRequests)
        {
            _logger.LogInformation(
                "{Method} {Path} {StatusCode} {DurationMs}ms trace={TraceId} client={Client}",
                request.Method,
                request.Path,
                response.StatusCode,
                (long)durationMs,
                request.TraceIdentifier,
                request.RemoteIp);
        }
        return response;
    }

    private async Task<ElsieHttpResponse> ProcessCoreAsync(
        ElsieRequest request,
        CancellationToken cancellationToken)
    {
        if (_features.OpenApi is not null &&
            (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
             request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
        {
            var docPath = Normalize(_features.OpenApi.DocumentPath);
            if (string.Equals(request.Path, docPath, StringComparison.OrdinalIgnoreCase) &&
                _features.OpenApiJson is not null)
            {
                return FromResult(ElsieResult.Bytes(_features.OpenApiJson, "application/json; charset=utf-8"));
            }

            if (!string.IsNullOrWhiteSpace(_features.OpenApi.UiPath) &&
                string.Equals(request.Path, Normalize(_features.OpenApi.UiPath!), StringComparison.OrdinalIgnoreCase) &&
                _features.OpenApiUiHtml is not null)
            {
                return FromResult(ElsieResult.Bytes(_features.OpenApiUiHtml, "text/html; charset=utf-8"));
            }

            // Offline Scalar: the bundled standalone bundle is served next to the UI page.
            var standalonePath = Normalize(_features.OpenApi.UiPath!.TrimEnd('/') + "/standalone.js");
            if (string.Equals(request.Path, standalonePath, StringComparison.OrdinalIgnoreCase) &&
                _features.OpenApiUiStandaloneJs is not null)
            {
                return FromResult(ElsieResult.Bytes(
                    _features.OpenApiUiStandaloneJs,
                    "application/javascript; charset=utf-8"));
            }
        }

        foreach (var attacher in _attachers)
        {
            await attacher.AttachAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var outcome = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        return ElsieHttpResponse.FromDispatch(outcome) ?? FromResult(ElsieResult.NotFound());
    }

    public static ElsieHttpResponse FromResult(ElsieResult result) =>
        ElsieHttpResponse.FromDispatch(ElsieDispatchResult.Handled(result, new ElsieResponse()))!;

    private static void EnsureTraceIdentifier(ElsieRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.TraceIdentifier))
        {
            if (!IsSafeTraceId(request.TraceIdentifier))
            {
                request.TraceIdentifier = Guid.NewGuid().ToString("N");
            }

            return;
        }

        var incoming = request.GetHeader("X-Request-Id") ?? request.GetHeader("X-Correlation-Id");
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            incoming = incoming.Trim();
            if (IsSafeTraceId(incoming))
            {
                request.TraceIdentifier = incoming;
                return;
            }
        }

        request.TraceIdentifier = Guid.NewGuid().ToString("N");
    }

    /// <summary>Reject CR/LF/NUL and oversized ids so echo into X-Request-Id cannot inject headers.</summary>
    private static bool IsSafeTraceId(string value)
    {
        if (value.Length is 0 or > 128)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string Normalize(string path) =>
        path.StartsWith('/') ? path : "/" + path;
}

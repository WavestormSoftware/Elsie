using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web.Hosting;

/// <summary>OpenAPI + static + filters + dispatcher — shared by HTTP/1.1 and HTTP/2.</summary>
internal sealed class HostDispatch
{
    private static readonly ActivitySource ActivitySource = new("Elsie");

    private readonly ServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly ElsieServerOptions _serverOptions;
    private readonly IElsieRequestFilter[] _filters;
    private readonly IElsiePrincipalAttacher[] _attachers;

    public HostDispatch(
        ServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        ElsieServerOptions? serverOptions = null)
    {
        _services = services;
        _dispatcher = dispatcher;
        _features = features;
        _serverOptions = serverOptions ?? new ElsieServerOptions();
        _filters = services.GetServices<IElsieRequestFilter>().ToArray();
        _attachers = services.GetServices<IElsiePrincipalAttacher>().ToArray();
    }

    public async Task<ElsieHttpResponse> ProcessAsync(
        ElsieRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTraceIdentifier(request);
        using var activity = ActivitySource.StartActivity("Elsie.Dispatch", ActivityKind.Server);
        activity?.SetTag("http.method", request.Method);
        activity?.SetTag("http.route", request.Path);
        activity?.SetTag("elsie.trace_id", request.TraceIdentifier);

        ElsieHttpResponse response;
        try
        {
            response = await ProcessCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ElsieMetrics.RequestsTotal.Add(1, new KeyValuePair<string, object?>("status", 500));
            throw;
        }

        ElsieMetrics.RequestsTotal.Add(1, new KeyValuePair<string, object?>("status", response.StatusCode));
        activity?.SetTag("http.status_code", response.StatusCode);

        if (!string.IsNullOrEmpty(request.TraceIdentifier) &&
            !response.Headers.Contains("X-Request-Id"))
        {
            response.Headers.Set("X-Request-Id", request.TraceIdentifier!);
        }

        if (_serverOptions.EnableResponseCompression)
        {
            response = ResponseCompression.MaybeCompress(request, response, _serverOptions.CompressionMinBodyBytes);
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
        }

        if (_features.StaticFiles is not null)
        {
            var headerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in request.Headers)
            {
                headerMap[k] = v;
            }

            var staticResponse = StaticFileHandler.TryServe(
                request.Method,
                request.Path,
                headerMap,
                _features.StaticFiles,
                _features.ContentRoot);
            if (staticResponse is not null)
            {
                return staticResponse;
            }
        }

        foreach (var attacher in _attachers)
        {
            attacher.Attach(request);
        }

        foreach (var filter in _filters)
        {
            var handled = await filter.TryHandleAsync(request, cancellationToken).ConfigureAwait(false);
            if (handled is not null)
            {
                return handled;
            }
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
            return;
        }

        var incoming = request.GetHeader("X-Request-Id") ?? request.GetHeader("X-Correlation-Id");
        request.TraceIdentifier = string.IsNullOrWhiteSpace(incoming)
            ? Guid.NewGuid().ToString("N")
            : incoming.Trim();
    }

    private static string Normalize(string path) =>
        path.StartsWith('/') ? path : "/" + path;
}

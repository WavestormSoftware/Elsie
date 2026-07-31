using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web.Hosting;

/// <summary>OpenAPI + static + filters + dispatcher — shared by HTTP/1.1 and HTTP/2.</summary>
internal sealed class HostDispatch
{
    private readonly ServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly IElsieRequestFilter[] _filters;
    private readonly IElsiePrincipalAttacher[] _attachers;

    public HostDispatch(
        ServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features)
    {
        _services = services;
        _dispatcher = dispatcher;
        _features = features;
        _filters = services.GetServices<IElsieRequestFilter>().ToArray();
        _attachers = services.GetServices<IElsiePrincipalAttacher>().ToArray();
    }

    public async Task<ElsieHttpResponse> ProcessAsync(
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
            var staticResponse = StaticFileHandler.TryServe(
                request.Method,
                request.Path,
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

    private static string Normalize(string path) =>
        path.StartsWith('/') ? path : "/" + path;
}

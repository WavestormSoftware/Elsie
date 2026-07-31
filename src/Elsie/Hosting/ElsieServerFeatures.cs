using Elsie.OpenApi;
using Elsie.Routing;

namespace Elsie.Web.Hosting;

internal sealed class ElsieServerFeatures
{
    public ElsieStaticFileOptions? StaticFiles { get; init; }
    public ElsieOpenApiHostOptions? OpenApi { get; init; }
    public byte[]? OpenApiJson { get; set; }
    public byte[]? OpenApiUiHtml { get; set; }
    public Func<ElsieRequest, CancellationToken, Task<ElsieHttpResponse?>>? TryCorsPreflight { get; init; }
    public Action<ElsieRequest>? AttachPrincipal { get; init; }
    public string ContentRoot { get; init; } = AppContext.BaseDirectory;

    public void WarmOpenApi(RouteTable routes)
    {
        if (OpenApi is null)
        {
            return;
        }

        OpenApiJson = ElsieOpenApiDocument.ToUtf8Json(routes, OpenApi.Info);
        if (!string.IsNullOrWhiteSpace(OpenApi.UiPath))
        {
            var docPath = NormalizePath(OpenApi.DocumentPath);
            var html =
                $$"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                  <meta charset="utf-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1" />
                  <title>{{System.Net.WebUtility.HtmlEncode(OpenApi.Info.Title)}}</title>
                </head>
                <body>
                  <script id="api-reference" data-url="{{docPath}}"></script>
                  <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference"></script>
                </body>
                </html>
                """;
            OpenApiUiHtml = System.Text.Encoding.UTF8.GetBytes(html);
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.StartsWith('/') ? path : "/" + path;
    }
}

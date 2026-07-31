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

        if (OpenApi.PrebuiltDocumentUtf8 is { Length: > 0 } prebuilt)
        {
            OpenApiJson = prebuilt;
        }
        else if (!string.IsNullOrWhiteSpace(OpenApi.PrebuiltDocumentPath) &&
                 File.Exists(OpenApi.PrebuiltDocumentPath))
        {
            OpenApiJson = File.ReadAllBytes(OpenApi.PrebuiltDocumentPath);
        }
        else
        {
            OpenApiJson = ElsieOpenApiDocument.ToUtf8Json(routes, OpenApi.Info);
        }

        if (!string.IsNullOrWhiteSpace(OpenApi.UiPath))
        {
            var docPath = NormalizePath(OpenApi.DocumentPath);
            var title = System.Net.WebUtility.HtmlEncode(OpenApi.Info.Title);
            string html;
            if (OpenApi.UseScalarCdn)
            {
                html =
                    $$"""
                    <!DOCTYPE html>
                    <html lang="en">
                    <head>
                      <meta charset="utf-8" />
                      <meta name="viewport" content="width=device-width, initial-scale=1" />
                      <title>{{title}}</title>
                    </head>
                    <body>
                      <script id="api-reference" data-url="{{docPath}}"></script>
                      <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference"></script>
                    </body>
                    </html>
                    """;
            }
            else
            {
                html =
                    $$"""
                    <!DOCTYPE html>
                    <html lang="en">
                    <head>
                      <meta charset="utf-8" />
                      <meta name="viewport" content="width=device-width, initial-scale=1" />
                      <title>{{title}}</title>
                      <style>
                        body { font-family: system-ui, sans-serif; margin: 2rem; }
                        pre { background: #f4f4f5; padding: 1rem; overflow: auto; }
                      </style>
                    </head>
                    <body>
                      <h1>{{title}}</h1>
                      <p>OpenAPI document: <a href="{{docPath}}">{{docPath}}</a></p>
                      <pre id="doc">Loading…</pre>
                      <script>
                        fetch('{{docPath}}').then(r => r.json()).then(j => {
                          document.getElementById('doc').textContent = JSON.stringify(j, null, 2);
                        }).catch(e => { document.getElementById('doc').textContent = String(e); });
                      </script>
                    </body>
                    </html>
                    """;
            }

            OpenApiUiHtml = System.Text.Encoding.UTF8.GetBytes(html);
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.StartsWith('/') ? path : "/" + path;
    }
}

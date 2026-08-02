using Elsie.OpenApi;
using Elsie.Routing;

namespace Elsie.Web.Hosting;

internal sealed class ElsieServerFeatures
{
    public ElsieStaticFileOptions? StaticFiles { get; init; }
    public ElsieOpenApiHostOptions? OpenApi { get; init; }
    public byte[]? OpenApiJson { get; set; }
    public byte[]? OpenApiUiHtml { get; set; }

    /// <summary>Bundled offline Scalar UI bundle (embedded resource), served when <c>UseScalarCdn = false</c>.</summary>
    public byte[]? OpenApiUiStandaloneJs { get; set; }
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
                // Offline: serve the bundled Scalar standalone bundle from an embedded resource
                // at a path relative to the UI page.
                OpenApiUiStandaloneJs = LoadEmbeddedScalarBundle();
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
                      <script src="./standalone.js"></script>
                    </body>
                    </html>
                    """;
            }

            OpenApiUiHtml = System.Text.Encoding.UTF8.GetBytes(html);
        }
    }

    /// <summary>Loads the bundled offline Scalar bundle from assembly embedded resources.</summary>
    private static byte[] LoadEmbeddedScalarBundle()
    {
        var assembly = typeof(ElsieServerFeatures).Assembly;
        using var stream = assembly.GetManifestResourceStream("Elsie.Web.OpenApiUi.standalone.js")
            ?? throw new InvalidOperationException(
                "The offline Scalar UI bundle is missing from the Elsie assembly. " +
                "Rebuild, or run tools/UpdateScalarAssets.sh and commit the asset.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.StartsWith('/') ? path : "/" + path;
    }
}

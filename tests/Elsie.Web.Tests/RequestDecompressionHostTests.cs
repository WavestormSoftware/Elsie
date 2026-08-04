using System.IO.Compression;
using System.Net;
using System.Text;
using Elsie.Web;
using Xunit;

namespace Elsie.Web.Tests;

public class RequestDecompressionHostTests
{
    private sealed class EchoModule : ElsieModule
    {
        public EchoModule()
        {
            Post("/echo", async (ctx, ct) =>
            {
                var text = await ctx.Request.ReadTextAsync(ct);
                return ElsieResult.Text(text);
            });
        }
    }

    private const string Hello = "hello over real HTTP/1.1, hello over real HTTP/1.1";

    private static async Task<(ElsieTestServer Server, HttpClient Client)> StartAsync()
    {
        var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .UseRequestDecompression()
            .StartAsync();
        return (server, server.CreateClient());
    }

    private static HttpContent GzipContent(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(data);
        }

        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    [Fact]
    public async Task Gzip_body_is_decoded_over_real_transport()
    {
        var (server, client) = await StartAsync();
        await using (server)
        {
            using var content = GzipContent(Encoding.UTF8.GetBytes(Hello));
            using var res = await client.PostAsync("/echo", content);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal(Hello, await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Unsupported_encoding_returns_415_over_real_transport()
    {
        var (server, client) = await StartAsync();
        await using (server)
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Hello));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            content.Headers.ContentEncoding.Add("zstd");

            using var res = await client.PostAsync("/echo", content);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        }
    }
}

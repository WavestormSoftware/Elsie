using System.Net;
using System.Text.Json;
using Elsie;
using Elsie.Web;
using Elsie.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Web.Tests;

public class HostMiddlewareTests
{
    private sealed class CookieModule : ElsieModule
    {
        public CookieModule()
        {
            Get("/cookies", ctx =>
            {
                ctx.Response.SetCookie("a", "1");
                ctx.Response.SetCookie("b", "2", new ElsieCookieOptions { HttpOnly = true });
                return ElsieResult.Text("ok").WithCookie("c", "3");
            });

            Get("/body", () => ElsieResult.Text("hello-body"));
            Get("/stream", () => ElsieResult.Stream(
                async (s, ct) =>
                {
                    var bytes = "streamed"u8.ToArray();
                    await s.WriteAsync(bytes, ct);
                },
                "text/plain; charset=utf-8"));
        }
    }

    private sealed class RequestMetaModule : ElsieModule
    {
        public RequestMetaModule()
        {
            Get("/meta", ctx => ElsieResult.Text(
                $"scheme={ctx.Request.Scheme};host={ctx.Request.Host};protocol={ctx.Request.Protocol};pathBase={ctx.Request.PathBase};ip={ctx.Request.RemoteIp}"));
        }
    }

    [Fact]
    public async Task Multi_set_cookie_headers_are_all_emitted()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<CookieModule>());
        var response = await host.GetAsync("/cookies");
        response.AssertStatus(HttpStatusCode.OK);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var list = cookies.ToList();
        Assert.Equal(3, list.Count);
        Assert.Contains(list, c => c.StartsWith("a=1", StringComparison.Ordinal));
        Assert.Contains(list, c => c.StartsWith("b=2", StringComparison.Ordinal));
        Assert.Contains(list, c => c.StartsWith("c=3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Buffered_response_sets_content_length()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<CookieModule>());
        var response = await host.GetAsync("/body");
        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(10, response.Content.Headers.ContentLength);
        Assert.Equal("hello-body", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Head_suppresses_body_but_keeps_content_length()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<CookieModule>());
        using var request = new HttpRequestMessage(HttpMethod.Head, "/body");
        var response = await host.SendAsync(request);
        response.AssertStatus(HttpStatusCode.OK);
        Assert.Equal(10, response.Content.Headers.ContentLength);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Empty(bytes);
    }

    [Fact]
    public async Task Head_stream_result_suppresses_body()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<CookieModule>());
        using var request = new HttpRequestMessage(HttpMethod.Head, "/stream");
        var response = await host.SendAsync(request);
        response.AssertStatus(HttpStatusCode.OK);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Terminal_map_returns_problem_json_404()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CookieModule>(),
            app => app.MapElsie(terminal: true));

        var response = await host.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Not Found", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Non_terminal_map_falls_through()
    {
        await using var host = ElsieTestHost.Create(
            s => s.AddElsieModule<CookieModule>(),
            app =>
            {
                app.MapElsie(terminal: false);
                app.Run(async ctx =>
                {
                    ctx.Response.StatusCode = 418;
                    await ctx.Response.WriteAsync("teapot");
                });
            });

        var response = await host.GetAsync("/nope");
        Assert.Equal(418, (int)response.StatusCode);
        Assert.Equal("teapot", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Static_files_serve_content_and_head()
    {
        var root = CreateTempContent();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "static-hi");
            await using var host = ElsieTestHost.Create(
                s => s.AddElsieModule<CookieModule>(),
                app =>
                {
                    app.MapElsieStaticFiles("/assets", root);
                    app.MapElsie();
                });

            var get = await host.GetAsync("/assets/hello.txt");
            get.AssertStatus(HttpStatusCode.OK);
            Assert.Equal("static-hi", await get.Content.ReadAsStringAsync());
            Assert.Equal(9, get.Content.Headers.ContentLength);
            Assert.NotNull(get.Headers.ETag);

            using var headReq = new HttpRequestMessage(HttpMethod.Head, "/assets/hello.txt");
            var head = await host.SendAsync(headReq);
            head.AssertStatus(HttpStatusCode.OK);
            Assert.Equal(9, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Static_files_default_document_and_304()
    {
        var root = CreateTempContent();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<h1>idx</h1>");
            await using var host = ElsieTestHost.Create(
                s => s.AddElsieModule<CookieModule>(),
                app =>
                {
                    app.MapElsieStaticFiles("/assets", root);
                    app.MapElsie();
                });

            var get = await host.GetAsync("/assets/");
            get.AssertStatus(HttpStatusCode.OK);
            Assert.Equal("<h1>idx</h1>", await get.Content.ReadAsStringAsync());
            var etag = get.Headers.ETag?.ToString();
            Assert.False(string.IsNullOrEmpty(etag));

            using var conditional = new HttpRequestMessage(HttpMethod.Get, "/assets/");
            conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
            var notModified = await host.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
            Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Static_path_rejects_traversal_and_absolute_segments()
    {
        var root = Path.GetFullPath(CreateTempContent());
        try
        {
            File.WriteAllText(Path.Combine(root, "ok.txt"), "ok");

            Assert.True(ElsieStaticPath.TryResolve(root, "ok.txt", out var ok));
            Assert.True(File.Exists(ok));

            Assert.False(ElsieStaticPath.TryResolve(root, "../secret.txt", out _));
            Assert.False(ElsieStaticPath.TryResolve(root, "a/../../secret.txt", out _));
            Assert.False(ElsieStaticPath.TryResolve(root, "/etc/passwd", out _));
            Assert.False(ElsieStaticPath.TryResolve(root, "..\\secret.txt", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Request_factory_fills_scheme_host_pathbase_protocol()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<RequestMetaModule>());
        var response = await host.GetAsync("/meta");
        response.AssertStatus(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("scheme=http", body, StringComparison.Ordinal);
        Assert.Contains("host=", body, StringComparison.Ordinal);
        Assert.Contains("protocol=HTTP", body, StringComparison.Ordinal);
        Assert.Contains("pathBase=", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateApp_non_generic_starts_without_scan()
    {
        await using var app = ElsieWeb.CreateApp(
            args: ["--urls", "http://127.0.0.1:0"],
            configure: o => o.ScanEntryAssembly = false,
            quietConsole: true);

        await app.StartAsync();
        try
        {
            Assert.NotEmpty(app.Urls);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var response = await client.GetAsync("/none");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static string CreateTempContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "elsie-static-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

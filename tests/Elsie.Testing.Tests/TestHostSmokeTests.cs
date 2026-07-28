using System.Net;
using Elsie.AspNetCore;
using Elsie.Testing;
using Xunit;

namespace Elsie.Testing.Tests;

public class TestHostSmokeTests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
            Delete("/ping", () => ElsieResult.NoContent());
            Put("/echo", async (ctx, ct) =>
            {
                var body = await ctx.ReadJsonAsync<EchoDto>(ct);
                return ctx.Json(body);
            });
            Get("/hdr", () => ElsieResult.Text("x").WithHeader("X-Test", "yes"));
        }
    }

    private sealed record EchoDto(string Message);

    [Fact]
    public async Task Test_host_disposes_cleanly()
    {
        var host = ElsieTestHost.Create(s => s.AddElsieModule<PingModule>());
        var response = await host.GetAsync("/ping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Assert_helpers_check_status_body_and_headers()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<PingModule>());

        var ping = await host.GetAsync("/ping");
        ping.AssertStatus(200);
        await ping.AssertTextAsync("pong");

        var hdr = await host.GetAsync("/hdr");
        hdr.AssertStatus(HttpStatusCode.OK).AssertHeader("X-Test", "yes");

        var del = await host.DeleteAsync("/ping");
        del.AssertStatus(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task PutJson_and_AssertJson_roundtrip()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<PingModule>());
        var response = await host.PutJsonAsync("/echo", new EchoDto("hi"));
        response.AssertStatus(HttpStatusCode.OK);
        var dto = await response.AssertJsonAsync<EchoDto>();
        Assert.NotNull(dto);
        Assert.Equal("hi", dto!.Message);
    }

    [Fact]
    public async Task AssertStatus_throws_on_mismatch()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<PingModule>());
        var response = await host.GetAsync("/ping");
        var ex = Assert.Throws<HttpResponseAssertionException>(() =>
            response.AssertStatus(HttpStatusCode.NotFound));
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }
}

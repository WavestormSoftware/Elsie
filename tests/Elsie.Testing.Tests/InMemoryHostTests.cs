using Elsie.Testing;
using Xunit;

namespace Elsie.Testing.Tests;

public class InMemoryHostTests
{
    private sealed class EchoModule : ElsieModule
    {
        public EchoModule()
        {
            Get("/hi/{name}", ctx => ElsieResult.Text($"hi {ctx.RouteOrDefault("name")}"));
            Post("/echo", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<Msg>(ct);
                return bind.IsSuccess ? ctx.Json(bind.Value) : bind.Error!;
            });
        }
    }

    private sealed record Msg(string Text);

    [Fact]
    public async Task In_memory_host_get_and_post()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<EchoModule>());

        var get = await host.GetAsync("/hi/Ada");
        Assert.Equal(200, get.StatusCode);
        Assert.Equal("hi Ada", get.ReadAsString());

        var post = await host.PostJsonAsync("/echo", new Msg("yo"));
        Assert.Equal(200, post.StatusCode);
        Assert.Contains("yo", post.ReadAsString(), StringComparison.Ordinal);
    }
}

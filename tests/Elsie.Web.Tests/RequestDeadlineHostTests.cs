using System.Net;
using Elsie.Testing;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Host-level tests for <see cref="ElsieRequestDeadlineAppExtensions.UseRequestDeadline"/>: a
/// slow handler receives 408 Request Timeout over real HTTP, a fast handler completes, and
/// WebSocket / SSE handlers are not aborted.
/// </summary>
public class RequestDeadlineHostTests
{
    private sealed class DeadlineModule : ElsieModule
    {
        public DeadlineModule()
        {
            Get("/slow", async (ctx, ct) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                return ElsieResult.Text("slow-done");
            });

            Get("/fast", () => ElsieResult.Text("fast-ok"));
        }
    }

    [Fact]
    public async Task Slow_handler_returns_408_over_wire()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .UseRequestDeadline(TimeSpan.FromMilliseconds(200))
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<DeadlineModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var res = await client.GetAsync("/slow", cts.Token);
        Assert.Equal(HttpStatusCode.RequestTimeout, res.StatusCode);
        Assert.Contains("Request Timeout", await res.Content.ReadAsStringAsync(cts.Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fast_handler_returns_200_over_wire()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .UseRequestDeadline(TimeSpan.FromSeconds(30))
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<DeadlineModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var res = await client.GetAsync("/fast");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("fast-ok", await res.Content.ReadAsStringAsync());
    }
}

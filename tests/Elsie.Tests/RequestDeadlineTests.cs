using System.Text;
using Elsie.Middleware;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

/// <summary>
/// Core tests for the <see cref="RequestDeadlineMiddleware"/>: a slow handler is aborted with
/// 408 Request Timeout once the deadline fires (and the handler's dispatch token is cancelled);
/// a fast handler completes normally; WebSocket and streaming (SSE) handlers are exempt from the
/// 408 because their route handler returns a terminal result immediately.
/// </summary>
public class RequestDeadlineTests
{
    private sealed class DeadlineModule : ElsieModule
    {
        public static readonly System.Collections.Concurrent.ConcurrentBag<long> Cancelled = new();

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
                    Cancelled.Add(1);
                    throw;
                }

                return ElsieResult.Text("slow-done");
            });

            Get("/fast", () => ElsieResult.Text("fast-ok"));

            Get("/ws", ctx => ElsieResult.WebSocket((ws, ct) => Task.CompletedTask));

            Get("/sse", ctx => ElsieResult.ServerSentEvents(async (sse, ct) =>
            {
                await sse.WriteEventAsync("data", "event", cancellationToken: ct).ConfigureAwait(false);
            }));
        }
    }

    private static ElsieInMemoryHost CreateHost(TimeSpan deadline) =>
        ElsieInMemoryHost.Create(s =>
        {
            s.AddRequestDeadline(o => o.Deadline = deadline);
            s.AddElsieModule<DeadlineModule>();
        });

    /// <summary>A handler that exceeds the deadline is cancelled and returns 408.</summary>
    [Fact]
    public async Task Slow_handler_returns_408()
    {
        DeadlineModule.Cancelled.Clear();
        await using var host = CreateHost(TimeSpan.FromMilliseconds(100));

        var res = await host.GetAsync("/slow");
        Assert.Equal(408, res.StatusCode);
        Assert.Contains("Request Timeout", res.ReadAsString(), StringComparison.Ordinal);
        Assert.True(DeadlineModule.Cancelled.Count > 0, "handler's dispatch token was not cancelled");
    }

    /// <summary>A fast handler completes normally (200) before the deadline.</summary>
    [Fact]
    public async Task Fast_handler_returns_200()
    {
        await using var host = CreateHost(TimeSpan.FromSeconds(30));
        var res = await host.GetAsync("/fast");
        Assert.Equal(200, res.StatusCode);
        Assert.Equal("fast-ok", res.ReadAsString());
    }

    /// <summary>A WebSocket handler is exempt from the 408 (returns the upgrade result).</summary>
    [Fact]
    public async Task WebSocket_handler_survives_deadline()
    {
        // Very short deadline: the route handler returns the WebSocket result immediately, so
        // it must not be aborted with 408.
        await using var host = CreateHost(TimeSpan.FromMilliseconds(50));
        var res = await host.GetAsync("/ws");
        Assert.Equal(101, res.StatusCode);
    }

    /// <summary>A streaming (SSE) handler is exempt from the 408.</summary>
    [Fact]
    public async Task Sse_handler_survives_deadline()
    {
        await using var host = CreateHost(TimeSpan.FromMilliseconds(50));
        var res = await host.GetAsync("/sse");
        Assert.Equal(200, res.StatusCode);
        Assert.Contains("text/event-stream", res.ContentType ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Passing an explicit <see cref="ElsieRequestDeadlineOptions"/> via the extension works.</summary>
    [Fact]
    public async Task Null_deadline_disables_enforcement()
    {
        await using var host = ElsieInMemoryHost.Create(s =>
        {
            s.AddRequestDeadline(o => o.Deadline = TimeSpan.Zero);
            s.AddElsieModule<DeadlineModule>();
        });
        // Zero deadline → pass-through, no enforcement; the fast handler completes normally.
        var res = await host.GetAsync("/fast");
        Assert.Equal(200, res.StatusCode);
        Assert.Equal("fast-ok", res.ReadAsString());
    }
}

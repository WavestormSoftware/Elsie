using System.Net;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace Elsie.Web.Tests;

public class WebSocketTests
{
    private sealed class EchoWsModule : ElsieModule
    {
        public EchoWsModule()
        {
            Get("/ws", () => ElsieResult.WebSocket(async (ws, ct) =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await ws.ReceiveAsync(ct).ConfigureAwait(false);
                    if (msg is null)
                    {
                        break;
                    }

                    if (msg.MessageType == WebSocketMessageType.Text)
                    {
                        await ws.SendTextAsync("echo:" + msg.GetText(), ct).ConfigureAwait(false);
                    }
                }
            }));
        }
    }

    [Fact]
    public async Task Echo_websocket_roundtrip()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoWsModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        using var client = new ClientWebSocket();
        var uri = new Uri($"ws://127.0.0.1:{ep.Port}/ws");
        await client.ConnectAsync(uri, CancellationToken.None);

        var send = Encoding.UTF8.GetBytes("hello");
        await client.SendAsync(send, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var buffer = new byte[256];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None);
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Assert.Equal("echo:hello", text);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }
}

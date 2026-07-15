// Minimal WebSocket echo — the walking skeleton's server end (condition C-G0-6).
// Proves transport + TLS (via Caddy) + off-network reachability. NO game logic on purpose.
// Replaced wholesale by Emberfall.Net.Server (Kestrel host running the deterministic core, ADR-0005)
// in WS B1. Do not grow this file.

using System.Net.WebSockets;
using System.Text;

var app = WebApplication.CreateBuilder(args).Build();
app.UseWebSockets();

// Liveness probe (also lets you confirm TLS in a browser before wiring the client).
app.MapGet("/", () => "Emberfall walking skeleton: alive. Connect a WebSocket to /ws.");

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[4096];

    while (socket.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result =
            await socket.ReceiveAsync(buffer, context.RequestAborted);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", context.RequestAborted);
            break;
        }

        // Echo the frame back, prefixed so the client can see the round-trip is real.
        string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        byte[] reply = Encoding.UTF8.GetBytes($"echo: {text}");
        await socket.SendAsync(reply, WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);
    }
});

app.Run();

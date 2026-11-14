using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

static class SecureScreenSession
{
    static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "heartbeat", "ready", "manual", "move", "relative", "button", "scroll", "key", "text", "sas" };

    public static async Task Handle(HttpContext context, SecureDesktopHub hub) {
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!context.Request.IsHttps || !string.Equals(context.Request.Headers.Origin.ToString(), expected, StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        (Guid Id, System.Threading.Channels.ChannelReader<SecureDesktopHub.Frame> Reader) subscription;
        try { subscription = await hub.Subscribe(context.RequestAborted); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { context.Response.StatusCode = 503; return; }
        try {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            stop.CancelAfter(TimeSpan.FromSeconds(20));
            var sender = Task.Run(async () => {
                await foreach (var frame in subscription.Reader.ReadAllAsync(stop.Token)) {
                    if (socket.State != WebSocketState.Open) return;
                    await socket.SendAsync(frame.Metadata, WebSocketMessageType.Text, true, stop.Token);
                    if (frame.Image is not null) await socket.SendAsync(frame.Image, WebSocketMessageType.Binary, true, stop.Token);
                }
            });
            try {
                var buffer = new byte[16384];
                while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open) {
                    using var message = new MemoryStream(); WebSocketReceiveResult part;
                    do {
                        part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                        if (part.MessageType == WebSocketMessageType.Close) return;
                        if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 65536) return;
                        message.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    var bytes = message.ToArray();
                    using var json = JsonDocument.Parse(bytes);
                    if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !Allowed.Contains(type.GetString() ?? "")) return;
                    stop.CancelAfter(TimeSpan.FromSeconds(20));
                    if (type.GetString() == "ready") await hub.ViewerReady(stop.Token);
                    else if (type.GetString() != "heartbeat") await hub.Send(bytes, stop.Token);
                }
            } catch (Exception e) when (e is WebSocketException or IOException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
            finally { stop.Cancel(); try { await sender; } catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { } }
        } finally { hub.Unsubscribe(subscription.Id); }
    }
}

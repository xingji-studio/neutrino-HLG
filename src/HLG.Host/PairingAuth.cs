using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

sealed class PairingAuth
{
    static readonly object SessionKey = new();
    readonly TrustedDevices trusted;
    readonly DeviceConnections devices;
    readonly Action<string>? log;
    readonly object pairingGate = new();
    readonly object trustGate = new();
    string pairingCode = NewCode();
    DateTimeOffset started = DateTimeOffset.UtcNow;
    int attempts;
    static string NewCode() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    public string PairingCode { get { lock (pairingGate) return pairingCode; } }
    public DateTimeOffset PairingExpires { get { lock (pairingGate) return started.AddMinutes(10); } }
    public bool PairingLocked { get { lock (pairingGate) return attempts >= 10; } }
    public PairingAuth(TrustedDevices trusted, DeviceConnections? devices = null, Action<string>? log = null) { this.trusted = trusted; this.devices = devices ?? new DeviceConnections(); this.log = log; }
    public void RefreshCode() { lock (pairingGate) { pairingCode = NewCode(); started = DateTimeOffset.UtcNow; attempts = 0; } }
    static bool SameOrigin(HttpContext c) => string.Equals(c.Request.Headers.Origin.ToString(), $"{c.Request.Scheme}://{c.Request.Host}", StringComparison.OrdinalIgnoreCase);
    bool SetSession(HttpContext c, string? trustedId = null) {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (devices.Create(token, c, trustedId) is null) return false;
        c.Response.Cookies.Append("hlg_session", token, new CookieOptions { HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Strict, IsEssential = true });
        return true;
    }
    static void LimitBody(HttpContext c) { var limit = c.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 1024; }
    public async Task Handle(HttpContext context, RequestDelegate next) {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        var path = context.Request.Path;
        if (context.Request.Method == "POST" && (path == "/api/pair" || path == "/api/trust/restore")) { await next(context); return; }
        if (!devices.TryGet(context.Request.Cookies["hlg_session"] ?? "", out var session) || session is null) { context.Response.StatusCode = 401; return; }
        if (context.Request.Method is not "GET" and not "HEAD" && !SameOrigin(context)) { context.Response.StatusCode = 403; return; }
        if (context.WebSockets.IsWebSocketRequest && !SameOrigin(context)) { context.Response.StatusCode = 403; return; }
        devices.Update(session, context); context.Items[SessionKey] = session;
        var kind = path == "/api/presence/connect" ? "presence" : path == "/api/screen/connect" ? "screen" : path == "/api/terminal/connect" ? "terminal" : "";
        using var tracked = context.WebSockets.IsWebSocketRequest && kind.Length > 0 ? devices.Track(session, context, kind) : null;
        if (context.WebSockets.IsWebSocketRequest && kind.Length > 0 && tracked is null) { context.Response.StatusCode = 401; return; }
        await next(context);
    }
    public async Task<IResult> Pair(HttpContext c) {
        if (!SameOrigin(c)) return Results.StatusCode(403);
        LimitBody(c);
        var request = await c.Request.ReadFromJsonAsync<PairRequest>();
        var supplied = Encoding.UTF8.GetBytes(request?.Code ?? "");
        lock (pairingGate) {
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(10) || ++attempts > 10) { log?.Invoke("配对码已过期或尝试次数已用尽"); return Results.StatusCode(429); }
            var expected = Encoding.UTF8.GetBytes(pairingCode);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { log?.Invoke("配对失败"); return Results.Unauthorized(); }
        }
        SetSession(c); return Results.Ok(new { ok = true });
    }
    public async Task<IResult> Trust(HttpContext c) {
        LimitBody(c);
        var request = await c.Request.ReadFromJsonAsync<TrustRequest>();
        try {
            var token = request?.Token ?? "";
            lock (trustGate) {
                if (c.Items[SessionKey] is DeviceConnections.Session session) {
                    lock (session) { if (!session.Active) return Results.Unauthorized(); trusted.Add(token); session.TrustedId = trusted.Identity(token); }
                } else trusted.Add(token);
            }
            return Results.Ok(new { ok = true });
        } catch (ArgumentException) { return Results.BadRequest(new { error = "无效的设备凭据。" }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException) { return Results.Problem("无法保存设备信任记录。", statusCode: 503); }
    }
    public async Task<IResult> Restore(HttpContext c) {
        if (!SameOrigin(c)) return Results.StatusCode(403);
        if (!trusted.Available) return Results.Problem("无法读取设备信任记录。", statusCode: 503);
        LimitBody(c);
        var request = await c.Request.ReadFromJsonAsync<TrustRequest>();
        lock (trustGate) {
            if (!trusted.IsTrusted(request?.Token)) { log?.Invoke("信任设备验证失败"); return Results.Unauthorized(); }
            var identity = trusted.Identity(request!.Token)!;
            return SetSession(c, identity) ? Results.Ok(new { ok = true }) : Results.StatusCode(403);
        }
    }
    public async Task Presence(HttpContext c) {
        if (!SameOrigin(c)) { c.Response.StatusCode = 403; return; }
        if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
        using var socket = await c.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        stop.CancelAfter(TimeSpan.FromSeconds(45));
        var buffer = new byte[256];
        try {
            while (socket.State == WebSocketState.Open) {
                var message = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token);
                if (message.MessageType == WebSocketMessageType.Close) break;
                if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage) break;
                if (c.Items[SessionKey] is DeviceConnections.Session session) devices.Update(session, c);
                stop.CancelAfter(TimeSpan.FromSeconds(45));
            }
        } catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        finally { if (socket.State == WebSocketState.CloseReceived) try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (WebSocketException) { } }
    }
    public void Revoke(string id) { lock (trustGate) { trusted.Remove(id); devices.Disconnect(id, false); devices.Allow(id); } }
    public record PairRequest(string? Code);
    public record TrustRequest(string? Token);
}

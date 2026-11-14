using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

// A session is a single browser's authenticated cookie. Tabs share the cookie;
// every live WebSocket is tracked so disconnecting a device also stops its input.
sealed class DeviceConnections
{
    public sealed class Session
    {
        internal readonly ConcurrentDictionary<Guid, (HttpContext Context, string Kind)> Sockets = new();
        internal bool Active = true;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string? TrustedId { get; set; }
        public string Address { get; set; } = "未知";
        public string Name { get; set; } = "浏览器设备";
        public string Browser { get; set; } = "浏览器";
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
        long lastSeenTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        public DateTimeOffset LastSeen { get => new(Interlocked.Read(ref lastSeenTicks), TimeSpan.Zero); set => Interlocked.Exchange(ref lastSeenTicks, value.UtcDateTime.Ticks); }
    }
    public record Device(string Id, string Name, string Address, string Browser, DateTimeOffset ConnectedAt, bool Trusted, bool Online, bool Screen, bool Terminal, bool Blocked);
    readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Session> blocked = new(StringComparer.Ordinal);
    readonly object gate = new();
    readonly Action<string>? log;
    public DeviceConnections(Action<string>? log = null) { this.log = log; }
    public Session? Create(string token, HttpContext context, string? trustedId = null) {
        lock (gate) {
            if (trustedId is not null && blocked.ContainsKey(trustedId)) return null;
            var s = new Session { TrustedId = trustedId }; Update(s, context); sessions[token] = s; return s;
        }
    }
    public bool TryGet(string token, out Session? session) => sessions.TryGetValue(token, out session);
    public bool IsBlocked(string trustedId) => blocked.ContainsKey(trustedId);
    public void Allow(string id) { lock (gate) { if (blocked.TryRemove(id, out _)) log?.Invoke("已允许受信任设备重新连接"); } }
    public void Update(Session s, HttpContext context) {
        s.LastSeen = DateTimeOffset.Now;
        s.Address = context.Connection.RemoteIpAddress?.ToString() ?? s.Address;
        var ua = context.Request.Headers.UserAgent.ToString();
        if (ua.Length == 0) return;
        s.Name = ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone" : ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad" : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android 设备" : ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows 设备" : ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "Mac 设备" : "浏览器设备";
        s.Browser = ua.Contains("Edg", StringComparison.OrdinalIgnoreCase) ? "Edge" : ua.Contains("Firefox", StringComparison.OrdinalIgnoreCase) || ua.Contains("FxiOS", StringComparison.OrdinalIgnoreCase) ? "Firefox" : ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase) || ua.Contains("CriOS", StringComparison.OrdinalIgnoreCase) ? "Chrome" : ua.Contains("Safari", StringComparison.OrdinalIgnoreCase) ? "Safari" : "浏览器";
    }
    public IDisposable? Track(Session session, HttpContext context, string kind) {
        var id = Guid.NewGuid();
        lock (session) { if (!session.Active) return null; session.Sockets[id] = (context, kind); Update(session, context); }
        if (kind == "presence") log?.Invoke("设备连接：" + session.Name + " " + session.Address);
        return new Release(() => { session.Sockets.TryRemove(id, out _); if (kind == "presence") log?.Invoke("设备断开：" + session.Name + " " + session.Address); });
    }
    public IReadOnlyList<Device> List() {
        var active = sessions.Values.Where(s => s.Sockets.Values.Any(x => x.Kind == "presence" || x.Kind == "screen" || x.Kind == "terminal"));
        var groups = active.GroupBy(s => s.TrustedId ?? s.Id, StringComparer.Ordinal).Select(g => {
            var s = g.OrderByDescending(x => x.LastSeen).First();
            var kinds = g.SelectMany(x => x.Sockets.Values.Select(v => v.Kind)).ToArray();
            return new Device(s.TrustedId ?? s.Id, s.Name, s.Address, s.Browser, g.Min(x => x.ConnectedAt), s.TrustedId is not null, true, kinds.Contains("screen"), kinds.Contains("terminal"), false);
        });
        var paused = blocked.Select(kv => new Device(kv.Key, kv.Value.Name, kv.Value.Address, kv.Value.Browser, kv.Value.ConnectedAt, true, false, false, false, true));
        return groups.Concat(paused).OrderByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public void Disconnect(string id, bool blockTrusted = true) {
        lock (gate) {
            foreach (var pair in sessions.ToArray()) {
                var s = pair.Value;
                lock (s) {
                    if (s.Id != id && s.TrustedId != id || !sessions.TryRemove(pair.Key, out _)) continue;
                    if (blockTrusted && s.TrustedId is not null) blocked[s.TrustedId] = s;
                    s.Active = false; foreach (var socket in s.Sockets.Values) socket.Context.Abort();
                }
            }
        }
        log?.Invoke("已断开设备连接");
    }
    sealed class Release(Action action) : IDisposable { Action? release = action; public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke(); }
}

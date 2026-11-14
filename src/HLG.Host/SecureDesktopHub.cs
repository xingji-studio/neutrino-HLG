using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

// One worker is shared by all viewers and by the optional unattended trigger.
sealed class SecureDesktopHub : IDisposable
{
    public record Frame(byte[] Metadata, byte[]? Image = null);
    readonly UnlockSettings settings;
    readonly FileLog log;
    readonly bool https;
    readonly object gate = new();
    readonly SemaphoreSlim connectionGate = new(1, 1), writeGate = new(1, 1);
    readonly Dictionary<Guid, Channel<Frame>> viewers = new();
    readonly CancellationTokenSource stop = new();
    NamedPipeClientStream? pipe;
    bool attempted;
    SessionLockState state = SessionLockState.Unknown;
    DateTime idleSince;
    readonly Task monitor;

    public SecureDesktopHub(UnlockSettings settings, FileLog log, bool https) {
        this.settings = settings; this.log = log; this.https = https;
        state = LockState;
        // Starting or restarting while locked must not retry an earlier password.
        attempted = state != SessionLockState.Unlocked;
        settings.Changed += SettingsChanged;
        monitor = Task.Run(Monitor);
    }
    public SessionLockState LockState => SessionLock.Query(SessionLock.CurrentSessionId);
    public bool Available {
        get {
            try { return https && settings.Read().Enabled && SecureService.ServiceProcessId() != 0; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { return false; }
        }
    }

    async Task Monitor() {
        while (!stop.IsCancellationRequested) {
            try {
                var next = LockState;
                bool changed;
                lock (gate) { changed = next != state; if (changed) { if (next == SessionLockState.Unlocked) attempted = false; state = next; } }
                if (changed && next != SessionLockState.Locked) {
                    if (next == SessionLockState.Unlocked) Broadcast(new Frame(JsonSerializer.SerializeToUtf8Bytes(new { type = "desktop", locked = false })));
                    else Broadcast(new Frame(JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message = "无法确定 Windows 会话状态，请重试。" })));
                    Disconnect();
                }
                if (next == SessionLockState.Locked && https) {
                    var config = settings.Read();
                    if (!config.Enabled) { Disconnect(); await Task.Delay(500, stop.Token); continue; }
                    bool needsAuto;
                    lock (gate) needsAuto = config.Enabled && config.Automatic && config.UnlockWithoutConnection && !attempted;
                    if (needsAuto) {
                        try { await EnsureConnected(stop.Token); await TryAutomatic(stop.Token); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) {
                            lock (gate) attempted = true;
                            log.Write(LogLevel.Warning, "远程解锁", "安全桌面服务不可用", e);
                        }
                    }
                    bool idle;
                    lock (gate) idle = pipe is not null && viewers.Count == 0 && DateTime.UtcNow - idleSince > TimeSpan.FromSeconds(15);
                    if (idle) Disconnect();
                }
                await Task.Delay(500, stop.Token);
            } catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or CryptographicException) {
                log.Write(LogLevel.Warning, "远程解锁", "无法读取远程解锁设置", e);
                try { await Task.Delay(5000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public async Task<(Guid Id, ChannelReader<Frame> Reader)> Subscribe(CancellationToken cancel) {
        if (!Available || LockState != SessionLockState.Locked) throw new InvalidOperationException("远程解锁未启用或电脑未锁定。");
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (gate) viewers.Add(id, channel);
        try { await EnsureConnected(cancel); return (id, channel.Reader); }
        catch { Unsubscribe(id); throw; }
    }

    public void Unsubscribe(Guid id) {
        var keepAlive = false;
        try { var config = settings.Read(); keepAlive = config.Enabled && config.Automatic && config.UnlockWithoutConnection; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { }
        NamedPipeClientStream? old = null;
        lock (gate) {
            if (viewers.Remove(id, out var channel)) channel.Writer.TryComplete();
            if (viewers.Count == 0) { idleSince = DateTime.UtcNow; if (!keepAlive) { old = pipe; pipe = null; } }
        }
        old?.Dispose();
    }
    public Task ViewerReady(CancellationToken cancel) => TryAutomatic(cancel);

    async Task EnsureConnected(CancellationToken cancel) {
        await connectionGate.WaitAsync(cancel);
        try {
            lock (gate) { if (pipe is { IsConnected: true }) return; }
            if (!Available || LockState != SessionLockState.Locked) throw new InvalidOperationException("安全桌面服务不可用。");
            NamedPipeClientStream connected;
            try { connected = await SecureService.Connect(cancel); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { log.Write(LogLevel.Warning, "远程解锁", "无法连接登录画面组件", e); throw; }
            lock (gate) { pipe = connected; idleSince = DateTime.UtcNow; }
            _ = Task.Run(() => ReadWorker(connected), CancellationToken.None);
        } finally { connectionGate.Release(); }
    }

    async Task ReadWorker(NamedPipeClientStream connection) {
        try {
            using var reader = new StreamReader(connection, Encoding.UTF8, false, 65536, leaveOpen: true);
            while (!stop.IsCancellationRequested) {
                var line = await reader.ReadLineAsync(stop.Token);
                if (line is null || line.Length > 12_000_000) break;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var kind) && kind.GetString() == "frame") {
                    var image = Convert.FromBase64String(root.GetProperty("jpeg").GetString() ?? "");
                    var meta = JsonSerializer.SerializeToUtf8Bytes(new { type = "frame", width = root.GetProperty("width").GetInt32(), height = root.GetProperty("height").GetInt32(), cursor = root.GetProperty("cursor").Clone() });
                    Broadcast(new Frame(meta, image));
                } else {
                    if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "error") {
                        var code = root.TryGetProperty("errorCode", out var error) && error.TryGetInt32(out var value) ? value : 0;
                        log.Write(LogLevel.Warning, "远程解锁", "登录画面组件报告错误，Windows 错误码：" + code);
                    }
                    Broadcast(new Frame(Encoding.UTF8.GetBytes(line)));
                }
            }
        } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException or FormatException or InvalidOperationException) { }
        finally {
            bool wasCurrent;
            lock (gate) { wasCurrent = ReferenceEquals(pipe, connection); if (wasCurrent) pipe = null; }
            connection.Dispose();
            if (wasCurrent) Broadcast(new Frame(JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message = "登录画面连接已断开，请重试。" })));
        }
    }

    async Task TryAutomatic(CancellationToken cancel) {
        var config = settings.Read();
        if (!config.Enabled || !config.Automatic || LockState != SessionLockState.Locked) return;
        lock (gate) { if (attempted) return; attempted = true; idleSince = DateTime.UtcNow; }
        byte[]? secret;
        try { secret = settings.ReadPassword(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException) {
            log.Write(LogLevel.Warning, "远程解锁", "无法读取已保存的密码，请重新设置", e);
            Broadcast(new Frame(JsonSerializer.SerializeToUtf8Bytes(new { type = "notice", message = "已保存的密码不可用，请手动解锁并重新设置。" })));
            return;
        }
        if (secret is null) return;
        var characters = Encoding.Unicode.GetChars(secret);
        MemoryStream? json = null;
        try {
            var aliases = new List<string> { Environment.UserName, WindowsIdentity.GetCurrent().Name };
            var display = new StringBuilder(256); uint size = (uint)display.Capacity;
            if (GetUserNameEx(3, display, ref size) && display.Length > 0) aliases.Add(display.ToString());
            json = SecureSecretJson.CreateRequest(characters, aliases.Distinct(StringComparer.OrdinalIgnoreCase));
            await Send(json.GetBuffer().AsMemory(0, checked((int)json.Length)), cancel);
        } finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan())); if (json is not null) { CryptographicOperations.ZeroMemory(json.GetBuffer()); json.Dispose(); } }
    }

    public async Task Send(ReadOnlyMemory<byte> data, CancellationToken cancel) {
        if (data.Length > 65536 || !https || !settings.Read().Enabled || LockState != SessionLockState.Locked) throw new InvalidOperationException("登录画面已关闭。");
        NamedPipeClientStream? target; lock (gate) target = pipe;
        if (target is null) throw new IOException("安全桌面服务已断开。");
        await writeGate.WaitAsync(cancel);
        try { await target.WriteAsync(data, cancel); await target.WriteAsync("\n"u8.ToArray(), cancel); await target.FlushAsync(cancel); }
        finally { writeGate.Release(); }
    }

    void Broadcast(Frame value) { lock (gate) foreach (var viewer in viewers.Values) viewer.Writer.TryWrite(value); }
    void Disconnect() {
        NamedPipeClientStream? old;
        lock (gate) { old = pipe; pipe = null; }
        old?.Dispose();
    }
    void SettingsChanged() { Disconnect(); Broadcast(new Frame(JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message = "远程解锁设置已更改，请返回后重新连接。" }))); }
    public void Dispose() { settings.Changed -= SettingsChanged; stop.Cancel(); Disconnect(); lock (gate) { foreach (var c in viewers.Values) c.Writer.TryComplete(); viewers.Clear(); } }
    [DllImport("secur32.dll", EntryPoint = "GetUserNameExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
}

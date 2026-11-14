using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

// A short-lived SYSTEM worker in the active console session. The service
// supplies a system-only pipe name; it never accepts a network connection.
static class SecureDesktopWorker
{
    static readonly object outputGate = new();
    static Stream? output;
    static readonly ConcurrentQueue<byte[]> incoming = new();
    static volatile bool disconnected;
    static char[]? pendingPassword;
    static int pendingLength;
    static string[] accountNames = [];
    static DateTime pendingUntil;
    static DateTime pendingReady;
    static bool wokeLockScreen;

    public static void Run(string[] args) {
        SecureService.EnsureSystem();
        if (args.Length != 3 || !args[1].StartsWith("NeutrinoHLG.Worker.", StringComparison.Ordinal) || !uint.TryParse(args[2], out var session) || session != SessionLock.CurrentSessionId || session != SessionLock.ActiveConsoleSessionId || SessionLock.Query(session) != SessionLockState.Locked) throw new UnauthorizedAccessException();
        using var channel = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        channel.Connect(10000);
        if (!SecureService.VerifyServer(channel)) throw new UnauthorizedAccessException("无法验证安全桌面服务。");
        output = channel;
        var reader = Task.Run(() => ReadInput(channel));
        try {
            while (!disconnected && SessionLock.Query(session) == SessionLockState.Locked && SessionLock.ActiveConsoleSessionId == session) {
                var desktop = OpenInputDesktop(0, false, 0x02000000);
                if (desktop == IntPtr.Zero) { Write(new { type = "error", message = "无法访问 Windows 登录画面。" }); break; }
                var name = DesktopName(desktop);
                if (name is null) { CloseDesktop(desktop); Write(new { type = "error", message = "无法确定当前 Windows 桌面。" }); break; }
                var thread = new Thread(() => CaptureDesktop(desktop, name, session)) { IsBackground = true, Name = "HLG secure desktop" };
                thread.Start(); thread.Join(); CloseDesktop(desktop);
            }
        } finally { ClearPassword(); disconnected = true; while (incoming.TryDequeue(out var message)) CryptographicOperations.ZeroMemory(message); output = null; }
    }

    static async Task ReadInput(Stream input) {
        var chunk = new byte[4096]; var line = new byte[65536]; var length = 0;
        try {
            while (!disconnected) {
                var count = await input.ReadAsync(chunk);
                if (count == 0) break;
                for (var i = 0; i < count; i++) {
                    if (chunk[i] != (byte)'\n') { if (length == line.Length) return; line[length++] = chunk[i]; continue; }
                    if (incoming.Count >= 256) return;
                    incoming.Enqueue(line.AsSpan(0, length).ToArray());
                    CryptographicOperations.ZeroMemory(line.AsSpan(0, length)); length = 0;
                }
                CryptographicOperations.ZeroMemory(chunk.AsSpan(0, count));
            }
        } catch (IOException) { }
        finally { CryptographicOperations.ZeroMemory(chunk); CryptographicOperations.ZeroMemory(line); disconnected = true; }
    }

    static void CaptureDesktop(IntPtr desktop, string name, uint session) {
        var buttons = new HashSet<string>();
        try {
            if (!SetThreadDesktop(desktop)) throw new Win32Exception();
            Write(new { type = "desktop", locked = true });
            while (!disconnected && SessionLock.Query(session) == SessionLockState.Locked && SessionLock.ActiveConsoleSessionId == session) {
                var current = OpenInputDesktop(0, false, 0x02000000);
                if (current == IntPtr.Zero) throw new Win32Exception();
                string? activeName;
                try { activeName = DesktopName(current); } finally { CloseDesktop(current); }
                if (!string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase)) return;
                var bounds = ScreenSession.Bounds(IntPtr.Zero);
                if (bounds.Width < 1 || bounds.Height < 1 || (long)bounds.Width * bounds.Height > 100_000_000) throw new InvalidOperationException("登录画面尺寸不可用。");
                while (incoming.TryDequeue(out var line)) { try { ProcessInput(line, bounds, buttons); } finally { CryptographicOperations.ZeroMemory(line); } }
                TryAutomatic(session);
                var jpeg = ScreenSession.CaptureJpeg(IntPtr.Zero, bounds);
                Write(new { type = "frame", width = bounds.Width, height = bounds.Height, cursor = ScreenSession.CurrentCursor(bounds), jpeg = Convert.ToBase64String(jpeg) });
                Thread.Sleep(100);
            }
        } catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or ArgumentException or ExternalException) {
            var code = e is Win32Exception error ? error.NativeErrorCode : 0;
            Write(new { type = "error", errorCode = code, message = "登录画面暂不可用，请在电脑端检查系统服务和会话状态。" + (code != 0 ? " Windows 错误码：" + code : "") });
            disconnected = true;
        } finally {
            foreach (var button in buttons) try { ScreenSession.MouseButton(button, false); } catch (Win32Exception) { }
        }
    }

    static void ProcessInput(byte[] line, System.Drawing.Rectangle bounds, HashSet<string> buttons) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String) return;
            var type = typeValue.GetString();
            if (type == "heartbeat") return;
            if (type == "auto") {
                if (pendingPassword is not null || !root.TryGetProperty("password", out var passwordValue) || passwordValue.ValueKind != JsonValueKind.String || !root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array) return;
                if (!SecureSecretJson.TryReadPassword(line, out var chars, out var count)) return;
                pendingPassword = chars; pendingLength = count;
                accountNames = accounts.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length is > 0 and <= 256).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
                pendingReady = DateTime.UtcNow.AddSeconds(3); pendingUntil = DateTime.UtcNow.AddSeconds(13); wokeLockScreen = false;
                Write(new { type = "notice", message = "即将自动解锁，可点击手动输入。" });
                return;
            }
            // Any manual interaction takes precedence over automatic input.
            if (pendingPassword is not null) { ClearPassword(); Write(new { type = "notice", message = "已切换到手动解锁。" }); }
            switch (type) {
                case "move": if (ScreenSession.Number(root, "x", out var x) && ScreenSession.Number(root, "y", out var y)) ScreenSession.MoveAbsolute(bounds, x, y); break;
                case "relative": if (ScreenSession.Number(root, "dx", out var dx) && ScreenSession.Number(root, "dy", out var dy)) ScreenSession.MoveRelative(bounds, dx, dy); break;
                case "button":
                    if (!root.TryGetProperty("button", out var b) || !root.TryGetProperty("down", out var d)) break;
                    var button = b.GetString() ?? ""; if (button is not ("left" or "right")) break;
                    if (d.ValueKind == JsonValueKind.True && buttons.Add(button)) ScreenSession.MouseButton(button, true);
                    else if (d.ValueKind == JsonValueKind.False && buttons.Remove(button)) ScreenSession.MouseButton(button, false);
                    break;
                case "scroll": if (ScreenSession.Number(root, "amount", out var amount)) ScreenSession.Scroll((int)Math.Clamp(amount, -960, 960)); break;
                case "key":
                    if (!root.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) break;
                    var modifiers = root.TryGetProperty("modifiers", out var mods) && mods.ValueKind == JsonValueKind.Array ? mods.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString() ?? "").ToArray() : [];
                    ScreenSession.SendKey(k.GetString() ?? "", modifiers); break;
                case "text":
                    if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: <= 4096 } text) ScreenSession.SendText(text); break;
            }
        } catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { }
    }

    static void TryAutomatic(uint session) {
        if (pendingPassword is null) return;
        if (DateTime.UtcNow < pendingReady) return;
        if (DateTime.UtcNow >= pendingUntil) { ClearPassword(); Write(new { type = "notice", message = "无法确认账户或密码输入框，请手动解锁。" }); return; }
        if (SessionLock.Query(session) != SessionLockState.Locked || session != SessionLock.ActiveConsoleSessionId) { ClearPassword(); return; }
        var sending = false;
        try {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;
            GetWindowThreadProcessId(foreground, out var processId);
            using var process = Process.GetProcessById((int)processId);
            if (!wokeLockScreen && process.ProcessName.Equals("LockApp", StringComparison.OrdinalIgnoreCase)) {
                wokeLockScreen = true; ScreenSession.SendKey("Space", []); return;
            }
            if (!process.ProcessName.Equals("LogonUI", StringComparison.OrdinalIgnoreCase)) return;
            var focused = AutomationElement.FocusedElement;
            if (focused is null || !focused.Current.IsPassword || focused.Current.ProcessId != processId || accountNames.Length == 0) return;
            // Do not type a secret unless the account label is visible near the
            // focused password field and is unambiguous in the login window.
            var root = AutomationElement.FromHandle(foreground);
            if (root is null) return;
            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
            bool Matches(AutomationElement element) => !element.Current.IsOffscreen && accountNames.Contains(element.Current.Name, StringComparer.OrdinalIgnoreCase);
            var matches = root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Where(Matches).ToArray();
            if (matches.Length != 1) return;
            var fieldRect = focused.Current.BoundingRectangle; var labelRect = matches[0].Current.BoundingRectangle;
            if (!SecureLoginPolicy.IsNearby(new((float)fieldRect.X, (float)fieldRect.Y, (float)fieldRect.Width, (float)fieldRect.Height), new((float)labelRect.X, (float)labelRect.Y, (float)labelRect.Width, (float)labelRect.Height))) return;
            var rootId = root.GetRuntimeId(); var labelId = matches[0].GetRuntimeId();
            var ancestor = focused;
            var near = false;
            for (var depth = 0; depth < 4 && ancestor is not null; depth++, ancestor = TreeWalker.ControlViewWalker.GetParent(ancestor)) {
                if (ancestor.GetRuntimeId().SequenceEqual(rootId)) break;
                if (ancestor.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>().Any(x => x.GetRuntimeId().SequenceEqual(labelId))) { near = true; break; }
            }
            if (!near) return;
            var runtimeId = focused.GetRuntimeId();
            var password = pendingPassword; pendingPassword = null; accountNames = [];
            try {
                sending = true;
                foreach (var character in password.AsSpan(0, pendingLength)) {
                    var active = AutomationElement.FocusedElement;
                    if (SessionLock.Query(session) != SessionLockState.Locked || GetForegroundWindow() != foreground || active is null || !active.Current.IsPassword || !active.GetRuntimeId().SequenceEqual(runtimeId)) {
                        Write(new { type = "notice", message = "登录焦点已变化，请清空密码框后手动解锁。" }); return;
                    }
                    ScreenSession.SendUnicode(character);
                }
                var lastFocus = AutomationElement.FocusedElement;
                if (SessionLock.Query(session) != SessionLockState.Locked || GetForegroundWindow() != foreground || lastFocus is null || !lastFocus.Current.IsPassword || !lastFocus.GetRuntimeId().SequenceEqual(runtimeId)) {
                    Write(new { type = "notice", message = "登录焦点已变化，请检查密码框后手动解锁。" }); return;
                }
                ScreenSession.SendKey("Enter", []);
                Write(new { type = "notice", message = "已提交解锁信息，等待 Windows 确认。" });
            } finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan())); pendingLength = 0; }
        } catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or ArgumentException or Win32Exception or ExternalException) {
            if (sending) Write(new { type = "notice", message = "自动输入已中断，请清空密码框后手动解锁。" });
        }
    }

    static void ClearPassword() {
        if (pendingPassword is not null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pendingPassword.AsSpan()));
        pendingPassword = null; pendingLength = 0; accountNames = [];
    }
    static void Write(object message) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        lock (outputGate) { if (output is null) return; try { output.Write(bytes); output.WriteByte((byte)'\n'); output.Flush(); } catch (IOException) { disconnected = true; } }
    }
    static string? DesktopName(IntPtr desktop) {
        var buffer = new StringBuilder(256);
        return GetUserObjectInformation(desktop, 2, buffer, buffer.Capacity * 2, out _) ? buffer.ToString() : null;
    }
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError=true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError=true)] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint="GetUserObjectInformationW", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

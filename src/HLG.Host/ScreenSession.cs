using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

// The first capture backend sends JPEG frames. A selected window is captured
// with PrintWindow only; it is never replaced by a crop of the desktop.
static class ScreenSession
{
    static readonly SemaphoreSlim CaptureSlots = new(2, 2);
    const uint InputMouse = 0, InputKeyboard = 1;
    const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, Wheel = 0x0800;
    const uint KeyUp = 0x0002, Unicode = 0x0004;

    public static async Task Handle(HttpContext context)
    {
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!string.Equals(context.Request.Headers.Origin.ToString(), expected, StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var all = context.Request.Query["all"] == "1";
        var id = context.Request.Query["id"].ToString();
        _ = uint.TryParse(context.Request.Query["pid"].ToString(), out var pid);
        var request = new WindowCatalog.WindowRequest(id, pid);
        var initialWindow = all ? IntPtr.Zero : WindowCatalog.Resolve(request);
        if (!all && initialWindow == IntPtr.Zero) { context.Response.StatusCode = 404; return; }
        var initialThread = all ? 0 : GetWindowThreadProcessId(initialWindow, out _);
        var initialStart = all ? 0 : ProcessStart(pid);
        if (!all && initialStart == 0) { context.Response.StatusCode = 403; return; }
        IntPtr ResolveTarget() {
            if (all) return IntPtr.Zero;
            var window = WindowCatalog.Resolve(request);
            if (window != initialWindow || GetWindowThreadProcessId(window, out var currentPid) != initialThread || currentPid != pid || ProcessStart(pid) != initialStart) return IntPtr.Zero;
            return window;
        }
        if (!await CaptureSlots.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 429; return; }
        WebSocket accepted;
        try { accepted = await context.WebSockets.AcceptWebSocketAsync(); }
        catch { CaptureSlots.Release(); throw; }
        using var socket = accepted;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        stop.CancelAfter(TimeSpan.FromSeconds(20));
        var cancel = stop.Token;
        var pressed = new HashSet<string>();
        var focusBlocked = false;
        var sendLock = new SemaphoreSlim(1, 1);
        async Task SendJson(object value) {
            var data = JsonSerializer.SerializeToUtf8Bytes(value);
            await sendLock.WaitAsync(cancel);
            try { if (socket.State == WebSocketState.Open) await socket.SendAsync(data, WebSocketMessageType.Text, true, cancel); }
            finally { sendLock.Release(); }
        }
        var capture = Task.Run(async () => {
            try {
                while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open) {
                    var hwnd = ResolveTarget();
                    if (!all && hwnd == IntPtr.Zero) { await SendJson(new { type = "error", message = "窗口已关闭或不可用。" }); break; }
                    var bounds = Bounds(hwnd);
                    if (bounds.Width < 1 || bounds.Height < 1 || (long)bounds.Width * bounds.Height > 100_000_000) throw new InvalidOperationException("画面尺寸不可用或超出限制。");
                    var bytes = CaptureJpeg(hwnd, bounds);
                    var cursor = GetCursorPos(out var cursorPosition) ? MapCursor(bounds, new Point(cursorPosition.X, cursorPosition.Y)) : null;
                    await sendLock.WaitAsync(cancel);
                    try {
                        var meta = JsonSerializer.SerializeToUtf8Bytes(new { type = "frame", width = bounds.Width, height = bounds.Height, cursor });
                        await socket.SendAsync(meta, WebSocketMessageType.Text, true, cancel);
                        await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, cancel);
                    } finally { sendLock.Release(); }
                    await Task.Delay(100, cancel);
                }
            } catch (OperationCanceledException) { }
            catch (Exception e) when (e is IOException or WebSocketException or ExternalException or InvalidOperationException or ArgumentException) {
                try { await SendJson(new { type = "error", message = "画面捕获失败：" + e.Message }); } catch { }
            } finally { stop.Cancel(); }
        });
        try {
            var buffer = new byte[16384];
            while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open) {
                using var message = new MemoryStream(); WebSocketReceiveResult part;
                do {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 65536) return;
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) continue;
                stop.CancelAfter(TimeSpan.FromSeconds(20));
                var kind = type.GetString();
                if (kind == "heartbeat") continue;
                var hwnd = ResolveTarget();
                // Always allow releasing a button that this connection pressed.
                if (kind == "button" && root.TryGetProperty("down", out var downValue) && downValue.ValueKind == JsonValueKind.False) {
                    var button = root.GetProperty("button").GetString() ?? "";
                    if (pressed.Remove(button)) MouseButton(button, false);
                    continue;
                }
                if (!all && (hwnd == IntPtr.Zero || GetForegroundWindow() != hwnd)) {
                    foreach (var held in pressed) MouseButton(held, false);
                    pressed.Clear();
                    if (!focusBlocked) await SendJson(new { type = "focus", message = "目标窗口未处于前台，已暂停输入。" }); focusBlocked = true; continue;
                }
                if (focusBlocked) { focusBlocked = false; await SendJson(new { type = "focus", message = "" }); }
                var bounds = Bounds(hwnd);
                switch (kind) {
                    case "move":
                        if (Number(root,"x",out var x) && Number(root,"y",out var y)) MoveAbsolute(bounds, x, y);
                        break;
                    case "relative":
                        if (Number(root,"dx",out var dx) && Number(root,"dy",out var dy)) MoveRelative(bounds, dx, dy);
                        break;
                    case "button":
                        var button = root.GetProperty("button").GetString() ?? "";
                        if (!all && !CursorOnWindow(hwnd)) { await SendJson(new { type = "focus", message = "光标位置被其他窗口遮挡，已暂停点击。" }); break; }
                        if (button is "left" or "right" && pressed.Add(button)) MouseButton(button, true);
                        break;
                    case "scroll":
                        if (!all && !CursorOnWindow(hwnd)) { await SendJson(new { type = "focus", message = "光标位置被其他窗口遮挡，已暂停滚动。" }); break; }
                        if (Number(root,"amount",out var amount)) Scroll((int)Math.Clamp(amount, -960, 960));
                        break;
                    case "key":
                        var key = root.GetProperty("key").GetString() ?? "";
                        var modifiers = root.TryGetProperty("modifiers", out var mods) && mods.ValueKind == JsonValueKind.Array ? mods.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString() ?? "").ToArray() : [];
                        SendKey(key, modifiers);
                        break;
                    case "text":
                        var text = root.GetProperty("text").GetString() ?? "";
                        if (text.Length <= 4096) SendText(text);
                        break;
                }
            }
        } catch (Exception e) when (e is WebSocketException or IOException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or KeyNotFoundException or Win32Exception) { }
        finally {
            foreach (var button in pressed) { try { MouseButton(button, false); } catch (Win32Exception) { } }
            stop.Cancel();
            await Task.WhenAny(capture, Task.Delay(2000));
            if (capture.IsCompleted && socket.State == WebSocketState.CloseReceived) {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "画面已关闭", CancellationToken.None); } catch (WebSocketException) { }
            }
            // A hung target may still be inside PrintWindow; retain its slot until it returns.
            if (capture.IsCompleted) { CaptureSlots.Release(); stop.Dispose(); }
            else _ = capture.ContinueWith(_ => { CaptureSlots.Release(); stop.Dispose(); }, TaskScheduler.Default);
        }
    }

    static bool Number(JsonElement element, string name, out double value) {
        value = 0; return element.TryGetProperty(name, out var property) && property.TryGetDouble(out value) && double.IsFinite(value);
    }
    static long ProcessStart(uint pid) {
        try { using var process = Process.GetProcessById(checked((int)pid)); return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or OverflowException) { return 0; }
    }
    internal static Rectangle Bounds(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) return System.Windows.Forms.SystemInformation.VirtualScreen;
        if (!GetWindowRect(hwnd, out var r)) throw new Win32Exception();
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }
    internal static byte[] CaptureJpeg(IntPtr hwnd, Rectangle bounds) {
        using var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image)) {
            if (hwnd == IntPtr.Zero) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            else {
                var hdc = graphics.GetHdc();
                try { if (!PrintWindow(hwnd, hdc, 2)) throw new InvalidOperationException("该应用不支持窗口捕获或窗口已最小化。"); }
                finally { graphics.ReleaseHdc(hdc); }
            }
        }
        var ratio = Math.Min(1d, Math.Min(1600d / bounds.Width, 1000d / bounds.Height));
        using var scaled = new Bitmap(Math.Max(1, (int)(bounds.Width * ratio)), Math.Max(1, (int)(bounds.Height * ratio)));
        using (var graphics = Graphics.FromImage(scaled)) graphics.DrawImage(image, 0, 0, scaled.Width, scaled.Height);
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
        scaled.Save(stream, codec, parameters);
        return stream.ToArray();
    }
    internal static Point MapPoint(Rectangle bounds, double x, double y) => new(bounds.Left + (int)Math.Round(Math.Clamp(x, 0, 1) * Math.Max(0, bounds.Width - 1)), bounds.Top + (int)Math.Round(Math.Clamp(y, 0, 1) * Math.Max(0, bounds.Height - 1)));
    internal record CursorPosition([property: JsonPropertyName("x")] double X, [property: JsonPropertyName("y")] double Y);
    internal static CursorPosition MapCursor(Rectangle bounds, Point point) => new(
        (point.X - (double)bounds.Left) / Math.Max(1, bounds.Width - 1),
        (point.Y - (double)bounds.Top) / Math.Max(1, bounds.Height - 1));
    static void MoveAbsolute(Rectangle bounds, double x, double y) { var p = MapPoint(bounds, x, y); if (!SetCursorPos(p.X, p.Y)) throw new Win32Exception(); }
    static bool CursorOnWindow(IntPtr hwnd) => GetCursorPos(out var point) && GetAncestor(WindowFromPoint(point), 2) == hwnd;
    static void MoveRelative(Rectangle bounds, double dx, double dy) {
        if (!GetCursorPos(out var p)) throw new Win32Exception();
        var x = Math.Clamp((long)p.X + (long)Math.Clamp(dx, -2000, 2000), bounds.Left, (long)bounds.Right - 1);
        var y = Math.Clamp((long)p.Y + (long)Math.Clamp(dy, -2000, 2000), bounds.Top, (long)bounds.Bottom - 1);
        if (!SetCursorPos((int)x, (int)y)) throw new Win32Exception();
    }
    static void MouseButton(string button, bool down) => Inject(new Input { Type = InputMouse, Data = new InputData { Mouse = new MouseInput { Flags = button == "right" ? (down ? RightDown : RightUp) : (down ? LeftDown : LeftUp) } } });
    static void Scroll(int amount) => Inject(new Input { Type = InputMouse, Data = new InputData { Mouse = new MouseInput { Flags = Wheel, MouseData = unchecked((uint)amount) } } });
    static Input Key(ushort vk, ushort scan, uint flags) => new() { Type = InputKeyboard, Data = new InputData { Keyboard = new KeyboardInput { Vk = vk, Scan = scan, Flags = flags } } };
    static void Inject(params Input[] input) { if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length) throw new Win32Exception("Windows 未接受输入。"); }
    static void SendKey(string name, string[] modifiers) {
        ushort vk = name switch { "Enter" => 0x0D, "Backspace" => 0x08, "Tab" => 0x09, "Esc" => 0x1B, "Space" => 0x20, "Delete" => 0x2E, "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28, "Shift" => 0x10, _ => 0 };
        if (vk == 0 && name.Length == 1) { char c = char.ToUpperInvariant(name[0]); if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') vk = c; }
        if (vk == 0 && name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 12) vk = (ushort)(0x70 + f - 1);
        if (vk == 0) return;
        var active = modifiers.Distinct().Select(m => m switch { "Ctrl" => (ushort)0x11, "Alt" => (ushort)0x12, "Win" => (ushort)0x5B, "Shift" => (ushort)0x10, _ => (ushort)0 }).Where(v => v != 0 && v != vk).ToArray();
        var inputs = new List<Input>(); foreach (var mod in active) inputs.Add(Key(mod, 0, 0));
        inputs.Add(Key(vk, 0, 0)); inputs.Add(Key(vk, 0, KeyUp));
        foreach (var mod in active.Reverse()) inputs.Add(Key(mod, 0, KeyUp));
        Inject(inputs.ToArray());
    }
    static void SendText(string text) { foreach (var c in text) Inject(Key(0, c, Unicode), Key(0, c, Unicode | KeyUp)); }
    static Task<T> Sta<T>(Func<T> action) {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { result.TrySetResult(action()); } catch (Exception e) { result.TrySetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start(); return result.Task;
    }
    public static Task<string> ReadClipboard() => Sta(() => System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : "");
    public static Task<bool> WriteClipboard(string text) => Sta(() => { if (text.Length > 1024 * 1024) throw new ArgumentException("剪切板文本过大。"); if (text.Length == 0) System.Windows.Forms.Clipboard.Clear(); else System.Windows.Forms.Clipboard.SetText(text); return true; });

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputData { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError=true)] static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll", SetLastError=true)] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError=true)] static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
}

using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Http;

static class WindowCatalog
{
    const int GwlExstyle = -20, GwOwner = 4;
    const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
    const uint WmClose = 0x0010;
    public record Window(string Id, uint ProcessId, string Title, string Application);
    public record WindowRequest(string? Id, uint ProcessId);
    delegate bool EnumCallback(IntPtr hwnd, IntPtr param);

    public static IReadOnlyList<Window> List()
    {
        var windows = new List<Window>();
        EnumWindows((hwnd, _) => {
            if (!IsWindowVisible(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
            if ((style & WsExToolWindow) != 0 && (style & WsExAppWindow) == 0) return true;
            if (GetWindow(hwnd, GwOwner) != IntPtr.Zero && (style & WsExAppWindow) == 0) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(Math.Min(length + 1, 4096));
            GetWindowText(hwnd, title, title.Capacity);
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId || pid == 0 || title.Length == 0) return true;
            var name = "应用";
            try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            windows.Add(new Window(hwnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), pid, title.ToString(), name));
            return true;
        }, IntPtr.Zero);
        return windows;
    }
    internal static IntPtr Resolve(WindowRequest request)
    {
        if (!long.TryParse(request.Id, out var id)) return IntPtr.Zero;
        var hwnd = new IntPtr(id);
        if (!IsWindow(hwnd)) return IntPtr.Zero;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != request.ProcessId || pid == 0 || pid == Environment.ProcessId) return IntPtr.Zero;
        return List().Any(w => w.Id == request.Id && w.ProcessId == pid) ? hwnd : IntPtr.Zero;
    }
    public static IResult Activate(WindowRequest request) {
        var hwnd = Resolve(request);
        if (hwnd == IntPtr.Zero) return Results.NotFound(new { error = "窗口已经关闭或不可用" });
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);
        if (GetForegroundWindow() != hwnd && !SetForegroundWindow(hwnd)) {
            // Join the foreground input queue briefly so that a remote selection can
            // activate a window even when this background service has no focus.
            var foreground = GetForegroundWindow();
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
            finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
        }
        // Cross-thread activation is asynchronous; give the target thread time
        // to process the focus change before reporting a failure.
        for (var i = 0; i < 15 && GetForegroundWindow() != hwnd; i++) Thread.Sleep(20);
        if (GetForegroundWindow() != hwnd) return Results.Conflict(new { error = "Windows 未允许激活此窗口，请先在电脑端解锁桌面或切换到该窗口后重试。" });
        return Results.Ok(new { ok = true });
    }
    public static IResult Icon(WindowRequest request) {
        var hwnd = Resolve(request);
        if (hwnd == IntPtr.Zero) return Results.NotFound();
        try {
            IntPtr handle = IntPtr.Zero;
            foreach (var kind in new[] { 2, 0, 1 }) {
                if (SendMessageTimeout(hwnd, 0x007F, new IntPtr(kind), IntPtr.Zero, 0x0002, 150, out handle) != IntPtr.Zero && handle != IntPtr.Zero) break;
            }
            if (handle == IntPtr.Zero) handle = GetClassIcon(hwnd, -34);
            if (handle == IntPtr.Zero) handle = GetClassIcon(hwnd, -14);
            using var icon = handle != IntPtr.Zero ? CloneIcon(handle) : ProcessIcon(request.ProcessId);
            if (icon is null) return Results.NotFound();
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return Results.File(stream.ToArray(), "image/png");
        } catch (Exception e) when (e is ArgumentException or ExternalException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { return Results.NotFound(); }
    }
    static System.Drawing.Icon CloneIcon(IntPtr handle) {
        using var borrowed = System.Drawing.Icon.FromHandle(handle);
        return (System.Drawing.Icon)borrowed.Clone();
    }
    static System.Drawing.Icon? ProcessIcon(uint pid) {
        using var process = Process.GetProcessById((int)pid);
        var path = process.MainModule?.FileName;
        return path is null ? null : System.Drawing.Icon.ExtractAssociatedIcon(path);
    }
    static IntPtr GetClassIcon(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetClassLongPtr64(hwnd, index) : new IntPtr(unchecked((int)GetClassLong32(hwnd, index)));
    public static IResult Close(WindowRequest request) {
        var hwnd = Resolve(request);
        if (hwnd == IntPtr.Zero) return Results.NotFound(new { error = "窗口已经关闭或不可用" });
        return PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero) ? Results.Ok(new { ok = true }) : Results.Problem("无法向应用发送关闭请求。", statusCode: 500);
    }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint="GetClassLongPtrW")] static extern IntPtr GetClassLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint="GetClassLongW")] static extern uint GetClassLong32(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="GetWindowTextLengthW")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="GetWindowTextW")] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
}

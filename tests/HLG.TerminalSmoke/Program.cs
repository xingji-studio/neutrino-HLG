using System.Text;
using System.Net.WebSockets;
using System.Drawing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

if (args.Length == 1 && args[0] == "--capture-child") {
    using var childForm = new CaptureTestForm { Text = "HLG isolated capture target", BackColor = Color.Red, Size = new Size(240, 180), StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = true };
    childForm.Shown += (_, _) => Console.WriteLine("HLG_HWND:" + childForm.Handle.ToInt64());
    System.Windows.Forms.Application.Run(childForm);
    return;
}
var windows = WindowCatalog.List();
if (((IStatusCodeHttpResult)WindowCatalog.Activate(new WindowCatalog.WindowRequest("invalid", 0))).StatusCode != 404) throw new Exception("无效窗口未被拒绝。");
Console.WriteLine($"窗口枚举成功（{windows.Count} 个），无效窗口操作已拒绝。");

var shell = TerminalHost.Shells().First(s => s.Id == "cmd");
using var terminal = PseudoConsole.Start(shell.Executable, "/Q /K");
terminal.Resize(90, 30);
var marker = "HLG_" + Guid.NewGuid().ToString("N");
var received = new StringBuilder();
var reader = Task.Run(() => {
    var decoder = Encoding.UTF8.GetDecoder();
    var bytes = new byte[4096];
    var chars = new char[4096];
    var output = new StringBuilder();
    while (true) {
        int n = terminal.Output.Read(bytes, 0, bytes.Length);
        if (n == 0) break;
        int charsRead = decoder.GetChars(bytes, 0, n, chars, 0);
        output.Append(chars, 0, charsRead);
        lock (received) received.Append(chars, 0, charsRead);
        if (output.ToString().Contains(marker, StringComparison.Ordinal)) return true;
        if (output.Length > 1_000_000) break;
    }
    return false;
});
var input = Encoding.UTF8.GetBytes("echo " + marker + "\r\n");
await terminal.Input.WriteAsync(input);
await terminal.Input.FlushAsync();
var completed = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(15)));
if (completed != reader || !await reader) {
    terminal.Stop();
    lock (received) Console.Error.WriteLine("ConPTY 输出: " + received.ToString());
    throw new Exception("ConPTY 未返回交互命令的输出。");
}
terminal.Stop();
Console.WriteLine("ConPTY 交互输入、输出和尺寸调整通过。");

var powershell = TerminalHost.Shells().First(s => s.Id == "powershell");
using (var ps = PseudoConsole.Start(powershell.Executable, "-NoLogo -NoProfile")) {
    var unicodeMarker = "远控测试_" + Guid.NewGuid().ToString("N");
    var psReader = Task.Run(() => {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096]; var chars = new char[4096]; var text = new StringBuilder();
        while (true) {
            var count = ps.Output.Read(bytes, 0, bytes.Length);
            if (count == 0) return false;
            text.Append(chars, 0, decoder.GetChars(bytes, 0, count, chars, 0));
            if (text.ToString().Contains(unicodeMarker, StringComparison.Ordinal)) return true;
            if (text.Length > 1_000_000) return false;
        }
    });
    var command = Encoding.UTF8.GetBytes("Write-Output '" + unicodeMarker + "'\r\n");
    await ps.Input.WriteAsync(command); await ps.Input.FlushAsync();
    if (await Task.WhenAny(psReader, Task.Delay(TimeSpan.FromSeconds(20))) != psReader || !await psReader) {
        ps.Stop(); throw new Exception("PowerShell 中文输入输出测试失败。");
    }
    var childReader = Task.Run(() => {
        var bytes = new byte[4096]; var text = new StringBuilder();
        while (true) {
            var count = ps.Output.Read(bytes, 0, bytes.Length);
            if (count == 0) return 0;
            text.Append(Encoding.UTF8.GetString(bytes, 0, count));
            var match = System.Text.RegularExpressions.Regex.Match(text.ToString(), @"HLGCHILDPID:(\d+)");
            if (match.Success) return int.Parse(match.Groups[1].Value);
            if (text.Length > 1_000_000) return 0;
        }
    });
    var childCommand = Encoding.UTF8.GetBytes("$p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 60' -NoNewWindow -PassThru; Write-Output ('HLGCHILDPID:' + $p.Id)\r\n");
    await ps.Input.WriteAsync(childCommand); await ps.Input.FlushAsync();
    if (await Task.WhenAny(childReader, Task.Delay(TimeSpan.FromSeconds(20))) != childReader || await childReader == 0) {
        ps.Stop(); throw new Exception("无法创建终端子进程以测试清理。");
    }
    var childPid = await childReader;
    using var child = System.Diagnostics.Process.GetProcessById(childPid);
    ps.Stop();
    if (!child.WaitForExit(5000)) throw new Exception("关闭终端后子进程仍在运行。");
}
Console.WriteLine("PowerShell 中文输入输出及子进程清理通过。");

var mappedBounds = new Rectangle(-1920, 100, 1920, 1080);
if (ScreenSession.MapPoint(mappedBounds, 0, 0) != new Point(-1920, 100) || ScreenSession.MapPoint(mappedBounds, 1, 1) != new Point(-1, 1179)) throw new Exception("多显示器坐标映射失败。");
var formReady = new TaskCompletionSource<System.Windows.Forms.Form>(TaskCreationOptions.RunContinuationsAsynchronously);
var ui = new Thread(() => {
    using var form = new CaptureTestForm { Text = "HLG capture test", BackColor = Color.Red, Size = new Size(240, 180), StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
    form.Shown += (_, _) => formReady.TrySetResult(form);
    System.Windows.Forms.Application.Run(form);
});
ui.SetApartmentState(ApartmentState.STA); ui.IsBackground = true; ui.Start();
var testForm = await formReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
try {
    var rect = ScreenSession.Bounds(testForm.Handle);
    var red = false;
    for (var attempt = 0; attempt < 20 && !red; attempt++) {
        var jpeg = ScreenSession.CaptureJpeg(testForm.Handle, rect);
        using var image = new Bitmap(new MemoryStream(jpeg));
        var pixel = image.GetPixel(image.Width / 2, image.Height / 2);
        red = pixel.R >= 150 && pixel.G <= 80 && pixel.B <= 80;
        if (!red) await Task.Delay(100);
    }
    if (!red) throw new Exception("窗口截图未捕获测试窗口内容。");
} finally { testForm.BeginInvoke(testForm.Close); ui.Join(5000); }
Console.WriteLine("窗口捕获和多显示器坐标映射通过。");

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
app.UseWebSockets();
app.MapGet("/api/terminal/connect", TerminalHost.Handle);
app.MapGet("/api/screen/connect", ScreenSession.Handle);
await app.StartAsync();
var origin = app.Urls.Single();
var uri = new Uri(origin.Replace("http://", "ws://") + "/api/terminal/connect?shell=cmd");
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using (var bad = new ClientWebSocket()) {
    bad.Options.SetRequestHeader("Origin", "https://untrusted.example");
    var rejected = false;
    try { await bad.ConnectAsync(uri, cancellation.Token); }
    catch (WebSocketException) { rejected = true; }
    if (!rejected) throw new Exception("跨来源终端连接未被拒绝。");
}
using (var socket = new ClientWebSocket()) {
    socket.Options.SetRequestHeader("Origin", origin);
    await socket.ConnectAsync(uri, cancellation.Token);
    var wsMarker = "WS_" + Guid.NewGuid().ToString("N");
    var payload = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { type = "input", data = "echo " + wsMarker + "\r\n" }));
    await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellation.Token);
    var receivedText = new StringBuilder();
    var buffer = new byte[8192];
    while (!receivedText.ToString().Contains(wsMarker, StringComparison.Ordinal)) {
        var chunk = await socket.ReceiveAsync(buffer.AsMemory(), cancellation.Token);
        if (chunk.MessageType != WebSocketMessageType.Binary) throw new Exception("终端 WebSocket 提前关闭。");
        receivedText.Append(Encoding.UTF8.GetString(buffer, 0, chunk.Count));
    }
    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "测试结束", cancellation.Token);
}
var screenUri = new Uri(origin.Replace("http://", "ws://") + "/api/screen/connect?all=1");
using (var badScreen = new ClientWebSocket()) {
    badScreen.Options.SetRequestHeader("Origin", "https://untrusted.example");
    var rejected = false;
    try { await badScreen.ConnectAsync(screenUri, cancellation.Token); }
    catch (WebSocketException) { rejected = true; }
    if (!rejected) throw new Exception("跨来源画面连接未被拒绝。");
}
using (var screen = new ClientWebSocket()) {
    screen.Options.SetRequestHeader("Origin", origin);
    await screen.ConnectAsync(screenUri, cancellation.Token);
    var buffer = new byte[2_000_000];
    var meta = await screen.ReceiveAsync(buffer.AsMemory(), cancellation.Token);
    if (meta.MessageType != WebSocketMessageType.Text || !Encoding.UTF8.GetString(buffer, 0, meta.Count).Contains("\"type\":\"frame\"")) throw new Exception("画面元数据缺失。");
    var frame = await screen.ReceiveAsync(buffer.AsMemory(), cancellation.Token);
    if (frame.MessageType != WebSocketMessageType.Binary || frame.Count < 2 || buffer[0] != 0xff || buffer[1] != 0xd8) throw new Exception("未收到 JPEG 画面。");
    await screen.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "测试结束", cancellation.Token);
}
var processPath = Environment.ProcessPath ?? throw new Exception("无法启动窗口测试进程。");
var childStart = new System.Diagnostics.ProcessStartInfo(processPath) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) childStart.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
childStart.ArgumentList.Add("--capture-child");
using var captureChild = System.Diagnostics.Process.Start(childStart) ?? throw new Exception("窗口测试进程启动失败。");
try {
    var line = await captureChild.StandardOutput.ReadLineAsync(cancellation.Token);
    if (line is null || !line.StartsWith("HLG_HWND:")) throw new Exception("窗口测试进程没有返回句柄。");
    var windowId = line["HLG_HWND:".Length..];
    var windowUri = new Uri(origin.Replace("http://", "ws://") + "/api/screen/connect?id=" + windowId + "&pid=" + captureChild.Id);
    using var screen = new ClientWebSocket();
    screen.Options.SetRequestHeader("Origin", origin);
    await screen.ConnectAsync(windowUri, cancellation.Token);
    var buffer = new byte[2_000_000];
    var red = false;
    for (var attempt = 0; attempt < 20 && !red; attempt++) {
        var meta = await screen.ReceiveAsync(buffer.AsMemory(), cancellation.Token);
        if (meta.MessageType != WebSocketMessageType.Text || !Encoding.UTF8.GetString(buffer, 0, meta.Count).Contains("\"type\":\"frame\"")) throw new Exception("单窗口画面元数据缺失。");
        using var jpeg = new MemoryStream();
        ValueWebSocketReceiveResult chunk;
        do {
            chunk = await screen.ReceiveAsync(buffer.AsMemory(), cancellation.Token);
            if (chunk.MessageType != WebSocketMessageType.Binary) throw new Exception("单窗口画面意外中断。");
            jpeg.Write(buffer, 0, chunk.Count);
        } while (!chunk.EndOfMessage);
        jpeg.Position = 0;
        using var frame = new Bitmap(jpeg);
        var center = frame.GetPixel(frame.Width / 2, frame.Height / 2);
        red = center.R >= 150 && center.G <= 80 && center.B <= 80;
    }
    if (!red) throw new Exception("发送的单窗口画面不包含指定窗口内容。");
    await screen.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "测试结束", cancellation.Token);
} finally { if (!captureChild.HasExited) captureChild.Kill(true); }
await app.StopAsync(cancellation.Token);
Console.WriteLine("终端、全屏和单窗口 WebSocket 往返及跨来源拒绝通过。");

sealed class CaptureTestForm : System.Windows.Forms.Form { protected override bool ShowWithoutActivation => true; }

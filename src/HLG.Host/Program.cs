using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Win32;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HLG 仅支持 Windows。");
System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
using (var identity = WindowsIdentity.GetCurrent())
{
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定启动程序路径。");
            var elevate = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
                elevate.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("无法确定程序路径。"));
            foreach (var arg in args) elevate.ArgumentList.Add(arg);
            Process.Start(elevate);
        }
        catch (Exception)
        {
            System.Windows.Forms.MessageBox.Show("本程序必须以管理员权限运行，否则可能无法正常访问电脑目录。未获得管理员权限，已拒绝启动。", "Neutrino HLG", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
        return;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var certPath = Environment.GetEnvironmentVariable("HLG_HTTPS_CERT");
var certPassword = Environment.GetEnvironmentVariable("HLG_HTTPS_PASSWORD");
const int port = 8767;
builder.WebHost.ConfigureKestrel(k => HostBinding.Configure(k, port, certPath, certPassword));
var app = builder.Build();
app.UseWebSockets();
var pairingCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
var authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var tokenBytes = Encoding.UTF8.GetBytes(authToken);
var started = DateTimeOffset.UtcNow;
var attempts = 0;
Console.WriteLine($"Neutrino HLG 配对码（10 分钟有效）: {pairingCode}");
Console.WriteLine(string.IsNullOrWhiteSpace(certPath)
    ? $"HTTP 正在 0.0.0.0:{port} 监听。使用电脑的 IPv4 地址从手机访问；连接未加密，请在可信网络中使用或配置 HTTPS。"
    : $"HTTPS 正在 0.0.0.0:{port} 监听。手机访问时需信任证书并使用匹配的地址。");
app.Use(async (context, next) => {
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    if (context.Request.Path == "/api/pair" && context.Request.Method == "POST") { await next(); return; }
    var cookie = context.Request.Cookies["hlg_session"] ?? "";
    var given = Encoding.UTF8.GetBytes(cookie);
    if (given.Length != tokenBytes.Length || !CryptographicOperations.FixedTimeEquals(given, tokenBytes)) { context.Response.StatusCode = 401; return; }
    if (context.Request.Method is not "GET" and not "HEAD")
    {
        var origin = context.Request.Headers.Origin.ToString();
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
    }
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/terminal/shells", () => Results.Ok(TerminalHost.Shells().Select(s => new { s.Id, s.Name })));
app.MapGet("/api/terminal/connect", TerminalHost.Handle);
app.MapGet("/api/windows", () => Results.Ok(WindowCatalog.List()));
app.MapGet("/api/windows/icon", (string id, uint processId) => WindowCatalog.Icon(new WindowCatalog.WindowRequest(id, processId)));
app.MapPost("/api/windows/activate", (WindowCatalog.WindowRequest request) => WindowCatalog.Activate(request));
app.MapPost("/api/windows/close", (WindowCatalog.WindowRequest request) => WindowCatalog.Close(request));
app.MapGet("/api/screen/connect", ScreenSession.Handle);
app.MapGet("/api/clipboard", async () => Results.Ok(new { text = await ScreenSession.ReadClipboard() }));
app.MapPost("/api/clipboard", async (ClipboardRequest request) => { await ScreenSession.WriteClipboard(request.Text ?? ""); return Results.Ok(new { ok = true }); });
app.MapPost("/api/pair", async (HttpContext c) => {
    if (c.Request.Headers.Origin.ToString() != $"{c.Request.Scheme}://{c.Request.Host}") return Results.StatusCode(403);
    if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(10) || Interlocked.Increment(ref attempts) > 10) return Results.StatusCode(429);
    var request = await c.Request.ReadFromJsonAsync<PairRequest>();
    var supplied = Encoding.UTF8.GetBytes(request?.Code ?? "");
    var expected = Encoding.UTF8.GetBytes(pairingCode);
    if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) return Results.Unauthorized();
    c.Response.Cookies.Append("hlg_session", authToken, new CookieOptions { HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Strict, IsEssential = true });
    return Results.Ok(new { ok = true });
});
app.MapGet("/api/status", () => Results.Ok(new { desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), machine = Environment.MachineName }));
app.MapGet("/api/fs", (string? path) => {
    try {
        if (string.IsNullOrWhiteSpace(path)) return Results.Ok(new { roots = DriveInfo.GetDrives().Select(d => new { name = d.Name, type = d.DriveType.ToString(), ready = d.IsReady }) });
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) return Results.NotFound(new { error = "目录不存在或不可访问" });
        var entries = dir.EnumerateFileSystemInfos().Select(f => new { name = f.Name, path = f.FullName, directory = f.Attributes.HasFlag(FileAttributes.Directory), hidden = f.Attributes.HasFlag(FileAttributes.Hidden) }).OrderByDescending(f => f.directory).ThenBy(f => f.name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        return Results.Ok(new { path = dir.FullName, parent = dir.Parent?.FullName, entries });
    } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return Results.Problem(e.Message, statusCode: 400); }
});
var appService = new AppService(
    new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) },
    new AppUsage(Path.Combine(NeutrinoPaths.Home, "hlg", "app-usage.json")));
app.MapGet("/api/apps", () => Results.Ok(appService.List()));
app.MapGet("/api/apps/icon", (string? id, string? path) => appService.Icon(id, path));
app.MapPost("/api/apps/open", (AppRequest request) => appService.Open(request.Id, request.Path));
var bridge = new NeutrinoBridge();
var store = new TaskStore(bridge);
var conversations = new ConversationService(bridge, store);
app.MapGet("/api/neutrino/models", () => {
    try { return Results.Ok(NeutrinoModels.Read(NeutrinoPaths.Home)); }
    catch (FileNotFoundException) { return Results.Problem("找不到 Neutrino 的 model.json，请先在 Neutrino 中配置模型。", statusCode: 503); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return Results.Problem("无法读取 Neutrino 模型配置，请检查 model.json 的格式和访问权限。", statusCode: 503); }
});
app.MapGet("/api/neutrino/capabilities", async () => await bridge.Forward(HttpMethod.Get, "/capabilities"));
app.MapGet("/api/tasks", () => Results.Ok(store.List()));
app.MapGet("/api/tasks/{taskId}", async (string taskId) => await conversations.Get(taskId));
app.MapPost("/api/tasks/{taskId}/message", async (string taskId, JsonObject body) => await conversations.Send(taskId, body));
app.MapPost("/api/tasks/{taskId}/approval/{id}", async (string taskId, string id, JsonObject body) => await conversations.Approve(taskId, id, body));
app.MapPost("/api/tasks", async (JsonObject body) => {
    var text = body["text"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "请输入任务" });
    var create = new JsonObject { ["cwd"] = body["cwd"]?.DeepClone(), ["selection"] = body["selection"]?.DeepClone(), ["agent_mode"] = body["agent_mode"]?.DeepClone() };
    foreach (var key in create.Where(kv => kv.Value is null).Select(kv => kv.Key).ToArray()) create.Remove(key);
    using var result = await bridge.Request(HttpMethod.Post, "/conversations", create);
    if (!result.IsSuccessStatusCode) return Results.Content(await result.Content.ReadAsStringAsync(), "application/json", statusCode: (int)result.StatusCode);
    var created = JsonNode.Parse(await result.Content.ReadAsStringAsync());
    var sessionKey = created?["key"]?.GetValue<string>() ?? throw new InvalidOperationException("Neutrino 未返回会话标识");
    var taskId = store.Track(sessionKey, text, create, created?["snapshot"]);
    using var sent = await bridge.Request(HttpMethod.Post, "/live/" + Uri.EscapeDataString(sessionKey) + "/message", new JsonObject { ["text"] = text });
    if (!sent.IsSuccessStatusCode) return Results.Content(await sent.Content.ReadAsStringAsync(), "application/json", statusCode: (int)sent.StatusCode);
    if (created is JsonObject response) response["taskId"] = taskId;
    return Results.Json(created);
});
app.Run();

record PairRequest(string? Code);
record AppRequest(string? Path, string? Id);
record ClipboardRequest(string? Text);
sealed class NeutrinoBridge : IConversationBackend
{
    readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
    public async Task<HttpResponseMessage> Request(HttpMethod method, string path, JsonObject? body = null)
    {
        var discovery = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(NeutrinoPaths.Home, "api.json"))) ?? throw new InvalidOperationException("Neutrino API 未启动");
        var baseUri = new Uri(discovery["url"]!.GetValue<string>());
        if (baseUri.Scheme != "http" || baseUri.Host != "127.0.0.1") throw new InvalidOperationException("无效的 Neutrino 本地地址");
        using var request = new HttpRequestMessage(method, baseUri.ToString().TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", discovery["token"]!.GetValue<string>());
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
    public async Task<IResult> Forward(HttpMethod method, string path) {
        try { using var response = await Request(method, path); return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode); }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or InvalidOperationException) { return Results.Problem("Neutrino 本机 API 不可用：" + e.Message, statusCode: 503); }
    }
}

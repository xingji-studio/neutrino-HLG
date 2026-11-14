using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

static class HostApplication
{
    public static WebApplication Create(string[] args, FileLog log, PairingAuth pairing, SecureDesktopHub secureDesktop)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        var certPath = Environment.GetEnvironmentVariable("HLG_HTTPS_CERT");
        var certPassword = Environment.GetEnvironmentVariable("HLG_HTTPS_PASSWORD");
        const int port = 8767;
        builder.WebHost.ConfigureKestrel(k => HostBinding.Configure(k, port, certPath, certPassword));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        var app = builder.Build();
        app.UseWebSockets();
        app.Use((HttpContext context, RequestDelegate next) => pairing.Handle(context, next));
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });
        app.MapGet("/api/terminal/shells", () => Results.Ok(TerminalHost.Shells().Select(s => new { s.Id, s.Name })));
        app.MapGet("/api/terminal/connect", TerminalHost.Handle);
        app.MapGet("/api/windows", () => Results.Ok(WindowCatalog.List()));
        app.MapGet("/api/windows/icon", (string id, uint processId) => WindowCatalog.Icon(new WindowCatalog.WindowRequest(id, processId)));
        app.MapPost("/api/windows/activate", (WindowCatalog.WindowRequest request) => WindowCatalog.Activate(request));
        app.MapPost("/api/windows/close", (WindowCatalog.WindowRequest request) => WindowCatalog.Close(request));
        app.MapGet("/api/screen/connect", (HttpContext context) => ScreenSession.Handle(context, () => secureDesktop.LockState == SessionLockState.Unlocked));
        app.MapGet("/api/screen/secure/connect", (HttpContext context) => SecureScreenSession.Handle(context, secureDesktop));
        app.MapGet("/api/clipboard", async () => Results.Ok(new { text = await ScreenSession.ReadClipboard() }));
        app.MapPost("/api/clipboard", async (ClipboardRequest request) => { await ScreenSession.WriteClipboard(request.Text ?? ""); return Results.Ok(new { ok = true }); });
        app.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)pairing.Pair);
        app.MapPost("/api/trust", (Func<HttpContext, Task<IResult>>)pairing.Trust);
        app.MapPost("/api/trust/restore", (Func<HttpContext, Task<IResult>>)pairing.Restore);
        app.MapPost("/api/trust/approval", (Func<HttpContext, Task<IResult>>)pairing.RequestApproval);
        app.MapPost("/api/trust/approval/status", (Func<HttpContext, Task<IResult>>)pairing.ApprovalStatus);
        app.MapPost("/api/trust/approval/cancel", (Func<HttpContext, Task<IResult>>)pairing.CancelApproval);
        app.MapGet("/api/presence/connect", pairing.Presence);
        app.MapGet("/api/status", () => Results.Ok(new { desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), machine = Environment.MachineName, locked = secureDesktop.LockState switch { SessionLockState.Locked => (bool?)true, SessionLockState.Unlocked => false, _ => null }, unlockAvailable = secureDesktop.Available }));
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
        return app;
    }
}

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

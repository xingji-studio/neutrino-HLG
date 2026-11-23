using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
if (args.FirstOrDefault() == "--fake") { await FakeRuntime.Serve(args); return; }
if (args.FirstOrDefault() == "--idle") { await Task.Delay(Timeout.Infinite); return; }
var real = args.Contains("--real");
var root = Path.Combine(AppContext.BaseDirectory, "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var folder = Path.Combine(root, "测试 空间 #1"); Directory.CreateDirectory(folder);
await File.WriteAllTextAsync(Path.Combine(folder, "hello.txt"), "HLG VS Code integration test\n");
using var certificate = new LocalHttpsCertificate(Path.Combine(root, "https"));
using var log = new FileLog(Path.Combine(root, "logs"));
var fake = new FakeRuntime();
var data = real ? Path.Combine(AppContext.BaseDirectory, "real-runtime") : Path.Combine(root, "vscode");
using var service = new VsCodeService(data, log, real ? new RealTestRuntime() : fake, TimeSpan.FromMinutes(10));
if (real) foreach (var old in service.List()) await service.Stop(old.Id, CancellationToken.None);
var devices = new DeviceConnections();
var pairing = new PairingAuth(new TrustedDevices(Path.Combine(root, "trusted.json")), devices);
var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
builder.Services.AddHttpForwarder(); builder.Services.AddSingleton(log); builder.Services.AddSingleton(service);
builder.Services.AddHostedService(provider => provider.GetRequiredService<VsCodeService>()); builder.Services.AddSingleton<VsCodeProxy>();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate.ForAddress(new IPEndPoint(IPAddress.Loopback, 0)))));
await using var app = builder.Build();
app.UseWebSockets(); app.Use((HttpContext context, RequestDelegate next) => pairing.Handle(context, next));
var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
using var webroot = new PhysicalFileProvider(Path.Combine(repo, "src", "HLG.Host", "wwwroot"));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webroot }); app.UseStaticFiles(new StaticFileOptions { FileProvider = webroot });
app.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)pairing.Pair);
app.MapGet("/api/status", () => Results.Ok(new { desktop = root, machine = "VS CODE TEST", locked = false }));
app.MapGet("/api/apps", () => Results.Ok(Array.Empty<object>()));
app.MapGet("/api/windows", () => Results.Ok(new[] { new { id = "123", processId = 456, title = "VS Code 桌面窗口", application = "Code" } }));
app.MapGet("/api/neutrino/models", () => Results.Ok(new { providers = Array.Empty<object>() }));
app.MapGet("/api/fs", (string? path) => Results.Ok(new { path = path ?? root, parent = Directory.GetParent(path ?? root)?.FullName, entries = new[] { new { name = Path.GetFileName(folder), path = folder, directory = true } } }));
app.MapGet("/api/presence/connect", pairing.Presence);
VsCodeProxy.Map(app);
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var origin = address.TrimEnd('/');
try {
    if (real) {
        Console.WriteLine("Testing installed official VS Code through HLG HTTPS. Logs: " + log.Path);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { Path.Combine(repo, "tests", "vscode-real-ui.cjs"), origin, pairing.PairingCode, folder, root }) start.ArgumentList.Add(value);
        using var browser = Process.Start(start)!;
        async Task Print(StreamReader reader) { while (await reader.ReadLineAsync() is { } line) Console.WriteLine(line); }
        var output = Task.WhenAll(Print(browser.StandardOutput), Print(browser.StandardError));
        await browser.WaitForExitAsync(); await output;
        Check(browser.ExitCode == 0, "Official VS Code browser integration failed. Logs: " + log.Path);
    } else {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == certificate.ForAddress(new IPEndPoint(IPAddress.Loopback, 0)).Thumbprint };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(origin) };
        client.DefaultRequestHeaders.Add("Origin", origin);
        using var unauthenticated = await client.GetAsync("/api/vscode/web/missing/");
        Check(unauthenticated.StatusCode == HttpStatusCode.Unauthorized, "editor bypassed pairing");
        using var pair = await client.PostAsJsonAsync("/api/pair", new { code = pairing.PairingCode }); Check(pair.IsSuccessStatusCode, "pairing failed");
        using var license = await client.PostAsJsonAsync("/api/vscode/open", new { path = folder }); Check((int)license.StatusCode == 428, "missing first-use license consent");
        using var relative = await client.PostAsJsonAsync("/api/vscode/open", new { path = "relative", acceptLicense = true }); Check(relative.StatusCode == HttpStatusCode.BadRequest, "relative folder accepted");
        using var opened = await client.PostAsJsonAsync("/api/vscode/open", new { path = folder, acceptLicense = true });
        Check(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
        var session = (await opened.Content.ReadFromJsonAsync<VsCodeSession>())!;
        var watch = Stopwatch.StartNew();
        while (service.Get(session.Id)?.State == "starting" && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
        Check(service.Get(session.Id)?.State == "ready", "fake server failed: " + service.Get(session.Id)?.Message);
        using var duplicate = await client.PostAsJsonAsync("/api/vscode/open", new { path = folder.ToUpperInvariant() + Path.DirectorySeparatorChar, openDesktop = false });
        Check((await duplicate.Content.ReadFromJsonAsync<VsCodeSession>())?.Id == session.Id && fake.DesktopOpens == 1 && fake.Starts == 1, "folder was not reused");
        using var html = await client.GetAsync(session.Url);
        Check(html.IsSuccessStatusCode && (await html.Content.ReadAsStringAsync()).Contains("vscode-workbench-web-configuration"), "workbench was not forwarded");
        Check((await html.Content.ReadAsStringAsync()).Contains("Default Dark Modern"), "official theme defaults were not applied");
        Check(html.Headers.GetValues("Set-Cookie").All(c => c.Contains("path=" + session.Url, StringComparison.OrdinalIgnoreCase) && c.Contains("secure", StringComparison.OrdinalIgnoreCase)), "cookies were not isolated");
        using var echo = await client.GetAsync(session.Url + "echo?file=a%20b%23c");
        var info = await echo.Content.ReadFromJsonAsync<JsonElement>();
        Check(info.GetProperty("host").GetString() == new Uri(origin).Authority && info.GetProperty("proto").GetString() == "https", "external address was lost");
        Check(!info.GetProperty("cookie").GetString()!.Contains("hlg_session") && info.GetProperty("cookie").GetString()!.Contains("vscode-tkn="), "pairing cookie leaked to upstream");
        Check(info.GetProperty("query").GetString() == "?file=a%20b%23c", "file query changed");
        async Task TestSocket(Version version) {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", origin);socket.Options.SetRequestHeader("Cookie", handler.CookieContainer.GetCookieHeader(new Uri(origin + session.Url)));
            socket.Options.HttpVersion = version; socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            using var invoker = new HttpMessageInvoker(new SocketsHttpHandler { UseProxy = false, UseCookies = false, SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, c, _, _) => c?.GetCertHashString() == certificate.ForAddress(new IPEndPoint(IPAddress.Loopback, 0)).Thumbprint } });
            await socket.ConnectAsync(new Uri(origin.Replace("https:", "wss:") + session.Url + "socket"), invoker, CancellationToken.None);
            await socket.SendAsync(Encoding.UTF8.GetBytes("editor input"), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[128]; var response = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Check(Encoding.UTF8.GetString(buffer, 0, response.Count) == "editor input", "WebSocket forwarding failed");
            Check(devices.List().Single().Editor, "editor device is not tracked");
            devices.Disconnect(devices.List().Single().Id, false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await socket.ReceiveAsync(buffer, timeout.Token); } catch (WebSocketException) { }
            Check(!timeout.IsCancellationRequested, "device disconnect left editor connected");
            using var paired = await client.PostAsJsonAsync("/api/pair", new { code = pairing.PairingCode }); Check(paired.IsSuccessStatusCode, "re-pairing failed");
        }
        await TestSocket(HttpVersion.Version11); await TestSocket(HttpVersion.Version20);
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "https://elsewhere.invalid");
        using var crossOrigin = await client.PostAsJsonAsync("/api/vscode/open", new { path = folder }); Check(crossOrigin.StatusCode == HttpStatusCode.Forbidden, "cross-origin start accepted");
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", origin);
        using (var restored = new VsCodeService(data, log, fake)) Check(restored.Get(session.Id)?.State == "stopped", "persisted entry adopted stale process");
        using var stopped = await client.DeleteAsync("/api/vscode/sessions/" + session.Id); Check(stopped.IsSuccessStatusCode && service.Target(session.Id) is null && service.Get(session.Id) is null, "stop did not remove editor");
        using var missing = await client.GetAsync(session.Url); Check(missing.StatusCode == HttpStatusCode.NotFound, "stopped entry still proxies");
        Check(!File.ReadAllText(log.Path).Contains(File.ReadAllText(Path.Combine(data, "workspaces", "workspace-" + session.Id, "connection-token"))), "token leaked into logs");
        var childId = int.Parse(await File.ReadAllTextAsync(Path.Combine(data, "workspaces", "workspace-" + session.Id, "connection-token.child")));
        try { using var child = Process.GetProcessById(childId); Check(child.HasExited || child.WaitForExit(5000), "stop left the server's child process running"); } catch (ArgumentException) { }
        var secondFolder = Path.Combine(root, "second"); Directory.CreateDirectory(secondFolder);
        using var second = await client.PostAsJsonAsync("/api/vscode/open", new { path = secondFolder, openDesktop = false });
        var secondSession = (await second.Content.ReadFromJsonAsync<VsCodeSession>())!;
        watch.Restart(); while (service.Get(secondSession.Id)?.State == "starting" && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
        Check(service.Get(secondSession.Id)?.State == "ready" && fake.DesktopOpens == 1, "web-only reopen launched a native window");
        await service.StopAsync(CancellationToken.None);
        Check(service.Get(secondSession.Id)?.State == "stopped" && service.Target(secondSession.Id) is null, "host shutdown left editor running");
        Console.WriteLine("VS Code lifecycle, license, reuse, proxy, cookies, HTTP/1+2 WebSockets, device disconnect and persistence passed.");
    }
} finally { await app.StopAsync(); }

sealed class FakeRuntime : IVsCodeRuntime
{
    public int Starts, DesktopOpens;
    public Task<VsCodeInstallation> Locate(CancellationToken cancellationToken) => Task.FromResult(new VsCodeInstallation("fake", "fake", "1.0.0", new string('a', 40)));
    public void OpenDesktop(VsCodeInstallation installation, string directory) => DesktopOpens++;
    public Process StartWeb(VsCodeInstallation installation, VsCodeStartOptions options) {
        Starts++; var start = VsCodeRuntime.Hidden(Environment.ProcessPath!);
        foreach (var value in new[] { "--fake", options.TokenFile, options.BasePath }) start.ArgumentList.Add(value);
        return Process.Start(start)!;
    }
    public static async Task Serve(string[] args) {
        var token = File.ReadAllText(args[1]); var path = args[2];
        var childStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };childStart.ArgumentList.Add("--idle");
        using var child = Process.Start(childStart)!; await File.WriteAllTextAsync(args[1] + ".child", child.Id.ToString());
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build(); app.UseWebSockets();
        app.Use(async (context, next) => { if (context.Request.Cookies["vscode-tkn"] != token || !context.Request.Path.StartsWithSegments(path.TrimEnd('/'))) { context.Response.StatusCode = 403; return; } await next(context); });
        app.Map(path, (HttpContext c) => {
            c.Response.Cookies.Append("vscode-secret-key-path", "/mint-key", new CookieOptions { Path = "/" });
            c.Response.Cookies.Append("vscode-cli-secret-half", "secret", new CookieOptions { Path = "/", HttpOnly = true });
            return Results.Content("<html><meta id='vscode-workbench-web-configuration' data-settings='{&quot;remoteAuthority&quot;:&quot;test&quot;}'></html>", "text/html");
        });
        app.MapGet(path + "echo", (HttpContext c) => Results.Ok(new { host = c.Request.Host.Value, proto = c.Request.Headers["X-Forwarded-Proto"].ToString(), cookie = c.Request.Headers.Cookie.ToString(), query = c.Request.QueryString.Value }));
        app.Map(path + "socket", async (HttpContext c) => {
            if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
            using var socket = await c.WebSockets.AcceptWebSocketAsync(); var buffer = new byte[1024];
            try { while (socket.State == WebSocketState.Open) { var message = await socket.ReceiveAsync(buffer, c.RequestAborted); if (message.MessageType == WebSocketMessageType.Close) break; await socket.SendAsync(buffer.AsMemory(0, message.Count), message.MessageType, message.EndOfMessage, c.RequestAborted); } } catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
        });
        await app.StartAsync(); Console.WriteLine(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()); await app.WaitForShutdownAsync();
    }
}

// Test the real official server without opening a visible native window.
sealed class RealTestRuntime : IVsCodeRuntime
{
    readonly VsCodeRuntime runtime = new();
    public Task<VsCodeInstallation> Locate(CancellationToken token) => runtime.Locate(token);
    public Process StartWeb(VsCodeInstallation installation, VsCodeStartOptions options) => runtime.StartWeb(installation, options);
    public void OpenDesktop(VsCodeInstallation installation, string directory) { }
}

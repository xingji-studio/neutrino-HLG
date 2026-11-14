using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var root = Path.Combine(AppContext.BaseDirectory, "pairing-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    var path = Path.Combine(root, "trusted.json");
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    string? previousCookie = null;
    for (var run = 0; run < 2; run++) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var devices = new DeviceConnections();
        var pairing = new PairingAuth(new TrustedDevices(path), devices);
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Use((HttpContext context, RequestDelegate next) => pairing.Handle(context, next));
        app.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)pairing.Pair);
        app.MapPost("/api/trust", (Func<HttpContext, Task<IResult>>)pairing.Trust);
        app.MapPost("/api/trust/restore", (Func<HttpContext, Task<IResult>>)pairing.Restore);
        app.MapGet("/api/presence/connect", pairing.Presence);
        app.MapGet("/api/screen/connect", pairing.Presence);
        app.MapGet("/api/protected", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/socket", async (HttpContext context) => {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
        });
        await app.StartAsync();
        try {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { UseCookies = false, UseProxy = false }) { BaseAddress = new Uri(address) };
            async Task<HttpResponseMessage> Send(string route, object? body = null, string? cookie = null, string? origin = null) {
                using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, route);
                if (body is not null) request.Content = JsonContent.Create(body);
                if (cookie is not null) request.Headers.Add("Cookie", cookie);
                if (origin is not null) request.Headers.Add("Origin", origin);
                return await client.SendAsync(request);
            }
            using (var unauth = await Send("/api/protected", cookie: previousCookie)) Check(unauth.StatusCode == HttpStatusCode.Unauthorized, "new session must not accept old cookie");
            using (var crossPair = await Send("/api/pair", new { code = pairing.PairingCode }, origin: "https://other.example")) Check(crossPair.StatusCode == HttpStatusCode.Forbidden, "cross-origin pair must fail");
            if (run == 0) {
                using var pair = await Send("/api/pair", new { code = pairing.PairingCode }, origin: address);
                Check(pair.StatusCode == HttpStatusCode.OK, "pairing failed");
                previousCookie = pair.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
                using (var denied = await Send("/api/trust", new { token }, cookie: previousCookie, origin: "https://other.example")) Check(denied.StatusCode == HttpStatusCode.Forbidden, "cross-origin trust must fail");
                using (var invalid = await Send("/api/trust", new { token = "bad" }, cookie: previousCookie, origin: address)) Check(invalid.StatusCode == HttpStatusCode.BadRequest, "invalid token must fail");
                using (var trusted = await Send("/api/trust", new { token }, cookie: previousCookie, origin: address)) Check(trusted.StatusCode == HttpStatusCode.OK, "trust registration failed");
                Check(!File.ReadAllText(path).Contains(token, StringComparison.OrdinalIgnoreCase), "raw token stored on host");
                using var secondPair = await Send("/api/pair", new { code = pairing.PairingCode }, origin: address);
                var secondCookie = secondPair.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
                Check(previousCookie != secondCookie, "devices must receive independent sessions");
                async Task<ClientWebSocket> Connect(string cookie, string route, string agent) {
                    var ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("Origin", address); ws.Options.SetRequestHeader("Cookie", cookie); ws.Options.SetRequestHeader("User-Agent", agent);
                    await ws.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = route }.Uri, CancellationToken.None);
                    return ws;
                }
                using var firstSocket = await Connect(previousCookie, "/api/presence/connect", "iPhone Safari");
                using var firstScreen = await Connect(previousCookie, "/api/screen/connect", "iPhone Safari");
                using var secondSocket = await Connect(secondCookie, "/api/presence/connect", "Android Chrome");
                for (var attempt = 0; attempt < 30 && devices.List().Count < 2; attempt++) await Task.Delay(50);
                var list = devices.List();
                Check(list.Count == 2, "separate devices must appear independently");
                var firstDevice = list.Single(d => d.Trusted);
                Check(firstDevice.Screen && firstDevice.Name == "iPhone", "device activity or identity missing");
                devices.Disconnect(firstDevice.Id);
                async Task CheckClosed(ClientWebSocket socket) {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { var message = await socket.ReceiveAsync(new byte[64].AsMemory(), timeout.Token); Check(message.MessageType == WebSocketMessageType.Close, "disconnected socket remained open"); }
                    catch (WebSocketException) { }
                    catch (OperationCanceledException) { throw new Exception("disconnected socket did not close"); }
                }
                await CheckClosed(firstSocket); await CheckClosed(firstScreen);
                using (var closed = await Send("/api/protected", cookie: previousCookie)) Check(closed.StatusCode == HttpStatusCode.Unauthorized, "disconnected device remained authenticated");
                using (var other = await Send("/api/protected", cookie: secondCookie)) Check(other.StatusCode == HttpStatusCode.OK, "disconnect affected another device");
                using (var blocked = await Send("/api/trust/restore", new { token }, origin: address)) Check(blocked.StatusCode == HttpStatusCode.Forbidden, "disconnected trusted device reconnected immediately");
                devices.Allow(firstDevice.Id);
                using var allowed = await Send("/api/trust/restore", new { token }, origin: address);
                Check(allowed.StatusCode == HttpStatusCode.OK, "allowed device could not reconnect");
                var restoredCookie = allowed.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
                pairing.Revoke(firstDevice.Id);
                using (var revoked = await Send("/api/trust/restore", new { token }, origin: address)) Check(revoked.StatusCode == HttpStatusCode.Unauthorized, "revoked device remained trusted");
                using (var revokedSession = await Send("/api/protected", cookie: restoredCookie)) Check(revokedSession.StatusCode == HttpStatusCode.Unauthorized, "revoked session remained authenticated");
                using var pairAgain = await Send("/api/pair", new { code = pairing.PairingCode }, origin: address);
                var againCookie = pairAgain.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
                using var trustAgain = await Send("/api/trust", new { token }, cookie: againCookie, origin: address);
                Check(trustAgain.StatusCode == HttpStatusCode.OK, "re-establish trust for restart test");
            } else {
                using (var crossRestore = await Send("/api/trust/restore", new { token }, origin: "https://other.example")) Check(crossRestore.StatusCode == HttpStatusCode.Forbidden, "cross-origin restore must fail");
                using (var invalid = await Send("/api/trust/restore", new { token = new string('A', 64) }, origin: address)) Check(invalid.StatusCode == HttpStatusCode.Unauthorized, "unknown device restored");
                using var restored = await Send("/api/trust/restore", new { token }, origin: address);
                Check(restored.StatusCode == HttpStatusCode.OK, "trusted device did not survive restart");
                var cookie = restored.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
                using var accessible = await Send("/api/protected", cookie: cookie);
                Check(accessible.StatusCode == HttpStatusCode.OK, "restored session cannot access API");
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Origin", address);
                socket.Options.SetRequestHeader("Cookie", cookie);
                var socketAddress = new UriBuilder(address) { Scheme = "ws", Path = "/api/socket" }.Uri;
                await socket.ConnectAsync(socketAddress, CancellationToken.None);
                Check(socket.State is WebSocketState.Open or WebSocketState.CloseReceived, "restored session cannot connect to WebSocket");
            }
        } finally { await app.StopAsync(); }
    }
    File.WriteAllText(path, "invalid json");
    var broken = new TrustedDevices(path);
    Check(!broken.Available && !broken.IsTrusted(token), "invalid trust database must fail closed");
    try { broken.Add(token); throw new Exception("invalid trust database was overwritten"); }
    catch (InvalidDataException) { }
    Check(File.ReadAllText(path) == "invalid json", "invalid database was changed");
    var brokenPairing = new PairingAuth(broken);
    var context = new DefaultHttpContext();
    context.Request.ContentType = "application/json";
    context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { token })));
    var result = await brokenPairing.Trust(context);
    Check((result as IStatusCodeHttpResult)?.StatusCode == 503, "broken trust database must return a recoverable error");
    Console.WriteLine("Pairing, origin checks, persistent trust and restart restoration passed.");
} finally { Directory.Delete(root, true); }

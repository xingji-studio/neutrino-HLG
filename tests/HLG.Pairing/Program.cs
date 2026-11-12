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
        var pairing = new PairingAuth(new TrustedDevices(path));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Use((HttpContext context, RequestDelegate next) => pairing.Handle(context, next));
        app.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)pairing.Pair);
        app.MapPost("/api/trust", (Func<HttpContext, Task<IResult>>)pairing.Trust);
        app.MapPost("/api/trust/restore", (Func<HttpContext, Task<IResult>>)pairing.Restore);
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

using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var directory = Path.Combine(AppContext.BaseDirectory, "https-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try {
    byte[] rootBytes;
    using (var first = new LocalHttpsCertificate(directory)) {
        rootBytes = first.RootCertificate;
        Check(first.Fingerprint.Replace(":", "") == Convert.ToHexString(SHA256.HashData(rootBytes)), "root fingerprint did not match");
        var file = File.ReadAllBytes(Path.Combine(directory, "root-ca.dpapi"));
        Check(!file.AsSpan().SequenceEqual(rootBytes), "CA file contains the public certificate instead of protected key material");
        var pfx = ProtectedData.Unprotect(file, Encoding.UTF8.GetBytes("Neutrino HLG local HTTPS CA v1"), DataProtectionScope.CurrentUser);
        try { using var restored = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet); Check(restored.HasPrivateKey && restored.RawData.SequenceEqual(rootBytes), "protected CA did not round-trip"); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        var issued = first.ForAddress(new IPEndPoint(IPAddress.Parse("192.0.2.41"), 8767));
        var san = issued.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Check(san.EnumerateIPAddresses().Contains(IPAddress.Parse("192.0.2.41")) && san.EnumerateIPAddresses().Contains(IPAddress.Loopback), "server certificate has incorrect IP names");
        Check(!issued.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority, "server certificate can issue certificates");
        Check(issued.Extensions.Cast<X509Extension>().Any(e => e.Oid?.Value == "2.5.29.35"), "server certificate is missing the authority key identifier");
        Parallel.For(0, 16, _ => Check(ReferenceEquals(issued, first.ForAddress(new IPEndPoint(IPAddress.Parse("192.0.2.41"), 8767))), "certificate cache is not thread safe"));
    }
    using var certificate = new LocalHttpsCertificate(directory);
    Check(certificate.RootCertificate.SequenceEqual(rootBytes), "CA changed across restarts");
    using var root = X509CertificateLoader.LoadCertificate(rootBytes);
    Check(!root.HasPrivateKey && root.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority, "exported root must contain only a public CA certificate");
    var profile = XDocument.Parse(Encoding.UTF8.GetString(CertificateSetup.AppleProfile(certificate)));
    Check(Convert.FromBase64String(profile.Descendants("data").Single().Value).SequenceEqual(rootBytes), "iPhone profile contains the wrong certificate");
    var corruptPath = Path.Combine(directory, "corrupt"); Directory.CreateDirectory(corruptPath);
    var corruptFile = Path.Combine(corruptPath, "root-ca.dpapi"); File.WriteAllText(corruptFile, "not a certificate");
    try { using var ignored = new LocalHttpsCertificate(corruptPath); throw new Exception("corrupt CA was silently replaced"); }
    catch (CryptographicException) { Check(File.ReadAllText(corruptFile) == "not a certificate", "corrupt CA was overwritten"); }

    bool Validate(X509Certificate2? server, SslPolicyErrors errors) {
        if (server is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(server);
    }
    var devices = new DeviceConnections();
    var pairing = new PairingAuth(new TrustedDevices(Path.Combine(directory, "trusted.json")), devices);
    using var log = new FileLog(Path.Combine(directory, "logs"));
    using var secureHub = new SecureDesktopHub(new UnlockSettings(Path.Combine(directory, "unlock.json")), log, true);
    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(k => HttpsHosting.Configure(k, 0, 0, certificate, null, null));
    await using var app = builder.Build();
    app.Use((HttpContext context, RequestDelegate next) => HttpsHosting.Dispatch(context, next, certificate));
    app.UseWebSockets();
    app.Use((HttpContext context, RequestDelegate next) => pairing.Handle(context, next));
    var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    using var webroot = new PhysicalFileProvider(Path.Combine(repository, "src", "HLG.Host", "wwwroot"));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webroot });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webroot });
    app.MapGet("/probe", (HttpContext c) => Results.Ok(new { secure = c.Request.IsHttps }));
    app.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)pairing.Pair);
    app.MapGet("/api/status", () => Results.Ok(new { desktop = "C:\\", machine = "HLG TEST", locked = false, unlockAvailable = false }));
    app.MapGet("/api/apps", () => Results.Ok(Array.Empty<object>()));
    app.MapGet("/api/windows", () => Results.Ok(Array.Empty<object>()));
    app.MapGet("/api/neutrino/models", () => Results.Ok(new { providers = Array.Empty<object>() }));
    app.MapGet("/api/test/devices", () => Results.Ok(devices.List()));
    WebSocketEndpoints.Map(app, "/api/presence/connect", pairing.Presence);
    WebSocketEndpoints.Map(app, "/api/screen/connect", ScreenSession.Handle);
    WebSocketEndpoints.Map(app, "/api/terminal/connect", TerminalHost.Handle);
    WebSocketEndpoints.Map(app, "/api/screen/secure/connect", context => SecureScreenSession.Handle(context, secureHub));
    WebSocketEndpoints.Map(app, "/socket", async (HttpContext c) => {
        if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
        using var socket = await c.WebSockets.AcceptWebSocketAsync();
        await socket.SendAsync(Encoding.UTF8.GetBytes("encrypted"), WebSocketMessageType.Text, true, c.RequestAborted);
    });
    await app.StartAsync();
    try {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(a => new Uri(a)).ToArray();
        Check(addresses.Length == 2 && addresses.All(a => a.Host == "0.0.0.0"), "listeners must bind all IPv4 interfaces");
        var http = new UriBuilder(addresses.Single(a => a.Scheme == "http")) { Host = "127.0.0.1" }.Uri;
        var https = new UriBuilder(addresses.Single(a => a.Scheme == "https")) { Host = "127.0.0.1" }.Uri;
        var httpOnHttpsPort = new UriBuilder(https) { Scheme = "http" }.Uri;
        using (var redirects = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })) {
            using var redirect = await redirects.GetAsync(new Uri(httpOnHttpsPort, "/documents/test?q=one%20two"));
            Check(redirect.StatusCode == HttpStatusCode.TemporaryRedirect && redirect.Headers.Location?.ToString() == new Uri(https, "/documents/test?q=one%20two").ToString(), "HTTP on the HTTPS port did not redirect while preserving the path and query");
            const string encodedPath = "/%E6%B5%8B%E8%AF%95/a%2Fb?x=%2B%2F";
            using var encoded = await redirects.GetAsync(new Uri(httpOnHttpsPort, encodedPath));
            Check(encoded.StatusCode == HttpStatusCode.TemporaryRedirect && encoded.Headers.Location?.AbsoluteUri == new Uri(https, encodedPath).AbsoluteUri, "redirect changed an encoded path or query");
            using var head = await redirects.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri(httpOnHttpsPort, "/")));
            Check(head.StatusCode == HttpStatusCode.TemporaryRedirect && head.Headers.Location == https, "HEAD did not redirect to HTTPS");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(httpOnHttpsPort, "/api/status"));
            request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("Cookie", "hlg_session=invalid");
            using var api = await redirects.SendAsync(request);
            Check(api.StatusCode == HttpStatusCode.TemporaryRedirect && !api.Headers.Contains("Set-Cookie") && (await api.Content.ReadAsByteArrayAsync()).Length == 0, "plaintext HTTP reached an application API");
            using var forwarded = new HttpRequestMessage(HttpMethod.Get, new Uri(httpOnHttpsPort, "/shared?view=1"));
            forwarded.Headers.Host = "remote.example:12345";
            using var forwardedResult = await redirects.SendAsync(forwarded);
            Check(forwardedResult.StatusCode == HttpStatusCode.TemporaryRedirect && forwardedResult.Headers.Location == new Uri("https://remote.example:12345/shared?view=1"), "redirect did not preserve the requested host and port");
            using var post = await redirects.PostAsync(new Uri(httpOnHttpsPort, "/api/pair"), JsonContent.Create(new { code = pairing.PairingCode }));
            Check(post.StatusCode == HttpStatusCode.MethodNotAllowed && !post.Headers.Contains("Set-Cookie"), "plaintext pairing was accepted");
        }
        using (var split = new TcpClient()) {
            await split.ConnectAsync(IPAddress.Loopback, https.Port);
            await using var stream = split.GetStream();
            await stream.WriteAsync("G"u8.ToArray()); await stream.FlushAsync(); await Task.Delay(50);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"ET /split HTTP/1.1\r\nHost: 127.0.0.1:{https.Port}\r\nConnection: close\r\n\r\n")); await stream.FlushAsync();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var splitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await reader.ReadToEndAsync(splitTimeout.Token);
            Check(response.StartsWith("HTTP/1.1 307 ", StringComparison.Ordinal) && response.Contains($"Location: https://127.0.0.1:{https.Port}/split", StringComparison.OrdinalIgnoreCase), "a fragmented HTTP request did not redirect");
        }
        using var noTrust = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        try { await noTrust.GetAsync(new Uri(https, "/probe")); throw new Exception("untrusted root was accepted"); }
        catch (HttpRequestException) { }
        using var handler = new HttpClientHandler { UseProxy = false, UseCookies = false, ServerCertificateCustomValidationCallback = (_, server, _, errors) => Validate(server, errors) };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        Check((await client.GetStringAsync(new Uri(https, "/probe"))).Contains("true"), "TLS request did not reach protected endpoint");
        using (var followed = await client.GetAsync(new Uri(httpOnHttpsPort, "/probe?redirect=test"))) {
            Check(followed.IsSuccessStatusCode && followed.RequestMessage?.RequestUri?.Scheme == "https" && (await followed.Content.ReadAsStringAsync()).Contains("true"), "HTTP redirect was not followed to the encrypted endpoint");
        }
        using (var response = await client.GetAsync(new Uri(https, "/neutrino-hlg-root.cer"))) Check(response.StatusCode == HttpStatusCode.NotFound, "setup routes leaked into application endpoint");
        var setup = await client.GetStringAsync(http);
        Check(setup.Contains(certificate.Fingerprint) && setup.Contains("iPhone") && setup.Contains("Android"), "setup page is incomplete");
        Check((await client.GetByteArrayAsync(new Uri(http, "/neutrino-hlg-root.cer"))).SequenceEqual(rootBytes), "downloaded CA does not match trusted root");
        Check((await client.GetByteArrayAsync(new Uri(http, "/neutrino-hlg.mobileconfig"))).SequenceEqual(CertificateSetup.AppleProfile(certificate)), "downloaded profile does not match root");
        using (var response = await client.GetAsync(new Uri(http, "/api/probe"))) Check(response.StatusCode == HttpStatusCode.NotFound, "plain HTTP exposed an API");
        using (var response = await client.PostAsync(new Uri(http, "/api/pair"), new StringContent("{}"))) Check(response.StatusCode == HttpStatusCode.MethodNotAllowed, "plain HTTP exposed pairing");
        using (var response = await client.GetAsync(new Uri(http, "/api/screen/secure/connect"))) Check(response.StatusCode == HttpStatusCode.NotFound, "plain HTTP exposed the secure screen");
        using var websocket = new ClientWebSocket();
        websocket.Options.RemoteCertificateValidationCallback = (_, server, _, errors) => Validate(server is null ? null : new X509Certificate2(server), errors);
        await websocket.ConnectAsync(new UriBuilder(https) { Scheme = "wss", Path = "/socket" }.Uri, CancellationToken.None);
        var buffer = new byte[32]; var frame = await websocket.ReceiveAsync(buffer, CancellationToken.None);
        Check(Encoding.UTF8.GetString(buffer, 0, frame.Count) == "encrypted", "secure WebSocket failed");
        using var websocket2 = new ClientWebSocket();
        websocket2.Options.HttpVersion = HttpVersion.Version20;
        websocket2.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        using var http2Handler = new SocketsHttpHandler { UseProxy = false, SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, server, _, errors) => Validate(server is null ? null : new X509Certificate2(server), errors) } };
        using var http2 = new HttpMessageInvoker(http2Handler);
        await websocket2.ConnectAsync(new UriBuilder(https) { Scheme = "wss", Path = "/socket" }.Uri, http2, CancellationToken.None);
        var frame2 = await websocket2.ReceiveAsync(buffer, CancellationToken.None);
        Check(Encoding.UTF8.GetString(buffer, 0, frame2.Count) == "encrypted", "HTTP/2 secure WebSocket failed");

        // Exercise the same authentication, origin checks, and routing used by
        // the real host over both WebSocket transports.
        var origin = https.GetLeftPart(UriPartial.Authority);
        using var pairRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(https, "/api/pair")) { Content = JsonContent.Create(new { code = pairing.PairingCode }) };
        pairRequest.Headers.Add("Origin", origin);
        using var paired = await client.SendAsync(pairRequest);
        Check(paired.IsSuccessStatusCode, "HTTPS pairing failed");
        var cookie = paired.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using (var plainClient = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })) {
            using var stolenCookie = new HttpRequestMessage(HttpMethod.Get, new Uri(httpOnHttpsPort, "/api/test/devices"));
            stolenCookie.Headers.Add("Cookie", cookie); stolenCookie.Headers.Add("X-Forwarded-Proto", "https");
            using var denied = await plainClient.SendAsync(stolenCookie);
            Check(denied.StatusCode == HttpStatusCode.TemporaryRedirect && (await denied.Content.ReadAsByteArrayAsync()).Length == 0, "an authenticated session exposed data over plaintext HTTP");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task<ClientWebSocket> Connect(string path, Version version, string? sessionCookie, string source) {
            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", source);
            ws.Options.SetRequestHeader("User-Agent", "Android Chrome");
            if (sessionCookie is not null) ws.Options.SetRequestHeader("Cookie", sessionCookie);
            var destination = new UriBuilder(https) { Scheme = "wss", Path = path.Split('?')[0], Query = path.Contains('?') ? path.Split('?', 2)[1] : "" }.Uri;
            try {
                if (version == HttpVersion.Version20) {
                    ws.Options.HttpVersion = HttpVersion.Version20; ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                    await ws.ConnectAsync(destination, http2, timeout.Token);
                } else {
                    ws.Options.RemoteCertificateValidationCallback = (_, server, _, errors) => Validate(server is null ? null : new X509Certificate2(server), errors);
                    await ws.ConnectAsync(destination, timeout.Token);
                }
                return ws;
            } catch { ws.Dispose(); throw; }
        }
        async Task Reject(string? sessionCookie, string source, string expectedStatus) {
            try { using var unexpected = await Connect("/api/presence/connect", HttpVersion.Version20, sessionCookie, source); throw new Exception("invalid HTTP/2 WebSocket was accepted"); }
            catch (WebSocketException e) { Check(e.Message.Contains(expectedStatus, StringComparison.Ordinal), "unexpected HTTP/2 rejection: " + e.Message); }
        }
        await Reject(null, origin, "401");
        await Reject(cookie, "https://untrusted.example", "403");
        using (var presence1 = await Connect("/api/presence/connect", HttpVersion.Version11, cookie, origin)) {
            Check(presence1.State == WebSocketState.Open && devices.List().Any(d => d.Online && d.Name == "Android 设备"), "HTTP/1.1 paired device was not tracked");
            await presence1.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", timeout.Token);
        }
        using (var presence2 = await Connect("/api/presence/connect", HttpVersion.Version20, cookie, origin)) {
            await Task.Delay(200, timeout.Token);
            Check(presence2.State == WebSocketState.Open && devices.List().Any(d => d.Online && d.Name == "Android 设备"), "HTTP/2 paired device disappeared");
            using (var screen = await Connect("/api/screen/connect?all=1", HttpVersion.Version20, cookie, origin)) {
                var largeBuffer = new byte[2_000_000];
                var meta = await screen.ReceiveAsync(largeBuffer.AsMemory(), timeout.Token);
                Check(meta.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(largeBuffer, 0, meta.Count).Contains("\"type\":\"frame\""), "HTTP/2 screen metadata missing");
                var jpeg = await screen.ReceiveAsync(largeBuffer.AsMemory(), timeout.Token);
                Check(jpeg.MessageType == WebSocketMessageType.Binary && jpeg.Count >= 2 && largeBuffer[0] == 0xff && largeBuffer[1] == 0xd8, "HTTP/2 screen JPEG missing");
                Check(devices.List().Any(d => d.Screen), "HTTP/2 screen connection not shown in device list");
                await screen.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", timeout.Token);
            }
            using (var terminal = await Connect("/api/terminal/connect?shell=cmd", HttpVersion.Version20, cookie, origin)) {
                var marker = "HLG_HTTPS_" + Guid.NewGuid().ToString("N");
                var input = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { type = "input", data = "echo " + marker + "\r\n" });
                await terminal.SendAsync(input, WebSocketMessageType.Text, true, timeout.Token);
                var output = new StringBuilder(); var chunk = new byte[8192];
                while (!output.ToString().Contains(marker, StringComparison.Ordinal)) {
                    var received = await terminal.ReceiveAsync(chunk.AsMemory(), timeout.Token);
                    Check(received.MessageType == WebSocketMessageType.Binary, "HTTP/2 terminal closed early");
                    output.Append(Encoding.UTF8.GetString(chunk, 0, received.Count));
                }
                Check(devices.List().Any(d => d.Terminal), "HTTP/2 terminal connection not shown in device list");
                await terminal.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", timeout.Token);
            }
            // With unlock disabled, the real handler must return 503 (not the
            // 405 routing failure) without locking the machine or starting a service.
            try { using var unexpected = await Connect("/api/screen/secure/connect", HttpVersion.Version20, cookie, origin); throw new Exception("disabled secure screen unexpectedly connected"); }
            catch (WebSocketException e) { Check(e.Message.Contains("503", StringComparison.Ordinal), "secure screen did not reach its HTTP/2 handler: " + e.Message); }
            await presence2.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test", timeout.Token);
        }
        if (args.Contains("--browser", StringComparer.Ordinal)) {
            var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            if (!File.Exists(node)) node = "node";
            var start = new ProcessStartInfo(node) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(repository, "tests", "https-websocket-ui.cjs"));
            start.ArgumentList.Add(origin); start.ArgumentList.Add(pairing.PairingCode);
            using var process = Process.Start(start) ?? throw new Exception("Unable to start HTTPS browser test.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var browserTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(browserTimeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw new Exception("HTTPS browser test timed out."); }
            var browserOutput = await stdout; var browserError = await stderr;
            if (process.ExitCode != 0) throw new Exception("HTTPS browser test failed: " + browserError + browserOutput);
            Console.Write(browserOutput);
        }
        var lan = NetworkInterface.GetAllNetworkInterfaces().Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        if (lan is not null) {
            Check((await client.GetStringAsync(new UriBuilder(https) { Host = lan.ToString(), Path = "/probe" }.Uri)).Contains("true"), "LAN TLS certificate did not match its IP address");
            using var redirected = await client.GetAsync(new UriBuilder(httpOnHttpsPort) { Host = lan.ToString(), Path = "/probe" }.Uri);
            Check(redirected.IsSuccessStatusCode && redirected.RequestMessage?.RequestUri?.Scheme == "https" && (await redirected.Content.ReadAsStringAsync()).Contains("true"), "LAN HTTP did not redirect to the correct HTTPS endpoint");
        }
    } finally { await app.StopAsync(); }

    // A user-supplied PFX continues to use HTTPS without the setup listener.
    using var manualKey = RSA.Create(2048);
    var manualRequest = new CertificateRequest("CN=localhost", manualKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var manualNames = new SubjectAlternativeNameBuilder(); manualNames.AddIpAddress(IPAddress.Loopback); manualRequest.CertificateExtensions.Add(manualNames.Build());
    using var manualCert = manualRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    var manualPath = Path.Combine(directory, "custom.pfx"); await File.WriteAllBytesAsync(manualPath, manualCert.Export(X509ContentType.Pfx, "test"));
    var manualBuilder = WebApplication.CreateSlimBuilder(); manualBuilder.Logging.ClearProviders();
    manualBuilder.WebHost.ConfigureKestrel(k => HttpsHosting.Configure(k, 0, 0, null, manualPath, "test"));
    await using var manualApp = manualBuilder.Build();
    manualApp.Use((HttpContext context, RequestDelegate next) => HttpsHosting.Dispatch(context, next, null));
    manualApp.MapGet("/", () => "manual");
    await manualApp.StartAsync();
    try {
        var manualAddresses = manualApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(a => new Uri(a)).ToArray();
        Check(manualAddresses.Length == 1 && manualAddresses[0].Scheme == "https", "custom certificate unexpectedly started plain HTTP");
        using var manualHandler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, server, _, errors) => server is not null && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && server.RawData.SequenceEqual(manualCert.RawData) };
        using var manualClient = new HttpClient(manualHandler);
        var manualUrl = new UriBuilder(manualAddresses[0]) { Host = "127.0.0.1" }.Uri;
        Check(await manualClient.GetStringAsync(manualUrl) == "manual", "custom HTTPS certificate did not work");
        var manualHttp = new UriBuilder(manualUrl) { Scheme = "http" }.Uri;
        using (var redirects = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })) {
            using var redirect = await redirects.GetAsync(new Uri(manualHttp, "/custom?test=1"));
            Check(redirect.StatusCode == HttpStatusCode.TemporaryRedirect && redirect.Headers.Location == new Uri(manualUrl, "/custom?test=1"), "custom certificate mode did not redirect HTTP");
        }
        using (var followed = await manualClient.GetAsync(manualHttp)) Check(followed.IsSuccessStatusCode && followed.RequestMessage?.RequestUri?.Scheme == "https", "custom certificate redirect failed");
    } finally { await manualApp.StopAsync(); }
    Console.WriteLine("Automatic CA persistence, IP certificates, trusted HTTPS/WSS and setup-only HTTP passed.");
} finally { Directory.Delete(directory, true); }

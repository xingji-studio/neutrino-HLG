using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;

sealed class VsCodeProxy(VsCodeService sessions, IHttpForwarder forwarder, FileLog log) : IDisposable
{
    readonly HttpMessageInvoker client = new(new SocketsHttpHandler {
        UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10)
    });
    static readonly ForwarderRequestConfig config = new() {
        ActivityTimeout = TimeSpan.FromMinutes(10), Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    public static void Map(IEndpointRouteBuilder endpoints) {
        endpoints.MapGet("/api/vscode/sessions", (VsCodeService service) => Results.Ok(service.List()));
        endpoints.MapGet("/api/vscode/sessions/{id}", (string id, VsCodeService service) => service.Get(id) is { } session ? Results.Ok(session) : Results.NotFound(new { error = "网页入口不存在。" }));
        endpoints.MapPost("/api/vscode/open", async (OpenRequest request, VsCodeService service, HttpContext context) => {
            try { return Results.Ok(await service.Open(request.Path, request.AcceptLicense, request.OpenDesktop, context.RequestAborted)); }
            catch (VsCodeLicenseRequiredException) { return Results.Json(new { licenseRequired = true, error = "首次使用需要同意 Microsoft VS Code Server 许可条款。" }, statusCode: 428); }
            catch (DirectoryNotFoundException e) { return Results.NotFound(new { error = e.Message }); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "无法访问该文件夹或 VS Code 数据目录。" }, statusCode: 403); }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return Results.Json(new { error = e.Message }, statusCode: 503); }
        });
        endpoints.MapDelete("/api/vscode/sessions/{id}", async (string id, VsCodeService service, HttpContext context) => {
            try { return await service.Stop(id, context.RequestAborted) ? Results.Ok(new { ok = true }) : Results.NotFound(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException) { return Results.Json(new { error = "停止网页服务失败：" + e.Message }, statusCode: 503); }
        });
        // The official workbench uses HTTP resources, POST endpoints and WebSockets
        // (including extended CONNECT over HTTP/2). All go through PairingAuth.
        endpoints.Map("/api/vscode/web/{id}/{**path}", (HttpContext context, VsCodeProxy proxy) => proxy.Forward(context));
    }
    public sealed record OpenRequest(string? Path, bool AcceptLicense = false, bool OpenDesktop = true);

    public async Task Forward(HttpContext context) {
        var id = context.Request.RouteValues["id"]?.ToString() ?? "";
        var target = sessions.Target(id);
        if (target is null) { context.Response.StatusCode = sessions.Get(id) is null ? 404 : 503; await context.Response.WriteAsJsonAsync(new { error = "VS Code 网页服务未就绪，请返回后重试。" }); return; }
        if (context.Request.Path.Value == target.BasePath.TrimEnd('/')) { context.Response.Redirect(target.BasePath + context.Request.QueryString, permanent: false); return; }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, target.Stopping);
        var error = await forwarder.SendAsync(context, target.Address.AbsoluteUri, client, config, new WorkbenchTransform(target), stop.Token);
        if (error != ForwarderError.None && !stop.IsCancellationRequested)
            log.Write(LogLevel.Warning, "VS Code 转发", "请求失败：" + error + "；入口 " + id);
    }
    public void Dispose() => client.Dispose();

    sealed class WorkbenchTransform(VsCodeTarget target) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken cancellationToken) {
            await base.TransformRequestAsync(context, request, destinationPrefix, cancellationToken);
            // Never send HLG pairing/trust cookies or arbitrary authorization to
            // the editor. Only the HLG-managed loopback server gets this token.
            request.Headers.Remove("Authorization"); request.Headers.Remove("Cookie");
            var cookies = new List<string> { "vscode-tkn=" + target.Token };
            foreach (var name in new[] { "vscode-secret-key-path", "vscode-cli-secret-half" })
                if (context.Request.Cookies.TryGetValue(name, out var value)) cookies.Add(name + "=" + value);
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
            request.Headers.Host = context.Request.Host.Value;
            foreach (var name in new[] { "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port" }) request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
            if (context.Request.Path.Value == target.BasePath) request.Headers.Remove("Accept-Encoding");
        }
        public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken cancellationToken) {
            if (response?.IsSuccessStatusCode == true && context.Request.Path.Value == target.BasePath && response.Content.Headers.ContentType?.MediaType == "text/html") {
                // Use the official workbench's supported construction options.
                // Only set defaults; saved editor preferences still take priority.
                // No script or workbench assets are rewritten (CSP stays intact).
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                var meta = Regex.Match(html, "<meta\\s[^>]*\\bid=[\"']vscode-workbench-web-configuration[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var settings = Regex.Match(meta.Value, "\\bdata-settings=(?<quote>[\"'])(?<json>.*?)\\k<quote>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
                if (settings.Success && JsonNode.Parse(WebUtility.HtmlDecode(settings.Groups["json"].Value)) is JsonObject configuration) {
                    var defaults = configuration["configurationDefaults"] as JsonObject ?? new JsonObject();
                    defaults["workbench.colorTheme"] = "Default Dark Modern";
                    defaults["window.autoDetectColorScheme"] = false;
                    defaults["workbench.secondarySideBar.defaultVisibility"] = "hidden";
                    if (configuration["configurationDefaults"] is null) configuration["configurationDefaults"] = defaults;
                    var value = settings.Groups["json"]; var index = meta.Index + value.Index;
                    html = html[..index] + WebUtility.HtmlEncode(configuration.ToJsonString()) + html[(index + value.Length)..];
                    var original = response.Content; response.Content = new StringContent(html, Encoding.UTF8, "text/html"); original.Dispose();
                    response.Headers.Remove("ETag");
                }
            }
            var copy = await base.TransformResponseAsync(context, response, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            if (response is null) return copy;
            if (response.Headers.TryGetValues("Set-Cookie", out var values)) {
                var cookies = new List<string>();
                foreach (var value in values) {
                    if (!SetCookieHeaderValue.TryParse(value, out var cookie) || !cookie.Name.ToString().StartsWith("vscode-", StringComparison.Ordinal)) continue;
                    cookie.Path = target.BasePath; cookie.Domain = default; cookie.Secure = context.Request.IsHttps;
                    cookies.Add(cookie.ToString());
                }
                context.Response.Headers.SetCookie = cookies.ToArray();
            }
            if (response.Headers.Location is { IsAbsoluteUri: true } location && location.Authority == target.Address.Authority)
                context.Response.Headers.Location = context.Request.Scheme + "://" + context.Request.Host + location.PathAndQuery + location.Fragment;
            if (context.Response.Headers.ContainsKey("Service-Worker-Allowed")) context.Response.Headers["Service-Worker-Allowed"] = target.BasePath;
            return copy;
        }
    }
}

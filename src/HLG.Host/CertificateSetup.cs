using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;

static class CertificateSetup
{
    public const int Port = 8768;

    public static byte[] AppleProfile(LocalHttpsCertificate certificate) {
        var root = certificate.RootCertificate;
        var hash = SHA256.HashData(root);
        var profileId = new Guid(hash.AsSpan(0, 16)).ToString().ToUpperInvariant();
        var payloadId = new Guid(hash.AsSpan(16, 16)).ToString().ToUpperInvariant();
        var machine = SecurityElement.Escape(Environment.MachineName) ?? "Neutrino HLG";
        var profile = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
            <key>PayloadType</key><string>Configuration</string>
            <key>PayloadVersion</key><integer>1</integer>
            <key>PayloadIdentifier</key><string>com.xingjisoft.neutrino-hlg.{profileId}</string>
            <key>PayloadUUID</key><string>{profileId}</string>
            <key>PayloadDisplayName</key><string>Neutrino HLG · {machine}</string>
            <key>PayloadDescription</key><string>信任此电脑的 Neutrino HLG 本地 HTTPS 证书。</string>
            <key>PayloadContent</key><array><dict>
            <key>PayloadType</key><string>com.apple.security.root</string>
            <key>PayloadVersion</key><integer>1</integer>
            <key>PayloadIdentifier</key><string>com.xingjisoft.neutrino-hlg.root.{payloadId}</string>
            <key>PayloadUUID</key><string>{payloadId}</string>
            <key>PayloadDisplayName</key><string>Neutrino HLG · {machine}</string>
            <key>PayloadCertificateFileName</key><string>neutrino-hlg-root.cer</string>
            <key>PayloadContent</key><data>{Convert.ToBase64String(root)}</data>
            </dict></array></dict></plist>
            """;
        return Encoding.UTF8.GetBytes(profile);
    }

    public static async Task Handle(HttpContext context, LocalHttpsCertificate certificate) {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) { context.Response.StatusCode = 405; return; }
        byte[] body;
        if (context.Request.Path == "/neutrino-hlg-root.cer") {
            context.Response.ContentType = "application/x-x509-ca-cert";
            context.Response.Headers.ContentDisposition = "attachment; filename=neutrino-hlg-root.cer";
            body = certificate.RootCertificate;
        } else if (context.Request.Path == "/neutrino-hlg.mobileconfig") {
            context.Response.ContentType = "application/x-apple-aspen-config";
            context.Response.Headers.ContentDisposition = "attachment; filename=neutrino-hlg.mobileconfig";
            body = AppleProfile(certificate);
        } else if (context.Request.Path == "/" || context.Request.Path == "/setup") {
            context.Response.ContentType = "text/html; charset=utf-8";
            var host = context.Request.Host.Host;
            var url = Uri.CheckHostName(host) == UriHostNameType.Unknown ? "" : new UriBuilder("https", host, 8767).Uri.ToString();
            var appLink = HtmlEncoder.Default.Encode(url);
            var page = $$"""
                <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Neutrino HLG · 首次连接</title>
                <style>body{background:#111416;color:#eff5f5;font:16px/1.6 system-ui;margin:0;padding:24px}main{max-width:560px;margin:auto}h1{font-size:24px}h2{font-size:18px;margin-top:28px}a.button{display:inline-block;background:#45acd7;color:#10232b;border-radius:12px;padding:11px 15px;text-decoration:none;font-weight:600}code{display:block;background:#202527;border-radius:10px;padding:12px;overflow-wrap:anywhere;font-size:13px}p{color:#c5d0d0}.note{color:#f0c78a}</style></head><body><main>
                <h1>首次连接 Neutrino HLG</h1><p>先在手机上安装并信任这台电脑的本地证书，以后连接通常无需重复安装。</p>
                <p class="note">此安装页面未加密。安装前，请在手机的证书详情中核对 SHA-256 指纹与电脑端一致；如果无法核对，请从电脑导出证书并通过 USB 等可信方式传到手机。</p>
                <code>{{certificate.Fingerprint}}</code>
                <h2>Android</h2><p><a class="button" href="/neutrino-hlg-root.cer">下载 Android 证书</a></p><p>在系统设置中找到“加密与凭据 → 安装证书 → CA 证书”，选择下载的文件。不同品牌的菜单名称可能不同。</p>
                <h2>iPhone / iPad</h2><p><a class="button" href="/neutrino-hlg.mobileconfig">下载 iPhone 描述文件</a></p><p>在系统设置中安装已下载的描述文件，再到“通用 → 关于本机 → 证书信任设置”启用此根证书的完全信任。</p>
                <h2>连接电脑</h2><p>完成信任后，用同一浏览器打开电脑端显示的 HTTPS 地址并输入配对码。</p><p><a class="button" href="{{appLink}}">打开 Neutrino HLG</a></p>
                </main></body></html>
                """;
            body = Encoding.UTF8.GetBytes(page);
        } else { context.Response.StatusCode = 404; return; }
        context.Response.ContentLength = body.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }
}

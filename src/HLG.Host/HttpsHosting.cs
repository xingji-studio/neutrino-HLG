using System.Buffers;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;

static class HttpsHosting
{
    sealed class RedirectConnectionFeature { public static readonly RedirectConnectionFeature Instance = new(); }

    public static void Configure(KestrelServerOptions server, int httpsPort, int setupPort, LocalHttpsCertificate? automatic, string? certificatePath, string? certificatePassword) {
        if (automatic is null && string.IsNullOrWhiteSpace(certificatePath)) throw new InvalidOperationException("未配置 HTTPS 证书。");
        server.Listen(IPAddress.Any, httpsPort, listener => {
            ConnectionDelegate? plainHttp = null;
            // Capture Kestrel's HTTP handler after its TLS middleware, so a
            // plain request can receive a redirect on the same TCP port.
            listener.Use(tls => {
                var http = plainHttp ?? throw new InvalidOperationException("无法配置 HTTP 跳转。");
                return connection => SelectProtocol(connection, tls, http);
            });
            if (automatic is not null) listener.UseHttps(https => https.ServerCertificateSelector = (connection, _) => automatic.ForAddress(connection?.LocalEndPoint));
            else listener.UseHttps(certificatePath!, certificatePassword);
            listener.Use(next => { plainHttp = next; return next; });
        });
        if (automatic is not null) server.Listen(IPAddress.Any, setupPort);
    }

    static byte FirstByte(ReadOnlySequence<byte> buffer) {
        foreach (var segment in buffer) if (!segment.IsEmpty) return segment.Span[0];
        throw new InvalidOperationException("连接没有数据。");
    }

    static async Task SelectProtocol(ConnectionContext connection, ConnectionDelegate tls, ConnectionDelegate http) {
        bool isTls;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed)) {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try {
                while (true) {
                    var result = await connection.Transport.Input.ReadAsync(timeout.Token);
                    var buffer = result.Buffer;
                    if (buffer.IsEmpty) {
                        connection.Transport.Input.AdvanceTo(buffer.Start, buffer.End);
                        if (result.IsCompleted || result.IsCanceled) return;
                        continue;
                    }
                    isTls = FirstByte(buffer) == 0x16;
                    connection.Transport.Input.AdvanceTo(buffer.Start, buffer.Start);
                    break;
                }
            } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return; }
        }
        if (isTls) await tls(connection);
        else { connection.Features.Set(RedirectConnectionFeature.Instance); await http(connection); }
    }

    public static async Task Dispatch(HttpContext context, RequestDelegate next, LocalHttpsCertificate? automatic) {
        if (context.Features.Get<RedirectConnectionFeature>() is not null) {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) { context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed; context.Response.Headers.Allow = "GET, HEAD"; return; }
            var host = context.Request.Host.Host;
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.CacheControl = "no-store";
            var port = context.Request.Host.Port ?? context.Connection.LocalPort;
            context.Response.Headers.Location = UriHelper.BuildAbsolute("https", new HostString(host, port), context.Request.PathBase, context.Request.Path, context.Request.QueryString);
            return;
        }
        if (context.Request.IsHttps && context.Features.Get<ITlsHandshakeFeature>() is not null) { await next(context); return; }
        // Plain HTTP is limited to the public certificate and setup page.
        if (automatic is null) context.Response.StatusCode = 404;
        else await CertificateSetup.Handle(context, automatic);
    }
}

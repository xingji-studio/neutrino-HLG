using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

static class HostBinding
{
    public static void Configure(KestrelServerOptions server, int port, string? certPath, string? certPassword)
    {
        server.Listen(IPAddress.Any, port, listener => {
            if (!string.IsNullOrWhiteSpace(certPath)) listener.UseHttps(certPath, certPassword);
        });
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

static class WebSocketEndpoints
{
    // HTTP/1.1 upgrades a GET; HTTP/2 uses an extended CONNECT request.
    public static void Map(IEndpointRouteBuilder endpoints, string path, RequestDelegate handler) =>
        endpoints.MapMethods(path, new[] { HttpMethods.Get, HttpMethods.Connect }, handler);
}

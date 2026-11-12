using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

sealed class PairingAuth
{
    readonly TrustedDevices trusted;
    readonly string session = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    int attempts;
    public string PairingCode { get; } = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    public PairingAuth(TrustedDevices trusted) { this.trusted = trusted; }
    static bool SameOrigin(HttpContext c) => string.Equals(c.Request.Headers.Origin.ToString(), $"{c.Request.Scheme}://{c.Request.Host}", StringComparison.OrdinalIgnoreCase);
    void SetSession(HttpContext c) => c.Response.Cookies.Append("hlg_session", session, new CookieOptions { HttpOnly = true, Secure = c.Request.IsHttps, SameSite = SameSiteMode.Strict, IsEssential = true });
    static void LimitBody(HttpContext c) { var limit = c.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 1024; }

    public async Task Handle(HttpContext context, RequestDelegate next) {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        var path = context.Request.Path;
        if (context.Request.Method == "POST" && (path == "/api/pair" || path == "/api/trust/restore")) { await next(context); return; }
        var supplied = Encoding.UTF8.GetBytes(context.Request.Cookies["hlg_session"] ?? "");
        var expected = Encoding.UTF8.GetBytes(session);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { context.Response.StatusCode = 401; return; }
        if (context.Request.Method is not "GET" and not "HEAD" && !SameOrigin(context)) { context.Response.StatusCode = 403; return; }
        await next(context);
    }
    public async Task<IResult> Pair(HttpContext c) {
        if (!SameOrigin(c)) return Results.StatusCode(403);
        if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(10) || Interlocked.Increment(ref attempts) > 10) return Results.StatusCode(429);
        LimitBody(c);
        var request = await c.Request.ReadFromJsonAsync<PairRequest>();
        var supplied = Encoding.UTF8.GetBytes(request?.Code ?? "");
        var expected = Encoding.UTF8.GetBytes(PairingCode);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) return Results.Unauthorized();
        SetSession(c);
        return Results.Ok(new { ok = true });
    }
    public async Task<IResult> Trust(HttpContext c) {
        LimitBody(c);
        var request = await c.Request.ReadFromJsonAsync<TrustRequest>();
        try {
            trusted.Add(request?.Token ?? "");
            return Results.Ok(new { ok = true });
        } catch (ArgumentException) { return Results.BadRequest(new { error = "无效的设备凭据。" }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException) { return Results.Problem("无法保存设备信任记录。", statusCode: 503); }
    }
    public async Task<IResult> Restore(HttpContext c) {
        if (!SameOrigin(c)) return Results.StatusCode(403);
        if (!trusted.Available) return Results.Problem("无法读取设备信任记录。", statusCode: 503);
        LimitBody(c);
        var request = await c.Request.ReadFromJsonAsync<TrustRequest>();
        if (!trusted.IsTrusted(request?.Token)) return Results.Unauthorized();
        SetSession(c);
        return Results.Ok(new { ok = true });
    }
    public record PairRequest(string? Code);
    public record TrustRequest(string? Token);
}

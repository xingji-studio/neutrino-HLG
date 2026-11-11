using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Verify(string? certificate, string? password) {
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(k => HostBinding.Configure(k, 0, certificate, password));
    await using var app = builder.Build();
    app.MapGet("/", () => "ok");
    await app.StartAsync();
    try {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var address = new Uri(addresses.Single());
        Check(address.Host == "0.0.0.0", "not bound to all IPv4 interfaces: " + address);
        Check(address.Scheme == (certificate is null ? "http" : "https"), "wrong protocol");
        using var handler = new HttpClientHandler { UseProxy = false };
        if (certificate is not null) handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var local = new UriBuilder(address) { Host = "127.0.0.1" }.Uri;
        Check(await client.GetStringAsync(local) == "ok", "loopback request failed");
        var lan = NetworkInterface.GetAllNetworkInterfaces().Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        if (lan is not null) {
            var remote = new UriBuilder(address) { Host = lan.ToString() }.Uri;
            Check(await client.GetStringAsync(remote) == "ok", "network interface request failed");
            Console.WriteLine($"{address.Scheme.ToUpperInvariant()} binding and local network interface passed.");
        } else Console.WriteLine($"{address.Scheme.ToUpperInvariant()} binding and loopback passed; no active LAN IPv4 found.");
    } finally { await app.StopAsync(); }
}
await Verify(null, null);
using var rsa = RSA.Create(2048);
var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var subjectNames = new SubjectAlternativeNameBuilder(); subjectNames.AddDnsName("localhost"); subjectNames.AddIpAddress(IPAddress.Loopback);
request.CertificateExtensions.Add(subjectNames.Build());
using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
var certificateFile = Path.Combine(Path.GetTempPath(), "hlg-network-test-" + Guid.NewGuid().ToString("N") + ".pfx");
try {
    await File.WriteAllBytesAsync(certificateFile, cert.Export(X509ContentType.Pfx, "test-only"));
    await Verify(certificateFile, "test-only");
} finally { File.Delete(certificateFile); }

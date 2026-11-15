using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// A per-Windows-user CA keeps the phone's trust stable across restarts and IP
// changes. Its private key is stored only inside a DPAPI-protected file.
sealed class LocalHttpsCertificate : IDisposable
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Neutrino HLG local HTTPS CA v1");
    readonly object gate = new();
    readonly X509Certificate2 authority;
    readonly Dictionary<string, X509Certificate2> leaves = new(StringComparer.OrdinalIgnoreCase);
    readonly List<X509Certificate2> retired = new();
    public byte[] RootCertificate => authority.RawData;
    public string Fingerprint { get; }

    public LocalHttpsCertificate(string directory) {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "root-ca.dpapi");
        if (File.Exists(path)) {
            var protectedBytes = File.ReadAllBytes(path);
            var pfx = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try { authority = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            var constraints = authority.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (!authority.HasPrivateKey || constraints?.CertificateAuthority != true || authority.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(2)) {
                authority.Dispose();
                throw new InvalidDataException("本机 HTTPS 根证书无效或即将过期，请恢复证书文件。");
            }
        } else {
            using var key = RSA.Create(3072);
            var name = "Neutrino HLG " + Environment.MachineName + " " + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            var pfx = created.Export(X509ContentType.Pfx);
            try {
                var encrypted = ProtectedData.Protect(pfx, Entropy, DataProtectionScope.CurrentUser);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temp, encrypted); File.Move(temp, path, false); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                authority = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
            } finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        Fingerprint = string.Join(":", SHA256.HashData(authority.RawData).Select(b => b.ToString("X2")));
    }

    public X509Certificate2 ForAddress(EndPoint? endpoint) {
        var ip = (endpoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var address = ip.ToString();
        lock (gate) {
            if (leaves.TryGetValue(address, out var cached) && cached.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1)) return cached;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Neutrino HLG", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var purposes = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, false));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(ip); if (!IPAddress.IsLoopback(ip)) names.AddIpAddress(IPAddress.Loopback);
            names.AddDnsName("localhost");
            var host = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host) == UriHostNameType.Dns && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) names.AddDnsName(host);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
            var expiry = DateTimeOffset.UtcNow.AddDays(90);
            var authorityExpiry = new DateTimeOffset(authority.NotAfter.ToUniversalTime()).AddMinutes(-1);
            if (expiry > authorityExpiry) expiry = authorityExpiry;
            var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7f;
            using var unsigned = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), expiry, serial);
            using var withKey = unsigned.CopyWithPrivateKey(key);
            // Windows Schannel cannot use an ephemeral private key for TLS.
            // Import a temporary user key that is removed when the cert is disposed.
            var pfx = withKey.Export(X509ContentType.Pfx);
            X509Certificate2 leaf;
            try { leaf = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            if (cached is not null) retired.Add(cached);
            leaves[address] = leaf;
            return leaf;
        }
    }

    public void Dispose() {
        lock (gate) { foreach (var leaf in leaves.Values.Concat(retired)) leaf.Dispose(); leaves.Clear(); retired.Clear(); authority.Dispose(); }
    }
}

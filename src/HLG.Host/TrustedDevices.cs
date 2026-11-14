using System.Security.Cryptography;
using System.Text.Json;

// Tokens remain in each browser. Only their hashes are persisted on the host.
sealed class TrustedDevices
{
    readonly object gate = new();
    readonly string path;
    readonly HashSet<string> hashes = new(StringComparer.Ordinal);
    readonly bool loadFailed;

    public TrustedDevices(string path) {
        this.path = path;
        try {
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            foreach (var hash in saved) {
                if (!ValidHex(hash)) throw new InvalidDataException();
                hashes.Add(hash.ToUpperInvariant());
            }
        } catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) {
            hashes.Clear(); loadFailed = true;
        }
    }
    public bool Available => !loadFailed;
    static bool ValidHex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    static string Digest(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
    public string? Identity(string? token) => ValidHex(token) ? Digest(token!) : null;
    public bool IsTrusted(string? token) {
        if (!ValidHex(token)) return false;
        var digest = Digest(token!);
        lock (gate) return !loadFailed && hashes.Contains(digest);
    }
    public void Add(string token) {
        if (!ValidHex(token)) throw new ArgumentException("无效的设备凭据。", nameof(token));
        lock (gate) {
            if (loadFailed) throw new InvalidDataException("无法读取已有的设备信任记录。");
            var hash = Digest(token);
            if (hashes.Contains(hash)) return;
            var next = hashes.Append(hash).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temp, JsonSerializer.Serialize(next));
                File.Move(temp, path, true);
                hashes.Add(hash);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public void Remove(string identity) {
        lock (gate) {
            if (loadFailed) throw new InvalidDataException("无法读取已有的设备信任记录。");
            if (!hashes.Contains(identity)) return;
            var next = hashes.Where(hash => hash != identity).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, JsonSerializer.Serialize(next)); File.Move(temp, path, true); hashes.Remove(identity); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

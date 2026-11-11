using System.Text.Json;

// Counts launches made through HLG, shared by all paired clients and persisted locally.
sealed class AppUsage
{
    public record Usage(long Count, DateTimeOffset LastUsed);
    readonly object gate = new();
    readonly string path;
    Dictionary<string, Usage> values;

    public AppUsage(string path) { this.path = path; values = Read(path); }
    static Dictionary<string, Usage> Read(string path) {
        try {
            var records = JsonSerializer.Deserialize<Dictionary<string, Usage>>(File.ReadAllText(path));
            return new Dictionary<string, Usage>(records ?? new(), StringComparer.OrdinalIgnoreCase);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(StringComparer.OrdinalIgnoreCase); }
    }
    public Dictionary<string, Usage> Snapshot() { lock (gate) return new(values, StringComparer.OrdinalIgnoreCase); }
    public void Record(string id) {
        lock (gate) {
            var next = new Dictionary<string, Usage>(values, StringComparer.OrdinalIgnoreCase);
            next.TryGetValue(id, out var previous);
            next[id] = new Usage(previous is null ? 1 : previous.Count == long.MaxValue ? long.MaxValue : previous.Count + 1, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, JsonSerializer.Serialize(next)); File.Move(temp, path, true); values = next; }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}

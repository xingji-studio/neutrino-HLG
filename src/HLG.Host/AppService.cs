using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

sealed class AppService
{
    public record App(string Name, string? Path, string Id, string Kind);
    sealed record Entry(App Public, string Target);
    readonly object gate = new();
    readonly string[] roots;
    readonly AppUsage usage;
    Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    bool loaded;

    public AppService(string[] roots, AppUsage usage) { this.roots = roots; this.usage = usage; }
    static string Id(string kind, string target) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + target.ToUpperInvariant())));
    public IReadOnlyList<App> List() {
        var next = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in AppCatalog.List(roots)) {
            var id = Id("shortcut", item.Path);
            names.Add(item.Name);
            next[id] = new(new(item.Name, item.Path, id, "shortcut"), item.Path);
        }
        foreach (var item in ShellApps.List()) {
            var id = Id("shell", item.Identity);
            if (!names.Contains(item.Name)) next[id] = new(new(item.Name, null, id, "shell"), item.Identity);
        }
        lock (gate) { entries = next; loaded = true; }
        var counts = usage.Snapshot();
        return next.Values.Select(e => e.Public)
            .OrderByDescending(a => counts.TryGetValue(a.Id, out var v) ? v.Count : 0)
            .ThenByDescending(a => counts.TryGetValue(a.Id, out var v) ? v.LastUsed : DateTimeOffset.MinValue)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    Entry? Resolve(string? id, string? path) {
        lock (gate) {
            if (loaded) return Find(id, path);
        }
        List();
        lock (gate) return Find(id, path);
    }
    Entry? Find(string? id, string? path) {
        if (!string.IsNullOrEmpty(id) && entries.TryGetValue(id, out var byId)) return byId;
        if (id is not null || string.IsNullOrWhiteSpace(path)) return null;
        return entries.Values.FirstOrDefault(e => e.Public.Kind == "shortcut" && string.Equals(e.Target, path, StringComparison.OrdinalIgnoreCase));
    }
    public IResult Icon(string? id, string? path) {
        var entry = Resolve(id, path);
        if (entry is null) return Results.NotFound();
        return entry.Public.Kind == "shortcut" ? AppIcons.Get(entry.Target, roots) : AppIcons.GetShell(entry.Target);
    }
    public IResult Open(string? id, string? path) {
        var entry = Resolve(id, path);
        if (entry is null) return Results.BadRequest(new { error = "应用不存在，请刷新应用列表。" });
        try {
            ProcessStartInfo start;
            if (entry.Public.Kind == "shell") {
                start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add("shell:AppsFolder\\" + entry.Target);
            } else {
                if (!File.Exists(entry.Target)) return Results.NotFound(new { error = "应用入口已经不存在。" });
                start = new ProcessStartInfo(entry.Target) { UseShellExecute = true };
            }
            using var process = Process.Start(start);
            int? pid = null;
            if (entry.Public.Kind == "shortcut" && process is not null) {
                try { pid = process.Id; } catch (InvalidOperationException) { }
            }
            try { usage.Record(entry.Public.Id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { /* Opening succeeded even if usage cannot be saved. */ }
            return Results.Ok(new { ok = true, processId = pid });
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException) {
            return Results.Problem("无法启动该应用：" + e.Message, statusCode: 500);
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

interface IConversationBackend
{
    Task<HttpResponseMessage> Request(HttpMethod method, string path, JsonObject? body = null);
}

sealed class TaskStore
{
    public record Entry(string Id, string Key, string? SessionId, string Directory);
    readonly IConversationBackend bridge;
    readonly string root;
    readonly bool monitor;
    readonly ConcurrentDictionary<string, string> active = new();

    public TaskStore(IConversationBackend bridge, string? root = null, bool monitor = true) {
        this.bridge = bridge;
        this.root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".neutrino", "hlg", "session");
        this.monitor = monitor;
        Directory.CreateDirectory(this.root);
        if (monitor) foreach (var dir in Directory.EnumerateDirectories(this.root)) {
            if (ReadEntry(dir) is { } entry) StartMonitor(entry);
        }
    }
    public string Track(string key, string prompt, JsonObject settings, JsonNode? initialSnapshot = null) {
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        var sessionId = StringProperty(initialSnapshot, "sessionId");
        var record = new JsonObject { ["id"] = id, ["key"] = key, ["sessionId"] = sessionId, ["title"] = prompt.Length > 60 ? prompt[..60] : prompt, ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["settings"] = settings.DeepClone() };
        WriteAtomic(Path.Combine(dir, "task.json"), record.ToJsonString());
        if (initialSnapshot is not null) WriteAtomic(Path.Combine(dir, "snapshot.json"), initialSnapshot.ToJsonString());
        StartMonitor(new Entry(id, key, sessionId, dir));
        return id;
    }
    public Entry? Find(string idOrKey) {
        if (Guid.TryParseExact(idOrKey, "N", out _)) {
            var direct = ReadEntry(Path.Combine(root, idOrKey));
            if (direct is not null && direct.Id == idOrKey) return direct;
        }
        foreach (var dir in Directory.EnumerateDirectories(root)) {
            var entry = ReadEntry(dir);
            if (entry is not null && (entry.Id == idOrKey || entry.Key == idOrKey)) return entry;
        }
        return null;
    }
    public void Rebind(Entry entry, string key) {
        var path = Path.Combine(entry.Directory, "task.json");
        var record = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("任务记录不可用。");
        record["key"] = key;
        if (entry.SessionId is not null) record["sessionId"] = entry.SessionId;
        WriteAtomic(path, record.ToJsonString());
        StartMonitor(entry with { Key = key });
    }
    public JsonNode? ArchivedSnapshot(string idOrKey) {
        var entry = Find(idOrKey);
        if (entry is null) return null;
        try {
            var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(entry.Directory, "snapshot.json"))) as JsonObject;
            if (snapshot is not null) { snapshot["archived"] = true; return snapshot; }
        } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }
    public JsonArray List() {
        var result = new JsonArray();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderByDescending(Directory.GetCreationTimeUtc)) {
            try { var record = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "task.json"))); if (record != null) result.Add(record); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result;
    }
    static Entry? ReadEntry(string dir) {
        try {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "task.json")));
            var id = StringProperty(node, "id"); var key = StringProperty(node, "key");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key) || !string.Equals(id, Path.GetFileName(dir), StringComparison.Ordinal)) return null;
            var sessionId = StringProperty(node, "sessionId");
            if (string.IsNullOrEmpty(sessionId)) {
                try { sessionId = StringProperty(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "snapshot.json"))), "sessionId"); }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return new Entry(id, key, sessionId, dir);
        } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    internal static string? StringProperty(JsonNode? node, string name) => node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static void WriteAtomic(string path, string text) {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    void StartMonitor(Entry entry) { if (monitor && active.TryAdd(entry.Id, entry.Directory)) _ = Monitor(entry.Id, entry.Directory); }
    async Task Monitor(string id, string dir) {
        while (active.ContainsKey(id)) {
            var entry = ReadEntry(dir);
            if (entry is null) { active.TryRemove(id, out _); return; }
            try {
                using var response = await bridge.Request(HttpMethod.Get, "/live/" + Uri.EscapeDataString(entry.Key));
                if (response.StatusCode == HttpStatusCode.NotFound) {
                    active.TryRemove(id, out _);
                    if (ReadEntry(dir) is { } latest && latest.Key != entry.Key) StartMonitor(latest);
                    return;
                }
                if (response.IsSuccessStatusCode && ReadEntry(dir)?.Key == entry.Key)
                    WriteAtomic(Path.Combine(dir, "snapshot.json"), await response.Content.ReadAsStringAsync());
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException) { }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

sealed class ConversationService(IConversationBackend bridge, TaskStore store)
{
    readonly ConcurrentDictionary<string, SemaphoreSlim> sendLocks = new();
    static string Live(string key) => "/live/" + Uri.EscapeDataString(key);
    static async Task<IResult> Pass(HttpResponseMessage response) => Results.Content(await response.Content.ReadAsStringAsync(), "application/json", statusCode: (int)response.StatusCode);
    static IResult Unavailable() => Results.Problem("Neutrino 本机 API 不可用，请检查它是否正在运行。", statusCode: 503);

    public async Task<IResult> Get(string id) {
        var entry = store.Find(id);
        if (entry is null) return Results.NotFound(new { error = "任务不存在。" });
        try {
            using var response = await bridge.Request(HttpMethod.Get, Live(entry.Key));
            if (response.IsSuccessStatusCode) return await Pass(response);
        } catch (Exception e) when (e is IOException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException) { }
        var archived = store.ArchivedSnapshot(entry.Id);
        return archived is null ? Results.NotFound(new { error = "活动会话不可用，尚无可用快照。" }) : Results.Json(archived);
    }
    public async Task<IResult> Approve(string taskId, string approvalId, JsonObject body) {
        var entry = store.Find(taskId);
        if (entry is null) return Results.NotFound(new { error = "任务不存在。" });
        try {
            using var response = await bridge.Request(HttpMethod.Post, Live(entry.Key) + "/approvals/" + Uri.EscapeDataString(approvalId), body);
            return await Pass(response);
        } catch (Exception e) when (e is IOException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException) { return Unavailable(); }
    }
    public async Task<IResult> Send(string taskId, JsonObject body) {
        var text = TaskStore.StringProperty(body, "text");
        if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "请输入消息。" });
        var entry = store.Find(taskId);
        if (entry is null) return Results.NotFound(new { error = "任务不存在。" });
        var gate = sendLocks.GetOrAdd(entry.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try {
            entry = store.Find(entry.Id);
            if (entry is null) return Results.NotFound(new { error = "任务不存在。" });
            using var probe = await bridge.Request(HttpMethod.Get, Live(entry.Key));
            JsonNode? snapshot;
            if (probe.StatusCode == HttpStatusCode.NotFound) {
                if (string.IsNullOrEmpty(entry.SessionId)) return Results.Conflict(new { error = "找不到原会话的历史记录，无法继续此对话。" });
                using var restored = await bridge.Request(HttpMethod.Post, "/session/" + Uri.EscapeDataString(entry.SessionId) + "/continue", new JsonObject());
                if (!restored.IsSuccessStatusCode) return await Pass(restored);
                var restoredKey = TaskStore.StringProperty(JsonNode.Parse(await restored.Content.ReadAsStringAsync()), "key");
                if (string.IsNullOrEmpty(restoredKey)) return Results.Problem("Neutrino 未返回恢复后的会话标识。", statusCode: 502);
                store.Rebind(entry, restoredKey);
                entry = entry with { Key = restoredKey };
                using var afterRestore = await bridge.Request(HttpMethod.Get, Live(entry.Key));
                if (!afterRestore.IsSuccessStatusCode) return await Pass(afterRestore);
                snapshot = JsonNode.Parse(await afterRestore.Content.ReadAsStringAsync());
            } else {
                if (!probe.IsSuccessStatusCode) return await Pass(probe);
                snapshot = JsonNode.Parse(await probe.Content.ReadAsStringAsync());
            }
            if (snapshot is not JsonObject) return Results.Problem("Neutrino 返回了无效的会话状态。", statusCode: 502);
            if (!string.IsNullOrEmpty(TaskStore.StringProperty(snapshot, "queued")))
                return Results.Conflict(new { error = "已有一条消息在排队，请等待后再发送。" });
            if (body["selection"] is not null) {
                if (body["selection"] is not JsonObject selection) return Results.BadRequest(new { error = "无效的模型选择。" });
                var changed = TaskStore.StringProperty(selection, "provider") != TaskStore.StringProperty(snapshot, "provider")
                    || TaskStore.StringProperty(selection, "model") != TaskStore.StringProperty(snapshot, "model")
                    || TaskStore.StringProperty(selection, "intensity") != TaskStore.StringProperty(snapshot, "intensity");
                if (changed) {
                    using var configured = await bridge.Request(HttpMethod.Post, Live(entry.Key) + "/model", (JsonObject)selection.DeepClone());
                    if (!configured.IsSuccessStatusCode) return await Pass(configured);
                }
            }
            var message = new JsonObject { ["text"] = text };
            if (body["agent_mode"] is not null) message["agent_mode"] = body["agent_mode"]!.DeepClone();
            using var sent = await bridge.Request(HttpMethod.Post, Live(entry.Key) + "/message", message);
            if (!sent.IsSuccessStatusCode) return await Pass(sent);
            return Results.Ok(new { ok = true, taskId = entry.Id });
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException) { return Unavailable(); }
        finally { gate.Release(); }
    }
}

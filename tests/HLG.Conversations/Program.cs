using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
var root = Path.Combine(Path.GetTempPath(), "hlg-conversations-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    var backend = new FakeBackend();
    var store = new TaskStore(backend, root, monitor: false);
    var id = store.Track("old", "first", new JsonObject(), backend.Snapshot());
    var service = new ConversationService(backend, store);
    Check(store.Find(id)?.SessionId == "session-1", "session id saved");
    Check(Status(await service.Send(id, new JsonObject { ["text"] = "second" })) == 200, "send succeeds");
    Check(backend.Messages.Single() == "second", "message sent to original conversation");
    backend.Queued = "waiting";
    Check(Status(await service.Send(id, new JsonObject { ["text"] = "third" })) == 409, "reject overwriting queued message");
    Check(backend.Messages.Count == 1, "queued message preserved");
    backend.Queued = null;
    backend.ValidKey = "new";
    var resumed = await Task.WhenAll(service.Send(id, new JsonObject { ["text"] = "third" }), service.Send(id, new JsonObject { ["text"] = "fourth" }));
    Check(resumed.All(r => Status(r) == 200), "concurrent sends resume");
    Check(backend.Continues == 1 && backend.Messages.Count == 3, "resume once and send both messages");
    Check(new TaskStore(backend, root, false).Find(id)?.Key == "new", "rebound key persists");
    var selection = new JsonObject { ["provider"] = "B", ["model"] = "two", ["intensity"] = "High" };
    Check(Status(await service.Send(id, new JsonObject { ["text"] = "fifth", ["selection"] = selection })) == 200, "change model and send");
    Check(backend.Events.TakeLast(2).SequenceEqual(new[] { "model", "message" }), "configure model before message");
    backend.RejectModel = true;
    Check(Status(await service.Send(id, new JsonObject { ["text"] = "sixth", ["selection"] = new JsonObject { ["provider"] = "C", ["model"] = "three", ["intensity"] = "High" } })) == 409, "model failure propagated");
    Check(!backend.Messages.Contains("sixth"), "model failure prevents send");
    backend.Offline = true;
    Check(Status(await service.Send(id, new JsonObject { ["text"] = "offline" })) == 503, "connection failure reported");
    Check(backend.Continues == 1, "no retry on connection failure");
    var noHistory = store.Track("expired", "old", new JsonObject());
    backend.Offline = false;
    Check(Status(await service.Send(noHistory, new JsonObject { ["text"] = "cannot restore" })) == 409, "missing session id cannot silently create new conversation");
    var appsRoot = Path.Combine(root, "start-menu");
    Directory.CreateDirectory(Path.Combine(appsRoot, "nested"));
    File.WriteAllText(Path.Combine(appsRoot, "First.lnk"), "");
    File.WriteAllText(Path.Combine(appsRoot, "nested", "Second.lnk"), "");
    var apps = AppCatalog.List(new[] { Path.Combine(root, "missing"), appsRoot });
    Check(apps.Select(a => a.Name).SequenceEqual(new[] { "First", "Second" }), "applications from available roots");
    if (OperatingSystem.IsWindows()) {
        var installed = AppCatalog.List(new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) });
        Console.WriteLine($"Windows start-menu enumeration succeeded: {installed.Count} entries.");
    }
    Console.WriteLine("Conversation persistence, restore, queue, selection and application enumeration tests passed.");
} finally { Directory.Delete(root, true); }

sealed class FakeBackend : IConversationBackend {
    public string ValidKey = "old";
    public string? Queued;
    public int Continues;
    public bool Offline, RejectModel;
    public List<string> Messages = new(), Events = new();
    string provider = "A", model = "one", intensity = "Low";
    public JsonObject Snapshot() => new() { ["sessionId"] = "session-1", ["provider"] = provider, ["model"] = model, ["intensity"] = intensity, ["queued"] = Queued, ["messages"] = new JsonArray() };
    static HttpResponseMessage Response(HttpStatusCode code, JsonNode? node = null) => new(code) { Content = new StringContent((node ?? new JsonObject()).ToJsonString()) };
    public async Task<HttpResponseMessage> Request(HttpMethod method, string path, JsonObject? body = null) {
        if (Offline) throw new HttpRequestException("offline");
        if (path == "/session/session-1/continue") { Continues++; await Task.Delay(20); return Response(HttpStatusCode.OK, new JsonObject { ["key"] = ValidKey }); }
        if (!path.StartsWith("/live/" + ValidKey, StringComparison.Ordinal)) return Response(HttpStatusCode.NotFound);
        if (method == HttpMethod.Get) return Response(HttpStatusCode.OK, Snapshot());
        if (path.EndsWith("/model")) {
            Events.Add("model");
            if (RejectModel) return Response(HttpStatusCode.Conflict);
            provider = body!["provider"]!.GetValue<string>(); model = body["model"]!.GetValue<string>(); intensity = body["intensity"]!.GetValue<string>();
        } else if (path.EndsWith("/message")) { Events.Add("message"); Messages.Add(body!["text"]!.GetValue<string>()); }
        return Response(HttpStatusCode.OK);
    }
}

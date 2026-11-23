using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

sealed class VsCodeLicenseRequiredException : Exception;
sealed record VsCodeSession(string Id, string Name, string Path, string State, string Message, string Url, string? Version, DateTimeOffset LastUsed);
sealed record VsCodeTarget(Uri Address, string BasePath, string Token, CancellationToken Stopping);

sealed class VsCodeService : IHostedService, IDisposable
{
    sealed class Entry(string id, string path, DateTimeOffset lastUsed)
    {
        public string Id { get; } = id;
        public string Path { get; } = path;
        public DateTimeOffset LastUsed = lastUsed;
        public string State = "stopped", Message = "点击启动网页服务";
        public string? Version, Token;
        public Uri? Address;
        public CancellationTokenSource? Run;
        public Task? Work;
        public Process? Process;
        public string BasePath => "/api/vscode/web/" + Id + "/";
        public VsCodeSession Snapshot() => new(Id, new DirectoryInfo(Path).Name is { Length: > 0 } name ? name : Path,
            Path, State, Message, BasePath, Version, LastUsed);
    }
    sealed record StoredEntry(string Id, string Path, DateTimeOffset LastUsed);
    readonly object gate = new();
    readonly SemaphoreSlim mutation = new(1, 1);
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly string directory;
    readonly FileLog log;
    readonly IVsCodeRuntime runtime;
    readonly CancellationTokenSource shutdown = new();
    readonly HttpClient probe = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    readonly TimeSpan startupTimeout;
    bool disposed;

    public VsCodeService(string directory, FileLog log, IVsCodeRuntime? runtime = null, TimeSpan? startupTimeout = null) {
        this.directory = directory; this.log = log; this.runtime = runtime ?? new VsCodeRuntime();
        this.startupTimeout = startupTimeout ?? TimeSpan.FromMinutes(20);
        try {
            var file = Path.Combine(directory, "sessions.json");
            if (!File.Exists(file)) return;
            foreach (var item in JsonSerializer.Deserialize<StoredEntry[]>(File.ReadAllText(file)) ?? []) {
                if (entries.Count >= 100 || !Regex.IsMatch(item.Id, "^[a-f0-9]{32}$") || !Path.IsPathFullyQualified(item.Path)) continue;
                var path = NormalizeDirectory(item.Path, false);
                if (!entries.Values.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))) entries[item.Id] = new(item.Id, path, item.LastUsed);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) {
            log.Write(LogLevel.Warning, "VS Code", "无法恢复网页入口", e);
        }
    }

    internal static string NormalizeDirectory(string? path, bool requireExists = true) {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || !Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择电脑上有效的绝对目录路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (requireExists && !Directory.Exists(full)) throw new DirectoryNotFoundException("文件夹不存在或无法访问。");
        return full;
    }
    public IReadOnlyList<VsCodeSession> List() { lock (gate) return entries.Values.OrderByDescending(e => e.LastUsed).Select(e => e.Snapshot()).ToArray(); }
    public VsCodeSession? Get(string id) { lock (gate) return entries.TryGetValue(id, out var entry) ? entry.Snapshot() : null; }
    public VsCodeTarget? Target(string id) {
        lock (gate) return entries.TryGetValue(id, out var e) && e.State == "ready" && e.Address is not null && e.Token is not null && e.Run is not null && !e.Run.IsCancellationRequested
            ? new(e.Address, e.BasePath, e.Token, e.Run.Token) : null;
    }

    public async Task<VsCodeSession> Open(string? path, bool acceptLicense, bool openDesktop, CancellationToken cancellationToken) {
        var full = NormalizeDirectory(path);
        await mutation.WaitAsync(cancellationToken);
        try {
            shutdown.Token.ThrowIfCancellationRequested();
            var consent = Path.Combine(directory, "server-license.json");
            if (!File.Exists(consent) && !acceptLicense) throw new VsCodeLicenseRequiredException();
            Entry? entry;
            bool active;
            lock (gate) { entry = entries.Values.FirstOrDefault(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)); active = entry?.State is "starting" or "ready"; }
            if (active && entry is not null && !openDesktop) {
                lock (gate) { entry.LastUsed = DateTimeOffset.UtcNow; Save(); return entry.Snapshot(); }
            }
            // A failed run may still be cleaning up its process and streams.
            // Finish that cleanup before replacing its token and process.
            if (!active && entry?.Work is { } previous) await previous.WaitAsync(cancellationToken);
            var installation = await runtime.Locate(cancellationToken);
            Directory.CreateDirectory(directory);
            if (!File.Exists(consent)) AtomicWrite(consent, JsonSerializer.Serialize(new { AcceptedAt = DateTimeOffset.UtcNow, License = "https://aka.ms/vscode-server-license" }));
            var wasNew = entry is null;
            entry ??= new(Guid.NewGuid().ToString("N"), full, DateTimeOffset.UtcNow);
            if (openDesktop) runtime.OpenDesktop(installation, full);
            lock (gate) {
                if (active) { entry.LastUsed = DateTimeOffset.UtcNow; Save(); return entry.Snapshot(); }
                if (wasNew && entries.Count >= 100) throw new InvalidOperationException("网页入口数量已达上限，请先在当前画面中长按移除不再使用的入口。");
                if (wasNew) entries.Add(entry.Id, entry);
                entry.LastUsed = DateTimeOffset.UtcNow; entry.Version = installation.Version;
                entry.State = "starting"; entry.Message = "正在准备官方 VS Code 网页组件…";
                entry.Run?.Dispose(); entry.Run = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                Save();
                var captured = entry;
                entry.Work = Task.Run(() => Run(captured, installation, captured.Run.Token));
                return entry.Snapshot();
            }
        } finally { mutation.Release(); }
    }

    async Task Run(Entry entry, VsCodeInstallation installation, CancellationToken stopping) {
        Process? process = null;
        VsCodeProcessTree? tree = null;
        Task? stdout = null, stderr = null;
        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            stopping.ThrowIfCancellationRequested();
            // A digit immediately after a Windows separator can be treated as
            // a backreference by the upstream native watcher's exclude glob.
            var profile = Path.Combine(directory, "workspaces", "workspace-" + entry.Id);
            Directory.CreateDirectory(profile);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var tokenFile = Path.Combine(profile, "connection-token");
            await File.WriteAllTextAsync(tokenFile, token, new UTF8Encoding(false), stopping);
            var data = Path.Combine(profile, "server");
            Directory.CreateDirectory(data);
            process = runtime.StartWeb(installation, new(entry.Path, entry.BasePath, Path.Combine(directory, "cli"), data, tokenFile));
            tree = new VsCodeProcessTree(process);
            lock (gate) { entry.Process = process; entry.Token = token; }
            async Task Read(StreamReader reader) {
                try {
                    while (await reader.ReadLineAsync(stopping) is { } line) {
                        var match = Regex.Match(line, @"http://127\.0\.0\.1:(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var port) && port is > 0 and <= 65535)
                            listening.TrySetResult(new Uri("http://127.0.0.1:" + port + "/"));
                        if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
                            lock (gate) { if (entry.State == "starting") entry.Message = "正在下载官方 VS Code 网页组件，首次启动可能需要几分钟…"; }
                        log.Write(LogLevel.Information, "VS Code " + entry.Id[..8], line.Replace(token, "[已隐藏]", StringComparison.Ordinal));
                    }
                } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException) { }
            }
            stdout = Read(process.StandardOutput); stderr = Read(process.StandardError);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            startup.CancelAfter(startupTimeout);
            var exited = process.WaitForExitAsync(stopping);
            var first = await Task.WhenAny(listening.Task, exited).WaitAsync(startup.Token);
            if (first == exited) throw new InvalidOperationException("VS Code 网页服务启动失败，请查看电脑端日志。");
            var address = await listening.Task;
            lock (gate) entry.Address = address;
            while (true) {
                startup.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("VS Code 网页服务已退出，请查看电脑端日志。");
                try {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, entry.BasePath));
                    request.Headers.Add("Cookie", "vscode-tkn=" + token);
                    using var response = await probe.SendAsync(request, startup.Token);
                    if (response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync(startup.Token)).Contains("vscode-workbench-web-configuration", StringComparison.Ordinal)) break;
                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new InvalidOperationException("VS Code 网页服务内部认证失败。");
                } catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !startup.IsCancellationRequested) { }
                await Task.Delay(500, startup.Token);
            }
            lock (gate) { entry.State = "ready"; entry.Message = "网页服务就绪"; }
            log.Write(LogLevel.Information, "VS Code", "网页服务就绪：" + entry.Path);
            await exited;
            if (!stopping.IsCancellationRequested) throw new InvalidOperationException("网页服务已退出，点击重试可重新启动。");
        } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception e) {
            var message = e is OperationCanceledException ? "准备网页组件超时，请检查电脑网络后重试。" : e.Message;
            lock (gate) { entry.State = "error"; entry.Message = message; }
            log.Write(LogLevel.Error, "VS Code", "网页服务失败：" + entry.Path + "；" + message, e is OperationCanceledException ? null : e);
        } finally {
            VsCodeRuntime.Kill(process);
            tree?.Dispose();
            if (process is not null) {
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await process.WaitForExitAsync(timeout.Token); }
                catch (Exception e) when (e is InvalidOperationException or OperationCanceledException) { }
            }
            if (stdout is not null && stderr is not null) { try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { } }
            lock (gate) { entry.Address = null; entry.Token = null; entry.Process = null; if (stopping.IsCancellationRequested) { entry.State = "stopped"; entry.Message = "网页服务已停止"; } }
            process?.Dispose();
        }
    }

    public async Task<bool> Stop(string id, CancellationToken cancellationToken) {
        await mutation.WaitAsync(cancellationToken);
        try {
            Entry? entry;
            lock (gate) { if (!entries.TryGetValue(id, out entry)) return false; entry.Run?.Cancel(); entries.Remove(id); Save(); }
            if (entry.Work is not null) await entry.Work.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            entry.Run?.Dispose();
            log.Write(LogLevel.Information, "VS Code", "已停止网页入口：" + entry.Path);
            return true;
        } finally { mutation.Release(); }
    }

    void Save() {
        Directory.CreateDirectory(directory);
        AtomicWrite(Path.Combine(directory, "sessions.json"), JsonSerializer.Serialize(entries.Values.Select(e => new StoredEntry(e.Id, e.Path, e.LastUsed)).ToArray()));
    }
    static void AtomicWrite(string path, string value) {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, value, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken) {
        shutdown.Cancel();
        Task[] work; lock (gate) work = entries.Values.Select(e => e.Work ?? Task.CompletedTask).ToArray();
        try { await Task.WhenAll(work).WaitAsync(cancellationToken); } catch (OperationCanceledException) { lock (gate) foreach (var entry in entries.Values) VsCodeRuntime.Kill(entry.Process); }
    }
    public void Dispose() {
        if (disposed) return; disposed = true; shutdown.Cancel();
        lock (gate) foreach (var entry in entries.Values) { VsCodeRuntime.Kill(entry.Process); entry.Run?.Dispose(); }
        shutdown.Dispose(); probe.Dispose(); mutation.Dispose();
    }
}

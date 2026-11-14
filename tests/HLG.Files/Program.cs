using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Expect(int status, Action action) {
    try { action(); throw new Exception("Expected file operation to fail."); }
    catch (FileTransferService.TransferException e) { Check(e.Status == status, "Unexpected error: " + e.Message); }
}
var root = Path.Combine(AppContext.BaseDirectory, "files-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    var source = Path.Combine(root, "源目录"); var destination = Path.Combine(root, "目标目录");
    Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
    var service = new FileTransferService();
    var file = Path.Combine(source, "报告.txt"); File.WriteAllText(file, "原始内容");
    var copy = service.Transfer(new(file, source, "copy"));
    Check(File.ReadAllText(file) == "原始内容" && File.ReadAllText(copy.Path) == "原始内容" && Path.GetFileName(copy.Path) == "报告 - 副本.txt", "same-directory file copy failed");
    var copy2 = service.Transfer(new(file, source, "copy"));
    Check(Path.GetFileName(copy2.Path) == "报告 - 副本 (2).txt", "duplicate copy name not incremented");
    Check(service.Transfer(new(file, source, "cut")).NoOp && File.Exists(file), "cut into same directory must be a no-op");
    var moved = service.Transfer(new(file, destination, "cut"));
    Check(!File.Exists(file) && File.ReadAllText(moved.Path) == "原始内容", "file move failed");
    File.WriteAllText(file, "新内容");
    Expect(409, () => service.Transfer(new(file, destination, "copy")));
    Check(File.ReadAllText(file) == "新内容" && File.ReadAllText(moved.Path) == "原始内容", "conflict overwrote data");
    Expect(404, () => service.Transfer(new(Path.Combine(source, "missing"), destination, "copy")));
    Expect(400, () => service.Transfer(new("relative.txt", destination, "copy")));
    Expect(400, () => service.Transfer(new(file, destination, "delete")));
    Expect(400, () => service.Transfer(new(Path.GetPathRoot(root), destination, "copy")));
    Expect(400, () => service.Transfer(new(file, moved.Path, "copy")));

    var folder = Path.Combine(source, "项目.v1"); Directory.CreateDirectory(Path.Combine(folder, "子目录", "空目录"));
    File.WriteAllText(Path.Combine(folder, "子目录", "数据.txt"), "嵌套数据");
    var hiddenFile = Path.Combine(folder, "隐藏.txt"); File.WriteAllText(hiddenFile, "隐藏内容"); File.SetAttributes(hiddenFile, FileAttributes.Hidden);
    Expect(400, () => service.Transfer(new(folder, Path.Combine(folder, "子目录"), "copy")));
    var dirCopy = service.Transfer(new(folder, source, "copy"));
    Check(Path.GetFileName(dirCopy.Path) == "项目.v1 - 副本" && File.ReadAllText(Path.Combine(dirCopy.Path, "子目录", "数据.txt")) == "嵌套数据" && Directory.Exists(Path.Combine(dirCopy.Path, "子目录", "空目录")) && File.ReadAllText(Path.Combine(dirCopy.Path, "隐藏.txt")) == "隐藏内容", "recursive folder copy failed");
    var dirMove = service.Transfer(new(folder, destination, "cut"));
    Check(!Directory.Exists(folder) && File.Exists(Path.Combine(dirMove.Path, "子目录", "数据.txt")), "same-volume folder move failed");

    static void CrossDevice(string _, string __) => throw new IOException("cross-volume", unchecked((int)0x80070011));
    var crossSource = Path.Combine(source, "跨盘项目"); Directory.CreateDirectory(Path.Combine(crossSource, "nested"));
    File.WriteAllText(Path.Combine(crossSource, "nested", "data.txt"), "跨磁盘文件");
    var crossReadonly = Path.Combine(crossSource, "nested", "readonly.txt"); File.WriteAllText(crossReadonly, "readonly"); File.SetAttributes(crossReadonly, FileAttributes.ReadOnly);
    var copiedReadonly = Path.Combine(destination, "跨盘项目", "nested", "readonly.txt");
    try {
        var cross = new FileTransferService(CrossDevice).Transfer(new(crossSource, destination, "cut"));
        Check(!Directory.Exists(crossSource) && File.ReadAllText(Path.Combine(cross.Path, "nested", "data.txt")) == "跨磁盘文件" && File.ReadAllText(copiedReadonly) == "readonly" && (File.GetAttributes(copiedReadonly) & FileAttributes.ReadOnly) != 0, "cross-volume folder move failed");
    } finally {
        if (File.Exists(crossReadonly)) File.SetAttributes(crossReadonly, FileAttributes.Normal);
        if (File.Exists(copiedReadonly)) File.SetAttributes(copiedReadonly, FileAttributes.Normal);
    }
    var changing = Path.Combine(source, "复制期间变化"); Directory.CreateDirectory(changing); File.WriteAllText(Path.Combine(changing, "old.txt"), "old");
    var changesBeforeCommit = new FileTransferService(CrossDevice, path => File.WriteAllText(Path.Combine(path, "new.txt"), "new"));
    Expect(409, () => changesBeforeCommit.Transfer(new(changing, destination, "cut")));
    Check(File.Exists(Path.Combine(changing, "old.txt")) && File.Exists(Path.Combine(changing, "new.txt")) && !Directory.Exists(Path.Combine(destination, "复制期间变化")), "source changed during copy was removed or committed");
    var afterCommit = Path.Combine(source, "提交后变化"); Directory.CreateDirectory(afterCommit); File.WriteAllText(Path.Combine(afterCommit, "old.txt"), "old");
    var changesAfterCommit = new FileTransferService(CrossDevice, afterCommit: path => File.WriteAllText(Path.Combine(path, "new.txt"), "new"));
    Expect(409, () => changesAfterCommit.Transfer(new(afterCommit, destination, "cut")));
    Check(File.ReadAllText(Path.Combine(afterCommit, "new.txt")) == "new" && File.ReadAllText(Path.Combine(destination, "提交后变化", "old.txt")) == "old", "new source file was lost after cross-volume copy");
    Check(!Directory.EnumerateFileSystemEntries(destination, ".hlg-transfer-*").Any(), "temporary transfer files remained");

    // A junction can otherwise make the destination appear outside the source
    // lexically while pointing back inside it.
    var junctionSource = Path.Combine(source, "junction-source"); Directory.CreateDirectory(Path.Combine(junctionSource, "inside"));
    var junction = Path.Combine(root, "junction");
    var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.Arguments = "/d /c mklink /J \"" + junction + "\" \"" + Path.Combine(junctionSource, "inside") + "\"";
    using (var process = Process.Start(psi) ?? throw new Exception("Unable to create junction test.")) { process.WaitForExit(); Check(process.ExitCode == 0, "Unable to create junction test: " + process.StandardError.ReadToEnd()); }
    try { Expect(400, () => service.Transfer(new(junctionSource, junction, "copy"))); }
    finally { Directory.Delete(junction); }
    var innerJunction = Path.Combine(junctionSource, "link");
    psi.Arguments = "/d /c mklink /J \"" + innerJunction + "\" \"" + destination + "\"";
    using (var process = Process.Start(psi) ?? throw new Exception("Unable to create inner junction test.")) { process.WaitForExit(); Check(process.ExitCode == 0, "Unable to create inner junction test: " + process.StandardError.ReadToEnd()); }
    try {
        Expect(400, () => service.Transfer(new(junctionSource, destination, "copy")));
        Expect(400, () => new FileTransferService(CrossDevice).Transfer(new(junctionSource, destination, "cut")));
        Check(Directory.Exists(junctionSource) && !Directory.Exists(Path.Combine(destination, "junction-source")) && !Directory.EnumerateFileSystemEntries(destination, ".hlg-transfer-*").Any(), "directory junction copy must fail without changing either tree");
    } finally { Directory.Delete(innerJunction); }

    // Failure after copying a read-only file must clean up the staging tree.
    var readonlyFolder = Path.Combine(source, "readonly-source"); Directory.CreateDirectory(readonlyFolder);
    var readonlyFile = Path.Combine(readonlyFolder, "readonly.txt"); File.WriteAllText(readonlyFile, "original"); File.SetAttributes(readonlyFile, FileAttributes.ReadOnly);
    try {
        var directCopy = service.Transfer(new(readonlyFile, destination, "copy"));
        try { Check(File.ReadAllText(directCopy.Path) == "original" && (File.GetAttributes(directCopy.Path) & FileAttributes.ReadOnly) != 0, "read-only file copy failed"); }
        finally { if (File.Exists(directCopy.Path)) File.SetAttributes(directCopy.Path, FileAttributes.Normal); }
        var interrupted = new FileTransferService(afterStaging: _ => throw new IOException("injected failure"));
        try { interrupted.Transfer(new(readonlyFolder, destination, "copy")); throw new Exception("Expected interrupted copy to fail."); }
        catch (IOException e) when (e.Message == "injected failure") { }
        Check(File.ReadAllText(readonlyFile) == "original" && !Directory.Exists(Path.Combine(destination, "readonly-source")) && !Directory.EnumerateFileSystemEntries(destination, ".hlg-transfer-*").Any(), "read-only staging cleanup failed");
    } finally { File.SetAttributes(readonlyFile, FileAttributes.Normal); }

    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
    var pairing = new PairingAuth(new TrustedDevices(Path.Combine(root, "trusted.json")));
    await using var app = builder.Build();
    app.Use((HttpContext context, RequestDelegate next) => pairing.Handle(context, next));
    app.MapPost("/api/pair", (Func<HttpContext, Task<IResult>>)pairing.Pair);
    app.MapPost("/api/fs/paste", service.Paste);
    app.MapGet("/api/fs/download", service.Download);
    await app.StartAsync();
    try {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false, UseProxy = false }) { BaseAddress = new Uri(address) };
        using (var denied = await client.GetAsync("/api/fs/download?path=" + Uri.EscapeDataString(file))) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "unpaired download was accepted");
        using var deniedPaste = new HttpRequestMessage(HttpMethod.Post, "/api/fs/paste") { Content = JsonContent.Create(new { source = file, directory = destination, operation = "cut" }) };
        deniedPaste.Headers.Add("Origin", address);
        using (var denied = await client.SendAsync(deniedPaste)) Check(denied.StatusCode == HttpStatusCode.Unauthorized && File.Exists(file), "unpaired paste was accepted");
        using var pairRequest = new HttpRequestMessage(HttpMethod.Post, "/api/pair") { Content = JsonContent.Create(new { code = pairing.PairingCode }) };
        pairRequest.Headers.Add("Origin", address);
        using var paired = await client.SendAsync(pairRequest); Check(paired.IsSuccessStatusCode, "pairing failed");
        var cookie = paired.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using var download = new HttpRequestMessage(HttpMethod.Get, "/api/fs/download?path=" + Uri.EscapeDataString(file)); download.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(download);
        Check(response.IsSuccessStatusCode && response.Content.Headers.ContentDisposition?.DispositionType == "attachment" && response.Content.Headers.ContentDisposition?.FileNameStar == "报告.txt" && await response.Content.ReadAsStringAsync() == "新内容", "authenticated file download failed");
        using var invalidDownload = new HttpRequestMessage(HttpMethod.Get, "/api/fs/download?path=" + Uri.EscapeDataString(source)); invalidDownload.Headers.Add("Cookie", cookie);
        using (var bad = await client.SendAsync(invalidDownload)) Check(bad.StatusCode == HttpStatusCode.BadRequest, "directory download was accepted");
        using var range = new HttpRequestMessage(HttpMethod.Get, "/api/fs/download?path=" + Uri.EscapeDataString(file)); range.Headers.Add("Cookie", cookie); range.Headers.Range = new RangeHeaderValue(0, 2);
        using var partial = await client.SendAsync(range); Check(partial.StatusCode == HttpStatusCode.PartialContent && (await partial.Content.ReadAsByteArrayAsync()).Length == 3, "file range download failed");
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/fs/paste") { Content = JsonContent.Create(new { source = file, directory = destination, operation = "cut" }) };
        crossOrigin.Headers.Add("Cookie", cookie); crossOrigin.Headers.Add("Origin", "https://untrusted.example");
        using var refused = await client.SendAsync(crossOrigin); Check(refused.StatusCode == HttpStatusCode.Forbidden && File.Exists(file), "cross-origin file move was accepted");
        var apiFile = Path.Combine(source, "api-source.txt"); File.WriteAllText(apiFile, "api content");
        using var paste = new HttpRequestMessage(HttpMethod.Post, "/api/fs/paste") { Content = JsonContent.Create(new { source = apiFile, directory = destination, operation = "copy" }) };
        paste.Headers.Add("Cookie", cookie); paste.Headers.Add("Origin", address);
        using var pasted = await client.SendAsync(paste);
        Check(pasted.IsSuccessStatusCode && File.ReadAllText(Path.Combine(destination, "api-source.txt")) == "api content" && File.Exists(apiFile), "authenticated paste failed");
    } finally { await app.StopAsync(); }
    Console.WriteLine("File copy, move, conflicts, cross-volume safety, junction checks and authenticated downloads passed.");
} finally { Directory.Delete(root, true); }

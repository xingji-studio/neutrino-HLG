using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Win32.SafeHandles;

sealed class FileTransferService
{
    public record PasteRequest(string? Source, string? Directory, string? Operation);
    public record PasteResult(string Path, string Operation, bool NoOp = false);
    sealed record CopiedFile(string RelativePath);
    sealed record CopyManifest(List<string> Directories, List<CopiedFile> Files);
    public sealed class TransferException(int status, string message, string? destination = null) : Exception(message) {
        public int Status { get; } = status;
        public string? Destination { get; } = destination;
    }

    readonly Action<string, string> moveDirectory;
    readonly Action<string>? afterStaging;
    readonly Action<string>? afterCommit;
    readonly SemaphoreSlim slots = new(2, 2);
    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public FileTransferService(Action<string, string>? moveDirectory = null, Action<string>? afterStaging = null, Action<string>? afterCommit = null) {
        this.moveDirectory = moveDirectory ?? System.IO.Directory.Move;
        this.afterStaging = afterStaging; this.afterCommit = afterCommit;
    }

    public async Task<IResult> Paste(HttpContext context, PasteRequest request) {
        if (!await slots.WaitAsync(0, context.RequestAborted)) return Results.Json(new { error = "文件操作正在进行，请稍后重试。" }, statusCode: 429);
        try {
            var result = await Task.Run(() => Transfer(request, context.RequestAborted), context.RequestAborted);
            return Results.Ok(result);
        } catch (TransferException e) { return Results.Json(new { error = e.Message, destination = e.Destination }, statusCode: e.Status); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception or System.Security.SecurityException) {
            return Results.Json(new { error = "文件操作失败：" + e.Message }, statusCode: e is UnauthorizedAccessException or System.Security.SecurityException ? 403 : 400);
        } finally { slots.Release(); }
    }

    public IResult Download(HttpContext context, string? path) {
        try {
            var source = FullPath(path, "文件路径");
            var attributes = File.GetAttributes(source);
            if ((attributes & FileAttributes.Directory) != 0) throw new TransferException(400, "只能下载文件。" );
            var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(file, "application/octet-stream", Path.GetFileName(source), enableRangeProcessing: true);
        } catch (TransferException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) {
            return Results.Json(new { error = "无法下载文件：" + e.Message }, statusCode: e is FileNotFoundException or DirectoryNotFoundException ? 404 : e is UnauthorizedAccessException or System.Security.SecurityException ? 403 : 400);
        }
    }

    internal PasteResult Transfer(PasteRequest request, CancellationToken cancellation = default) {
        if (request.Operation is not ("copy" or "cut")) throw new TransferException(400, "无效的文件操作。");
        var source = FullPath(request.Source, "源路径");
        var destination = FullPath(request.Directory, "目标目录");
        FileAttributes sourceAttributes;
        try { sourceAttributes = File.GetAttributes(source); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new TransferException(404, "源文件或文件夹不存在。"); }
        FileAttributes destinationAttributes;
        try { destinationAttributes = File.GetAttributes(destination); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new TransferException(404, "目标目录不存在。"); }
        if ((destinationAttributes & FileAttributes.Directory) == 0) throw new TransferException(400, "粘贴位置不是文件夹。");
        var sourceRoot = Path.GetPathRoot(source);
        if (string.Equals(Path.TrimEndingDirectorySeparator(source), Path.TrimEndingDirectorySeparator(sourceRoot ?? ""), PathComparison)) throw new TransferException(400, "不能复制或剪切磁盘根目录。");
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source)) ?? throw new TransferException(400, "源路径没有上级目录。");
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new TransferException(400, "源文件名无效。");
        var directory = (sourceAttributes & FileAttributes.Directory) != 0;
        var sameParent = SameDirectory(parent, destination);
        if (sameParent && request.Operation == "cut") return new PasteResult(source, "cut", true);
        if (directory) {
            var physicalSource = PhysicalDirectory(source);
            var physicalDestination = PhysicalDirectory(destination);
            if (IsSameOrChild(physicalSource, physicalDestination)) throw new TransferException(400, "不能将文件夹粘贴到自身或其子文件夹中。");
        }
        var target = Path.Combine(destination, name);
        if (sameParent && request.Operation == "copy") target = CopyName(destination, name, directory);
        else if (Exists(target)) throw new TransferException(409, "目标目录中已存在同名文件或文件夹。" );
        cancellation.ThrowIfCancellationRequested();

        if (!directory && request.Operation == "cut") {
            try { File.Move(source, target, false); }
            catch (IOException) when (Exists(target) && !Exists(source)) { throw new TransferException(409, "移动结果无法确认，请检查目标目录。", target); }
            catch (IOException) when (Exists(target)) { throw new TransferException(409, "目标目录中已存在同名文件或文件夹，或文件复制未完成。", target); }
            if (File.Exists(source)) throw new TransferException(409, "目标文件已创建，但源文件未能移除，请检查两处文件。", target);
            return new PasteResult(target, "cut");
        }
        if (directory && request.Operation == "cut") {
            try { moveDirectory(source, target); return new PasteResult(target, "cut"); }
            catch (IOException e) when ((e.HResult & 0xFFFF) == 17) { /* Cross-volume move: copy safely, then remove verified entries. */ }
            catch (IOException) when (Exists(target)) { throw new TransferException(409, "目标目录中已存在同名文件或文件夹。" ); }
        }

        if ((sourceAttributes & FileAttributes.ReparsePoint) != 0 && directory) throw new TransferException(400, "暂不支持复制目录链接或跨磁盘移动目录链接。" );
        var staging = Path.Combine(destination, ".hlg-transfer-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try {
            if (directory) {
                CreateNewDirectory(staging); created = true;
                var manifest = CopyDirectory(source, staging, cancellation);
                afterStaging?.Invoke(source);
                if (request.Operation == "cut") VerifySnapshot(source, staging, manifest, cancellation);
                try { System.IO.Directory.Move(staging, target); created = false; }
                catch (IOException) when (Exists(target)) { throw new TransferException(409, "目标目录中已存在同名文件或文件夹。" ); }
                if (request.Operation == "cut") { afterCommit?.Invoke(source); RemoveCopiedSource(source, target, manifest); }
            } else {
                // CopyFile preserves Windows file data and metadata; the unique
                // staging name prevents partial files becoming the final target.
                using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                created = true; File.Copy(source, staging, true);
                cancellation.ThrowIfCancellationRequested();
                try { File.Move(staging, target, false); created = false; }
                catch (IOException) when (Exists(target)) { throw new TransferException(409, "目标目录中已存在同名文件或文件夹。" ); }
            }
            return new PasteResult(target, request.Operation);
        } finally { if (created) RemoveStaging(staging, directory); }
    }

    static string FullPath(string? value, string label) {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new TransferException(400, label + "必须是绝对路径。");
        return Path.GetFullPath(value);
    }
    static bool Exists(string path) => File.Exists(path) || System.IO.Directory.Exists(path);
    static bool SameDirectory(string first, string second) {
        if (string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), PathComparison)) return true;
        try { return string.Equals(Path.TrimEndingDirectorySeparator(PhysicalDirectory(first)), Path.TrimEndingDirectorySeparator(PhysicalDirectory(second)), PathComparison); }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException) { return false; }
    }
    static bool IsSameOrChild(string parent, string candidate) {
        parent = Path.TrimEndingDirectorySeparator(parent); candidate = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(parent, candidate, PathComparison) || candidate.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);
    }
    static string CopyName(string directory, string name, bool isDirectory) {
        var extension = isDirectory ? "" : Path.GetExtension(name);
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        for (var index = 1; index <= 10000; index++) {
            var suffix = index == 1 ? " - 副本" : " - 副本 (" + index + ")";
            var candidate = Path.Combine(directory, stem + suffix + extension);
            if (!Exists(candidate)) return candidate;
        }
        throw new TransferException(409, "无法为副本生成文件名。");
    }
    static CopyManifest CopyDirectory(string source, string destination, CancellationToken cancellation) {
        var manifest = new CopyManifest(new List<string> { "" }, new List<CopiedFile>());
        var pending = new Stack<string>(); pending.Push("");
        while (pending.TryPop(out var relative)) {
            cancellation.ThrowIfCancellationRequested();
            foreach (var item in new DirectoryInfo(Path.Combine(source, relative)).EnumerateFileSystemInfos()) {
                cancellation.ThrowIfCancellationRequested();
                var child = Path.Combine(relative, item.Name); var attributes = item.Attributes;
                if ((attributes & FileAttributes.Directory) != 0) {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new TransferException(400, "文件夹包含目录链接，暂不支持复制或跨磁盘剪切。" );
                    System.IO.Directory.CreateDirectory(Path.Combine(destination, child)); manifest.Directories.Add(child); pending.Push(child);
                } else { File.Copy(item.FullName, Path.Combine(destination, child), false); manifest.Files.Add(new CopiedFile(child)); }
            }
        }
        return manifest;
    }
    static void VerifySnapshot(string source, string copied, CopyManifest manifest, CancellationToken cancellation) {
        var dirs = new HashSet<string>(manifest.Directories, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var files = new HashSet<string>(manifest.Files.Select(f => f.RelativePath), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push("");
        while (pending.TryPop(out var relative)) {
            cancellation.ThrowIfCancellationRequested();
            if (!dirs.Remove(relative)) throw new TransferException(409, "源文件夹在复制期间发生变化，未执行剪切。");
            foreach (var item in new DirectoryInfo(Path.Combine(source, relative)).EnumerateFileSystemInfos()) {
                var child = Path.Combine(relative, item.Name);
                if ((item.Attributes & FileAttributes.Directory) != 0) {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new TransferException(409, "源文件夹在复制期间出现目录链接，未执行剪切。");
                    pending.Push(child);
                } else if (!files.Remove(child) || !EqualFiles(item.FullName, Path.Combine(copied, child))) throw new TransferException(409, "源文件在复制期间发生变化，未执行剪切。");
            }
        }
        if (dirs.Count != 0 || files.Count != 0) throw new TransferException(409, "源文件夹在复制期间发生变化，未执行剪切。");
    }
    static bool EqualFiles(string source, string target) {
        using var first = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var second = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        return first.Length == second.Length && CryptographicOperations.FixedTimeEquals(SHA256.HashData(first), SHA256.HashData(second));
    }
    static void RemoveCopiedSource(string source, string target, CopyManifest manifest) {
        try {
            foreach (var item in manifest.Files) {
                var original = Path.Combine(source, item.RelativePath); var copy = Path.Combine(target, item.RelativePath);
                using var originalGuard = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var copyGuard = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (originalGuard.Length != copyGuard.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(originalGuard), SHA256.HashData(copyGuard))) throw new IOException("源文件在移动期间发生变化。");
                var attributes = File.GetAttributes(original);
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(original, attributes & ~FileAttributes.ReadOnly);
                try { File.Delete(original); }
                catch {
                    if ((attributes & FileAttributes.ReadOnly) != 0) { try { File.SetAttributes(original, attributes); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
                    throw;
                }
            }
            foreach (var directory in manifest.Directories.OrderByDescending(p => p.Length)) System.IO.Directory.Delete(Path.Combine(source, directory), false);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            throw new TransferException(409, "目标已复制，但源文件夹未能完整移除。请检查源目录和目标目录：" + e.Message, target);
        }
    }
    static void RemoveStaging(string path, bool directory) {
        try {
            if (directory && System.IO.Directory.Exists(path)) {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                foreach (var file in System.IO.Directory.EnumerateFiles(path, "*", options)) File.SetAttributes(file, FileAttributes.Normal);
                System.IO.Directory.Delete(path, true);
            } else if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    static void CreateNewDirectory(string path) {
        if (OperatingSystem.IsWindows()) { if (!CreateDirectory(path, IntPtr.Zero)) throw new Win32Exception(); }
        else { if (System.IO.Directory.Exists(path)) throw new IOException("临时目录已存在。"); System.IO.Directory.CreateDirectory(path); }
    }
    static string PhysicalDirectory(string path) {
        if (!OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认目录的实际位置。");
        var buffer = new StringBuilder(512); var size = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认目录的实际位置。");
        if (size >= buffer.Capacity) { buffer.EnsureCapacity(checked((int)size + 1)); size = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0); if (size == 0 || size >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认目录的实际位置。"); }
        return buffer.ToString();
    }
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint="GetFinalPathNameByHandleW", CharSet=CharSet.Unicode, SetLastError=true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", EntryPoint="CreateDirectoryW", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateDirectory(string path, IntPtr security);
}

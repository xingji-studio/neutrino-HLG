using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

sealed record VsCodeInstallation(string Editor, string Cli, string Version, string Commit);
sealed record VsCodeStartOptions(string Directory, string BasePath, string CliDirectory, string DataDirectory, string TokenFile);

interface IVsCodeRuntime
{
    Task<VsCodeInstallation> Locate(CancellationToken cancellationToken);
    Process StartWeb(VsCodeInstallation installation, VsCodeStartOptions options);
    void OpenDesktop(VsCodeInstallation installation, string directory);
}

sealed class VsCodeRuntime : IVsCodeRuntime
{
    public async Task<VsCodeInstallation> Locate(CancellationToken cancellationToken) {
        foreach (var root in Roots().Distinct(StringComparer.OrdinalIgnoreCase)) {
            var editor = Path.Combine(root, "Code.exe");
            var cli = Path.Combine(root, "bin", "code-tunnel.exe");
            if (!File.Exists(editor) || !File.Exists(cli)) continue;
            var start = Hidden(cli);
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法读取 VS Code 版本。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var match = Regex.Match(await output, @"\b(\d+\.\d+\.\d+)[^\r\n]*commit\s+([a-f0-9]{40})\b", RegexOptions.IgnoreCase);
                if (process.ExitCode == 0 && match.Success)
                    return new(editor, cli, match.Groups[1].Value, match.Groups[2].Value.ToLowerInvariant());
                _ = await error;
            } catch (OperationCanceledException) { Kill(process); cancellationToken.ThrowIfCancellationRequested(); }
        }
        throw new InvalidOperationException("未找到支持 serve-web 的官方 VS Code。请先安装或更新 Windows 版 VS Code；自定义安装可设置 HLG_VSCODE_PATH 为 Code.exe 路径。");
    }

    static IEnumerable<string> Roots() {
        var configured = Environment.GetEnvironmentVariable("HLG_VSCODE_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) {
            var full = Path.GetFullPath(configured.Trim('"'));
            yield return Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code");
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            var path = directory.Trim().Trim('"');
            if (File.Exists(Path.Combine(path, "code.cmd"))) yield return Path.GetFullPath(Path.Combine(path, ".."));
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine }) {
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Code.exe");
            if (key?.GetValue(null) is string path && Path.IsPathFullyQualified(path.Trim('"')))
                yield return Path.GetDirectoryName(path.Trim('"'))!;
        }
    }

    internal static ProcessStartInfo Hidden(string executable) {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var name in new[] { "ELECTRON_RUN_AS_NODE", "VSCODE_DEV", "VSCODE_IPC_HOOK_CLI" }) start.Environment.Remove(name);
        return start;
    }

    public Process StartWeb(VsCodeInstallation installation, VsCodeStartOptions options) {
        var start = Hidden(installation.Cli);
        ConfigureDownloadProxy(start);
        foreach (var argument in new[] {
            "--cli-data-dir", options.CliDirectory, "serve-web", "--host", "127.0.0.1", "--port", "0",
            "--connection-token-file", options.TokenFile, "--server-base-path", options.BasePath.TrimEnd('/'),
            "--server-data-dir", options.DataDirectory, "--default-folder", options.Directory,
            "--commit-id", installation.Commit, "--accept-server-license-terms"
        }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动 VS Code 网页服务。");
    }

    static void ConfigureDownloadProxy(ProcessStartInfo start) {
        // The standalone CLI does not consistently inherit Windows' system
        // proxy. Respect explicit CLI proxy variables; otherwise bridge the
        // current user's proxy so first-use component downloads also work.
        if (new[] { "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" }.Any(name => start.Environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))) return;
        try {
            var target = new Uri("https://update.code.visualstudio.com");
            var proxy = HttpClient.DefaultProxy.GetProxy(target);
            if (proxy is null || proxy.Authority == target.Authority || proxy.Scheme is not ("http" or "https")) return;
            start.Environment["HTTPS_PROXY"] = proxy.AbsoluteUri;
            var bypass = start.Environment.TryGetValue("NO_PROXY", out var existing) ? existing : null;
            start.Environment["NO_PROXY"] = string.Join(',', new[] { bypass, "127.0.0.1", "localhost", "::1" }.Where(value => !string.IsNullOrEmpty(value)));
        } catch (Exception e) when (e is System.Net.WebException or InvalidOperationException or PlatformNotSupportedException) { }
    }

    public void OpenDesktop(VsCodeInstallation installation, string directory) {
        var start = new ProcessStartInfo(installation.Editor) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var name in new[] { "ELECTRON_RUN_AS_NODE", "VSCODE_DEV", "VSCODE_IPC_HOOK_CLI" }) start.Environment.Remove(name);
        start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法打开电脑端 VS Code。");
    }

    internal static void Kill(Process? process) {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}

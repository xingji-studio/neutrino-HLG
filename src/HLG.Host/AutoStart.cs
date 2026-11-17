using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

interface IAutoStartControl
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

interface IAutoStartTaskStore
{
    AutoStartTask? Read();
    void Register(string executable);
    void Delete();
}

sealed record AutoStartTask(bool Enabled, string Executable);

sealed class AutoStartRegistration : IAutoStartControl
{
    readonly IAutoStartTaskStore tasks;
    readonly AutoStartFiles files;
    readonly string? mutexName;

    public AutoStartRegistration() : this(
        new WindowsAutoStartTaskStore(CurrentUserSid()),
        new AutoStartFiles(AppContext.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neutrino HLG", "AutoStart")),
        @"Local\NeutrinoHLG-AutoStart-" + CurrentUserSid()) { }

    static string CurrentUserSid() { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 用户。"); }

    internal AutoStartRegistration(IAutoStartTaskStore tasks, AutoStartFiles files, string? mutexName = null) { this.tasks = tasks; this.files = files; this.mutexName = mutexName; }
    public bool IsEnabled() => tasks.Read()?.Enabled == true;
    public void SetEnabled(bool enabled) => WithLock(() => { if (enabled) tasks.Register(files.Install()); else tasks.Delete(); });

    // A newly launched version updates an existing opt-in task. Autostarted
    // copies already live in the protected directory and need no replacement.
    internal void RefreshIfEnabled() {
        if (files.IsInstalledCopy) return;
        WithLock(() => {
            if (tasks.Read()?.Enabled != true) return;
            var executable = files.Install();
            var current = tasks.Read();
            if (current?.Enabled == true && !string.Equals(current.Executable, executable, StringComparison.OrdinalIgnoreCase)) tasks.Register(executable);
        });
    }

    void WithLock(Action action) {
        if (mutexName is null) { action(); return; }
        using var mutex = new Mutex(false, mutexName); var acquired = false;
        try {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("等待开机自启设置超时，请重试。");
            action();
        } finally { if (acquired) mutex.ReleaseMutex(); }
    }
}

sealed class AutoStartFiles
{
    readonly string source;
    readonly string root;
    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public AutoStartFiles(string source, string root) { this.source = Path.GetFullPath(source); this.root = Path.GetFullPath(root); }
    public bool IsInstalledCopy => source.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

    public string Install() {
        if (!File.Exists(Path.Combine(source, "HLG.Host.exe")) || !File.Exists(Path.Combine(source, "HLG.Host.dll")))
            throw new FileNotFoundException("找不到桌面程序文件，无法启用开机自启。");
        if (IsInstalledCopy) return Path.Combine(source, "HLG.Host.exe");
        var fingerprint = Fingerprint(source);
        var target = Path.Combine(root, fingerprint);
        var executable = Path.Combine(target, "HLG.Host.exe");
        if (File.Exists(Path.Combine(target, ".complete")) && File.Exists(executable)) return executable;
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        try {
            Directory.CreateDirectory(staging);
            foreach (var relative in EnumerateFiles(source)) {
                var destination = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(source, relative), destination, false);
            }
            if (Fingerprint(staging) != fingerprint) throw new IOException("程序文件在复制期间发生变化，请重试。");
            File.WriteAllText(Path.Combine(staging, ".complete"), fingerprint);
            try { Directory.Move(staging, target); }
            catch (IOException) when (File.Exists(Path.Combine(target, ".complete")) && File.Exists(executable)) { }
            return executable;
        } finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    static string Fingerprint(string directory) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        foreach (var relative in EnumerateFiles(directory)) {
            var path = Path.Combine(directory, relative);
            var name = Encoding.UTF8.GetBytes(relative.Replace('\\', '/'));
            hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
            using var stream = File.OpenRead(path);
            hash.AppendData(BitConverter.GetBytes(stream.Length));
            int read; while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset())[..24];
    }

    static IEnumerable<string> EnumerateFiles(string directory) {
        var pending = new Stack<string>(); pending.Push(""); var files = new List<string>();
        while (pending.TryPop(out var relativeDirectory)) {
            foreach (var entry in Directory.EnumerateFileSystemEntries(Path.Combine(directory, relativeDirectory))) {
                var relative = Path.Combine(relativeDirectory, Path.GetFileName(entry));
                if (string.IsNullOrEmpty(relativeDirectory) && (Path.GetFileName(entry).Equals("obj", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(entry) == ".complete")) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("程序目录中包含链接，无法安全地启用开机自启：" + relative);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(relative); else files.Add(relative);
            }
        }
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal);
    }
}

sealed class WindowsAutoStartTaskStore : IAutoStartTaskStore
{
    const int FileNotFound = unchecked((int)0x80070002);
    internal const string TaskSecurity = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)";
    readonly string sid;
    readonly string name;
    readonly bool highest;
    readonly string? security;

    public WindowsAutoStartTaskStore(string sid) : this(sid, "Neutrino HLG Autostart-" + sid, true, TaskSecurity) { }
    internal WindowsAutoStartTaskStore(string sid, string name, bool highest, string? security) { this.sid = sid; this.name = name; this.highest = highest; this.security = security; }

    public AutoStartTask? Read() => WithFolder(folder => {
        object? task = null;
        try {
            task = folder.GetTask(name);
            dynamic registered = task;
            var xml = XDocument.Parse((string)registered.Xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var executable = (string?)xml.Root?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command") ?? "";
            return new AutoStartTask((bool)registered.Enabled, executable);
        } catch (Exception e) when (e is FileNotFoundException || e is COMException { HResult: FileNotFound }) { return null; }
        finally { Release(task); }
    });

    public void Register(string executable) => WithFolder(folder => {
        object? registered = null;
        try { registered = folder.RegisterTask(name, TaskXml(sid, executable, highest), 6, sid, null, 3, security); return true; }
        finally { Release(registered); }
    });

    public void Delete() => WithFolder(folder => {
        try { folder.DeleteTask(name, 0); } catch (Exception e) when (e is FileNotFoundException || e is COMException { HResult: FileNotFound }) { }
        return true;
    });

    internal static string TaskXml(string sid, string executable, bool highest) {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", "Neutrino HLG: 当前用户登录后启动桌面主机。")),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger", new XElement(ns + "Enabled", "true"), new XElement(ns + "UserId", sid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "HLG"), new XElement(ns + "UserId", sid), new XElement(ns + "LogonType", "InteractiveToken"), new XElement(ns + "RunLevel", highest ? "HighestAvailable" : "LeastPrivilege"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(ns + "DisallowStartIfOnBatteries", "false"), new XElement(ns + "StopIfGoingOnBatteries", "false"), new XElement(ns + "StartWhenAvailable", "true"), new XElement(ns + "Enabled", "true"), new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "HLG"), new XElement(ns + "Exec", new XElement(ns + "Command", executable), new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable) ?? ""))));
        return new XDocument(new XDeclaration("1.0", "UTF-16", null), root).ToString();
    }

    static T WithFolder<T>(Func<dynamic, T> action) {
        object? service = null, folder = null;
        try {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            dynamic scheduler = service; scheduler.Connect(); folder = scheduler.GetFolder("\\");
            return action(folder);
        } finally { Release(folder); Release(service); }
    }
    static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

using System.Security.Principal;
using System.Security.AccessControl;
using System.Xml.Linq;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var temp = Path.Combine(AppContext.BaseDirectory, "autostart-test-" + Guid.NewGuid().ToString("N"));
var source = Path.Combine(temp, "source"); var install = Path.Combine(temp, "protected");
Directory.CreateDirectory(Path.Combine(source, "wwwroot")); Directory.CreateDirectory(Path.Combine(source, "obj"));
try {
    File.WriteAllText(Path.Combine(source, "HLG.Host.exe"), "host");
    File.WriteAllText(Path.Combine(source, "HLG.Host.dll"), "assembly");
    File.WriteAllText(Path.Combine(source, "wwwroot", "app.js"), "version one");
    File.WriteAllText(Path.Combine(source, "obj", "intermediate"), "excluded");
    var tasks = new FakeTasks(); var files = new AutoStartFiles(source, install); var setting = new AutoStartRegistration(tasks, files);
    Check(!setting.IsEnabled(), "Autostart must be disabled when no task exists.");
    setting.RefreshIfEnabled(); Check(!Directory.Exists(install), "Disabled autostart must not install files.");
    setting.SetEnabled(true);
    Check(setting.IsEnabled() && tasks.Current is not null, "Enabling must register a task.");
    var first = tasks.Current!.Executable; var firstDir = Path.GetDirectoryName(first)!;
    Check(File.Exists(first) && File.Exists(Path.Combine(firstDir, ".complete")), "Protected copy is incomplete.");
    Check(File.ReadAllText(Path.Combine(firstDir, "wwwroot", "app.js")) == "version one", "Web assets must be included.");
    Check(!Directory.Exists(Path.Combine(firstDir, "obj")), "Build intermediates must be excluded.");
    var firstRegistrations = tasks.Registrations; setting.RefreshIfEnabled();
    Check(tasks.Registrations == firstRegistrations, "Unchanged files must not rewrite the task.");
    File.WriteAllText(Path.Combine(source, "wwwroot", "app.js"), "version two");
    setting.RefreshIfEnabled();
    Check(tasks.Current!.Executable != first && File.ReadAllText(Path.Combine(Path.GetDirectoryName(tasks.Current.Executable)!, "wwwroot", "app.js")) == "version two", "Updates to web files must update the task's protected copy.");
    var count = tasks.Registrations;
    new AutoStartRegistration(tasks, new AutoStartFiles(Path.GetDirectoryName(tasks.Current.Executable)!, install)).RefreshIfEnabled();
    Check(tasks.Registrations == count, "An autostarted copy must not re-register itself.");
    var previous = tasks.Current.Executable; tasks.RejectRegistration = true;
    File.WriteAllText(Path.Combine(source, "wwwroot", "app.js"), "version three");
    try { setting.RefreshIfEnabled(); throw new Exception("A failed update must be reported."); }
    catch (InvalidOperationException e) when (e.Message == "injected") { }
    Check(tasks.Current?.Executable == previous, "A failed update must preserve the last working task.");
    tasks.RejectRegistration = false;
    setting.SetEnabled(false); Check(!setting.IsEnabled(), "Disabling must remove the task.");
    setting.SetEnabled(false); Check(!setting.IsEnabled(), "Disabling twice must be safe.");
    tasks.RejectRegistration = true;
    try { setting.SetEnabled(true); throw new Exception("A scheduler failure must be reported."); }
    catch (InvalidOperationException e) when (e.Message == "injected") { }
    Check(!setting.IsEnabled(), "A failed registration must not report success.");

    using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User?.Value ?? throw new Exception("No Windows SID.");
    const string taskPath = @"C:\Program Files\Neutrino HLG\host & UI.exe";
    var xml = XDocument.Parse(WindowsAutoStartTaskStore.TaskXml(sid, taskPath, true));
    XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    Check((string?)xml.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId") == sid, "Logon trigger must target the current user.");
    Check((string?)xml.Descendants(ns + "LogonType").Single() == "InteractiveToken" && (string?)xml.Descendants(ns + "RunLevel").Single() == "HighestAvailable", "Task must run interactively with administrator privileges.");
    Check((string?)xml.Descendants(ns + "Command").Single() == taskPath, "Executable path must round-trip through task XML.");
    Check((string?)xml.Descendants(ns + "DisallowStartIfOnBatteries").Single() == "false" && (string?)xml.Descendants(ns + "ExecutionTimeLimit").Single() == "PT0S", "Battery use and unlimited runtime must be enabled.");
    var security = new RawSecurityDescriptor(WindowsAutoStartTaskStore.TaskSecurity);
    Check(security.Owner?.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) == true && security.DiscretionaryAcl?.Count == 2, "The task ACL must restrict ownership and access to administrators and system.");

    // Register an inert, least-privilege task with a unique name to exercise
    // the real Task Scheduler COM interface without touching the user's task.
    var native = new WindowsAutoStartTaskStore(sid, "HLG test " + Guid.NewGuid().ToString("N"), false, $"O:{sid}D:P(A;;FA;;;SY)(A;;FA;;;{sid})");
    try {
        Check(native.Read() is null, "Temporary task should not exist yet.");
        var systemExe = Path.Combine(Environment.SystemDirectory, "where.exe");
        native.Register(systemExe);
        var registered = native.Read();
        Check(registered?.Enabled == true && string.Equals(registered.Executable, systemExe, StringComparison.OrdinalIgnoreCase), "Native task registration or lookup failed.");
        native.Delete(); Check(native.Read() is null, "Native task removal failed.");
    } finally { native.Delete(); }
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) {
        var elevated = new WindowsAutoStartTaskStore(sid, "HLG elevated test " + Guid.NewGuid().ToString("N"), true, WindowsAutoStartTaskStore.TaskSecurity);
        try {
            elevated.Register(Path.Combine(Environment.SystemDirectory, "where.exe"));
            Check(elevated.Read()?.Enabled == true, "Elevated task with protected ACL was not registered.");
        } finally { elevated.Delete(); }
    } else Console.WriteLine("Elevated registration test skipped: run this test as administrator to exercise the protected task ACL.");
    Console.WriteLine("Autostart task configuration, protected copies, updates and native scheduler registration passed.");
} finally { Directory.Delete(temp, true); }

sealed class FakeTasks : IAutoStartTaskStore {
    public AutoStartTask? Current { get; private set; }
    public int Registrations { get; private set; }
    public bool RejectRegistration { get; set; }
    public AutoStartTask? Read() => Current;
    public void Register(string executable) { if (RejectRegistration) throw new InvalidOperationException("injected"); Registrations++; Current = new(true, executable); }
    public void Delete() => Current = null;
}

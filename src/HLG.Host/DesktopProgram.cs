using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

static class DesktopProgram
{
    [STAThread]
    static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (args is ["--secure-service"] || args.Length > 0 && args[0] == "--secure-worker") {
            try { if (args[0] == "--secure-service") SecureService.RunService(); else SecureDesktopWorker.Run(args); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException or TimeoutException) { Environment.ExitCode = 1; }
            return;
        }
        using (var identity = WindowsIdentity.GetCurrent()) {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) {
                try {
                    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定启动程序路径。");
                    var elevate = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
                    if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
                        elevate.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("无法确定程序路径。"));
                    foreach (var arg in args) elevate.ArgumentList.Add(arg);
                    Process.Start(elevate);
                } catch (Exception e) {
                    try { using var startupLog = new FileLog(Path.Combine(NeutrinoPaths.Home, "hlg", "logs")); startupLog.Write(LogLevel.Error, "权限", "管理员权限申请失败", e); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                    MessageBox.Show("本程序必须以管理员权限运行，否则可能无法正常访问电脑目录。未获得管理员权限，已拒绝启动。", "Neutrino HLG", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.ExitCode = 1;
                }
                return;
            }
        }
        FileLog? log = null;
        try {
            log = new FileLog(Path.Combine(NeutrinoPaths.Home, "hlg", "logs"));
            using var currentIdentity = WindowsIdentity.GetCurrent();
            var userId = currentIdentity.User?.Value ?? Environment.UserName;
            var name = @"Local\NeutrinoHLG-" + userId;
            using var mutex = new Mutex(true, name, out var first);
            if (!first) {
                try { using var existing = EventWaitHandle.OpenExisting(name + "-show"); existing.Set(); log.Write(LogLevel.Information, "主机", "已打开现有窗口"); }
                catch (WaitHandleCannotBeOpenedException) { log.Write(LogLevel.Warning, "主机", "现有窗口尚未准备就绪"); }
                return;
            }
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
            var capturedLog = log;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => { capturedLog.Write(LogLevel.Error, "桌面", "界面发生异常", e.Exception); MessageBox.Show("界面发生异常，详情请查看日志。", "Neutrino HLG", MessageBoxButtons.OK, MessageBoxIcon.Error); };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => capturedLog.Write(LogLevel.Critical, "主机", "未处理的异常", e.ExceptionObject as Exception);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var devices = new DeviceConnections(message => capturedLog.Write(LogLevel.Information, "设备", message));
            var https = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HLG_HTTPS_CERT"));
            // HTTP device tokens may have travelled in plaintext. Do not accept
            // them on the HTTPS host that can expose the Windows sign-in screen.
            var trustFile = https ? "trusted-devices-https.json" : "trusted-devices.json";
            var pairing = new PairingAuth(new TrustedDevices(Path.Combine(NeutrinoPaths.Home, "hlg", trustFile)), devices, message => capturedLog.Write(LogLevel.Warning, "配对", message));
            var unlock = new UnlockSettings(Path.Combine(NeutrinoPaths.Home, "hlg", "remote-unlock.json"));
            using var secureDesktop = new SecureDesktopHub(unlock, log, https);
            if (https) _ = Task.Run(() => { try { if (unlock.Read().Enabled) SecureService.InstallAndStart(); } catch (Exception e) { capturedLog.Write(LogLevel.Error, "远程解锁", "无法启动安全桌面服务", e); } });
            var host = HostApplication.Create(args, log, pairing, secureDesktop);
            using var context = new DesktopContext(host, pairing, devices, log, unlock, https);
            var registration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => context.ShowWindow(), null, Timeout.Infinite, false);
            log.Write(LogLevel.Information, "主机", "正在启动桌面控制面板");
            try { Application.Run(context); } finally { registration.Unregister(null); }
            try { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception e) { log.Write(LogLevel.Error, "主机", "关闭主机失败", e); }
        } catch (Exception e) {
            log?.Write(LogLevel.Critical, "主机", "启动失败", e);
            MessageBox.Show("Neutrino HLG 启动失败：" + e.Message + (log is null ? "" : "\n日志：" + log.Path), "Neutrino HLG", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        } finally { log?.Dispose(); }
    }
}

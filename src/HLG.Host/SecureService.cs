using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

// This service has no network listener. Only an elevated process in the active
// console session can ask it to start the fixed, bundled secure-desktop worker.
static class SecureService
{
    internal const string Name = "NeutrinoHLGSecureDesktop";
    internal const string PipeName = "NeutrinoHLG.SecureDesktop.v1";
    const uint ServiceWin32OwnProcess = 0x10, ServiceStartPending = 2, ServiceStopPending = 3, ServiceRunning = 4, ServiceStopped = 1;
    const uint ControlStop = 1, ControlShutdown = 5, AcceptStop = 1, AcceptShutdown = 4;
    const uint ScConnect = 1, ScCreateService = 2, QueryStatus = 4, Start = 0x10, Stop = 0x20, ChangeConfig = 2;
    internal const PipeAccessRights ClientRights = PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.WriteAttributes | PipeAccessRights.ReadExtendedAttributes | PipeAccessRights.WriteExtendedAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
    static readonly ServiceMainCallback serviceMain = ServiceMain;
    static readonly ServiceControlCallback serviceControl = ServiceControl;
    static readonly object installGate = new();
    static CancellationTokenSource? stopping;
    static IntPtr statusHandle;

    public static void RunService() {
        EnsureSystem();
        var table = new[] { new ServiceTableEntry { Name = Name, Main = Marshal.GetFunctionPointerForDelegate(serviceMain) }, new ServiceTableEntry() };
        if (!StartServiceCtrlDispatcher(table)) throw new Win32Exception();
    }

    static void ServiceMain(int count, IntPtr arguments) {
        statusHandle = RegisterServiceCtrlHandlerEx(Name, serviceControl, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero) return;
        stopping = new CancellationTokenSource();
        Report(ServiceStartPending);
        try {
            Report(ServiceRunning, AcceptStop | AcceptShutdown);
            Serve(stopping.Token).GetAwaiter().GetResult();
        } catch (OperationCanceledException) { }
        catch { Report(ServiceStopped, error: 1); return; }
        finally { stopping.Dispose(); stopping = null; }
        Report(ServiceStopped);
    }

    static int ServiceControl(uint control, uint eventType, IntPtr eventData, IntPtr context) {
        if (control is ControlStop or ControlShutdown) { Report(ServiceStopPending); stopping?.Cancel(); }
        return 0;
    }

    static void Report(uint state, uint accepted = 0, uint error = 0) {
        var status = new ServiceStatus { Type = ServiceWin32OwnProcess, State = state, Accepted = accepted, Win32ExitCode = error, WaitHint = state is ServiceStartPending or ServiceStopPending ? 5000u : 0 };
        SetServiceStatus(statusHandle, ref status);
    }

    static PipeSecurity Security() {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), ClientRights, AccessControlType.Allow));
        return security;
    }
    internal static PipeSecurity WorkerSecurity() {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    static async Task Serve(CancellationToken cancel) {
        var slots = new SemaphoreSlim(4, 4);
        while (!cancel.IsCancellationRequested) {
            await slots.WaitAsync(cancel);
            NamedPipeServerStream? pipe = null;
            try {
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, Security());
                await pipe.WaitForConnectionAsync(cancel);
            } catch { pipe?.Dispose(); slots.Release(); throw; }
            var connected = pipe;
            _ = Task.Run(async () => { try { await Handle(connected, cancel); } finally { slots.Release(); } }, CancellationToken.None);
        }
    }

    static async Task Handle(NamedPipeServerStream pipe, CancellationToken cancel) {
        using (pipe) {
            var authenticated = false; var acknowledged = false;
            try {
                using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var hello = new byte[1];
                await pipe.ReadExactlyAsync(hello, helloTimeout.Token);
                if (hello[0] != (byte)'H') return;
                var elevated = false; SecurityIdentifier? clientSid = null;
                pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(true); elevated = identity is not null && new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); clientSid = identity?.User; });
                if (!elevated || clientSid is null || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || !ProcessIdToSessionId(pid, out var sessionId) || sessionId != SessionLock.ActiveConsoleSessionId || SessionLock.Query(sessionId) != SessionLockState.Locked) return;
                if (!WTSQueryUserToken(sessionId, out var userToken)) return;
                using var token = userToken;
                using (var user = new WindowsIdentity(token.DangerousGetHandle())) if (user.User != clientSid) return;
                authenticated = true;
                var workerName = "NeutrinoHLG.Worker." + Guid.NewGuid().ToString("N");
                using var worker = NamedPipeServerStreamAcl.Create(workerName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, WorkerSecurity());
                using var child = LaunchWorker(sessionId, workerName);
                try {
                    helloTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await worker.WaitForConnectionAsync(helloTimeout.Token);
                    if (!GetNamedPipeClientProcessId(worker.SafePipeHandle, out var workerPid) || workerPid != child.Id || !ProcessIdToSessionId(workerPid, out var workerSession) || workerSession != sessionId) return;
                    await pipe.WriteAsync(new byte[] { (byte)'Y' }, helloTimeout.Token);
                    await pipe.FlushAsync(helloTimeout.Token);
                    acknowledged = true;
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    var toWorker = PumpRequests(pipe, worker, token, sessionId, linked.Token);
                    var fromWorker = worker.CopyToAsync(pipe, linked.Token);
                    await Task.WhenAny(toWorker, fromWorker);
                    linked.Cancel();
                    if (!child.HasExited) child.Kill(true);
                    worker.Dispose();
                    try { await Task.WhenAll(toWorker, fromWorker).WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or TimeoutException) { }
                } finally { if (!child.HasExited) child.Kill(true); }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or OperationCanceledException or ArgumentException) {
                if (authenticated && !acknowledged && pipe.IsConnected) try {
                    var failure = new byte[5]; failure[0] = (byte)'E';
                    BinaryPrimitives.WriteInt32LittleEndian(failure.AsSpan(1), e is Win32Exception win32 ? win32.NativeErrorCode : e is OperationCanceledException ? 1460 : 0);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await pipe.WriteAsync(failure, timeout.Token); await pipe.FlushAsync(timeout.Token);
                } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    static async Task PumpRequests(Stream source, Stream destination, SafeAccessTokenHandle userToken, uint sessionId, CancellationToken cancel) {
        var chunk = new byte[4096]; var line = new byte[65536]; var length = 0;
        try {
            while (!cancel.IsCancellationRequested) {
                var count = await source.ReadAsync(chunk, cancel);
                if (count == 0) return;
                for (var i = 0; i < count; i++) {
                    if (chunk[i] != (byte)'\n') { if (length == line.Length) return; line[length++] = chunk[i]; continue; }
                    if (line.AsSpan(0, length).SequenceEqual("{\"type\":\"sas\"}"u8)) {
                        if (sessionId == SessionLock.ActiveConsoleSessionId && SessionLock.Query(sessionId) == SessionLockState.Locked)
                            try { WindowsIdentity.RunImpersonated(userToken, () => SendSAS(true)); } catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or UnauthorizedAccessException) { }
                    } else {
                        await destination.WriteAsync(line.AsMemory(0, length), cancel);
                        await destination.WriteAsync("\n"u8.ToArray(), cancel);
                        await destination.FlushAsync(cancel);
                    }
                    CryptographicOperations.ZeroMemory(line.AsSpan(0, length)); length = 0;
                }
                CryptographicOperations.ZeroMemory(chunk.AsSpan(0, count));
            }
        } finally { CryptographicOperations.ZeroMemory(chunk); CryptographicOperations.ZeroMemory(line); }
    }

    static Process LaunchWorker(uint sessionId, string pipeName) {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0002 | 0x0008 | 0x0080 | 0x0100, out var source)) throw new Win32Exception();
        using (source) {
            if (!DuplicateTokenEx(source, 0x02000000, IntPtr.Zero, 2, 1, out var token)) throw new Win32Exception();
            using (token) {
                if (!SetTokenInformation(token, 12, ref sessionId, sizeof(uint))) throw new Win32Exception();
                var exe = Path.Combine(AppContext.BaseDirectory, "HLG.Host.exe");
                var command = new StringBuilder($"\"{exe}\" --secure-worker {pipeName} {sessionId}");
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                if (!CreateProcessAsUser(token, exe, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var process)) throw new Win32Exception();
                try { return Process.GetProcessById((int)process.ProcessId); }
                finally { CloseHandle(process.Process); CloseHandle(process.Thread); }
            }
        }
    }

    internal static void EnsureSystem() {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true) throw new UnauthorizedAccessException("安全桌面组件只能由 Windows 系统服务启动。");
    }

    // Authenticate the pipe server against SCM before sending any input or secret.
    public static async Task<NamedPipeClientStream> Connect(CancellationToken cancel) {
        var pipe = new NamedPipeClientStream(".", PipeName, ClientRights, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation, HandleInheritability.None);
        try {
            await pipe.ConnectAsync(3000, cancel);
            if (!VerifyServer(pipe)) throw new UnauthorizedAccessException("无法验证安全桌面服务。");
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await pipe.WriteAsync(new byte[] { (byte)'H' }, helloTimeout.Token);
            await pipe.FlushAsync(helloTimeout.Token);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, helloTimeout.Token);
            if (acknowledgement[0] == (byte)'E') {
                var error = new byte[4]; await pipe.ReadExactlyAsync(error, helloTimeout.Token);
                var code = BinaryPrimitives.ReadInt32LittleEndian(error);
                throw new Win32Exception(code, "无法启动 Windows 登录画面组件，错误码：" + code);
            }
            if (acknowledgement[0] != (byte)'Y') throw new UnauthorizedAccessException("安全桌面服务拒绝连接。");
            return pipe;
        } catch (OperationCanceledException e) when (!cancel.IsCancellationRequested) { pipe.Dispose(); throw new TimeoutException("等待安全桌面服务响应超时。", e); }
        catch { pipe.Dispose(); throw; }
    }

    internal static bool VerifyServer(NamedPipeClientStream pipe) {
        var expected = ServiceProcessId();
        return expected != 0 && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) && expected == actual && IsExpectedService(actual);
    }

    static bool IsExpectedService(uint pid) {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process.DangerousGetHandle(), 0x0008, out var token)) return false;
        using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) {
            if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true) return false;
        }
        var path = new StringBuilder(32768); var length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)) return false;
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neutrino HLG", "SecureHost")) + Path.DirectorySeparatorChar;
        var image = Path.GetFullPath(path.ToString());
        return image.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(image).Equals("HLG.Host.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static uint ServiceProcessId() {
        using var manager = OpenSCManager(null, null, ScConnect);
        if (manager.IsInvalid) return 0;
        using var service = OpenService(manager, Name, QueryStatus);
        if (service.IsInvalid || !QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _)) return 0;
        return status.State == ServiceRunning ? status.ProcessId : 0;
    }

    public static void InstallAndStart() { lock (installGate) InstallAndStartCore(); }
    static void InstallAndStartCore() {
        var source = AppContext.BaseDirectory;
        var assembly = Path.Combine(source, "HLG.Host.dll");
        var exe = Path.Combine(source, "HLG.Host.exe");
        if (!File.Exists(assembly) || !File.Exists(exe)) throw new FileNotFoundException("找不到桌面程序文件，无法安装安全服务。");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))).Substring(0, 20);
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Neutrino HLG", "SecureHost", hash);
        var binary = "\"" + Path.Combine(target, "HLG.Host.exe") + "\" --secure-service";
        using var manager = OpenSCManager(null, null, ScConnect | ScCreateService);
        if (manager.IsInvalid) throw new Win32Exception();
        using var service = OpenService(manager, Name, QueryStatus | Start | Stop | ChangeConfig);
        var missing = service.IsInvalid;
        if (missing && Marshal.GetLastWin32Error() != 1060) throw new Win32Exception();
        if (!missing) StopAndWait(service);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(target, name), true);
        }
        if (missing) {
            using var created = CreateService(manager, Name, "Neutrino HLG 安全桌面", QueryStatus | Start | Stop | ChangeConfig, ServiceWin32OwnProcess, 3, 1, binary, null, IntPtr.Zero, null, "LocalSystem", null);
            if (created.IsInvalid) throw new Win32Exception();
            StartAndWait(created);
        } else {
            if (!ChangeServiceConfig(service, uint.MaxValue, 3, uint.MaxValue, binary, null, IntPtr.Zero, null, "LocalSystem", null, null)) throw new Win32Exception();
            StartAndWait(service);
        }
    }

    public static void StopIfRunning() { lock (installGate) StopIfRunningCore(); }
    static void StopIfRunningCore() {
        using var manager = OpenSCManager(null, null, ScConnect);
        if (manager.IsInvalid) throw new Win32Exception();
        using var service = OpenService(manager, Name, QueryStatus | Stop);
        if (service.IsInvalid) { if (Marshal.GetLastWin32Error() == 1060) return; throw new Win32Exception(); }
        StopAndWait(service);
    }

    static ServiceStatusProcess State(SafeServiceHandle service) {
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _)) throw new Win32Exception();
        return status;
    }
    static void StopAndWait(SafeServiceHandle service) {
        if (State(service).State == ServiceStopped) return;
        if (!ControlService(service, ControlStop, out _) && Marshal.GetLastWin32Error() != 1062) throw new Win32Exception();
        WaitFor(service, ServiceStopped);
    }
    static void StartAndWait(SafeServiceHandle service) {
        if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) throw new Win32Exception();
        WaitFor(service, ServiceRunning);
    }
    static void WaitFor(SafeServiceHandle service, uint target) {
        for (var i = 0; i < 100; i++) { if (State(service).State == target) return; Thread.Sleep(100); }
        throw new TimeoutException("等待安全桌面服务超时。");
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ServiceMainCallback(int count, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate int ServiceControlCallback(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ServiceTableEntry { public string? Name; public IntPtr Main; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatus { public uint Type, State, Accepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] struct ServiceStatusProcess { public uint Type, State, Accepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2; public IntPtr ReservedPtr, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid { SafeServiceHandle() : base(true) { } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr RegisterServiceCtrlHandlerEx(string name, ServiceControlCallback callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeServiceHandle CreateService(SafeServiceHandle manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool ChangeServiceConfig(SafeServiceHandle service, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool StartService(SafeServiceHandle service, int count, IntPtr args);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool ControlService(SafeServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool QueryServiceStatusEx(SafeServiceHandle service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);
    [DllImport("wtsapi32.dll", SetLastError=true)] static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", EntryPoint="QueryFullProcessImageNameW", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError=true)] static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, ref uint value, int length);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("sas.dll")] static extern void SendSAS(bool asUser);
}

using System.ComponentModel;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Win32.SafeHandles;

static class TerminalHost
{
    public record Shell(string Id, string Name, string Executable, string Arguments);
    public static IReadOnlyList<Shell> Shells()
    {
        var sys = Environment.SystemDirectory;
        var candidates = new List<Shell> {
            new("powershell", "Windows PowerShell", Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoLogo"),
            new("cmd", "命令提示符", Path.Combine(sys, "cmd.exe"), ""),
            new("wsl", "WSL", Path.Combine(sys, "wsl.exe"), ""),
            new("pwsh", "PowerShell 7", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"), "-NoLogo"),
            new("git-bash", "Git Bash", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"), "--login -i")
        };
        return candidates.Where(s => File.Exists(s.Executable)).ToArray();
    }

    public static async Task Handle(HttpContext context)
    {
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!string.Equals(context.Request.Headers.Origin.ToString(), expected, StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var shell = Shells().FirstOrDefault(s => s.Id == context.Request.Query["shell"].ToString());
        if (shell is null) { context.Response.StatusCode = 400; return; }
        using var terminal = PseudoConsole.Start(shell.Executable, shell.Arguments);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        // ConPTY pipes are synchronous; run both directions independently.
        var output = Task.Run(async () => {
            var buffer = new byte[8192];
            var canSend = true;
            try {
                while (true) {
                    var count = terminal.Output.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    if (canSend && socket.State == WebSocketState.Open) {
                        try { await socket.SendAsync(buffer.AsMemory(0, count), WebSocketMessageType.Binary, true, context.RequestAborted); }
                        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { canSend = false; }
                    }
                }
                if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "终端已退出", CancellationToken.None);
            } catch (Exception e) when (e is IOException or ObjectDisposedException or WebSocketException or OperationCanceledException) { }
        });
        try {
            var buffer = new byte[32768];
            while (socket.State == WebSocketState.Open) {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 1024 * 1024) return;
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(message.ToArray());
                var root = json.RootElement;
                if (!root.TryGetProperty("type", out var type)) continue;
                if (type.GetString() == "input" && root.TryGetProperty("data", out var data)) {
                    var bytes = Encoding.UTF8.GetBytes(data.GetString() ?? "");
                    await terminal.Input.WriteAsync(bytes, context.RequestAborted);
                    await terminal.Input.FlushAsync(context.RequestAborted);
                } else if (type.GetString() == "resize" && root.TryGetProperty("cols", out var cols) && root.TryGetProperty("rows", out var rows) && cols.TryGetInt32(out var c) && rows.TryGetInt32(out var r)) {
                    terminal.Resize(c, r);
                }
            }
        } catch (Exception e) when (e is WebSocketException or IOException or JsonException or OperationCanceledException or ObjectDisposedException) { }
        finally {
            terminal.Stop();
            terminal.CloseConsole();
            await Task.WhenAny(output, Task.Delay(3000));
        }
    }
}

sealed class PseudoConsole : IDisposable
{
    const uint ExtendedStartupInfoPresent = 0x00080000;
    const int ProcThreadAttributePseudoConsole = 0x00020016;
    IntPtr console;
    IntPtr process;
    IntPtr job;
    public FileStream Input { get; private set; } = null!;
    public FileStream Output { get; private set; } = null!;

    public static PseudoConsole Start(string executable, string arguments)
    {
        var result = new PseudoConsole();
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        IntPtr attributes = IntPtr.Zero;
        bool attributesInitialized = false;
        try {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            var hr = CreatePseudoConsole(new Coord(80, 24), inputRead, outputWrite, 0, out result.console);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            attributesInitialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)ProcThreadAttributePseudoConsole, result.console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), flags = 0x100, stdInput = new IntPtr(-1), stdOutput = new IntPtr(-1), stdError = new IntPtr(-1) }, AttributeList = attributes };
            var command = new StringBuilder('"' + executable + '"' + (arguments.Length > 0 ? " " + arguments : ""));
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent, IntPtr.Zero, null, ref startup, out var info)) throw new Win32Exception();
            inputRead.Dispose(); inputRead = null;
            outputWrite.Dispose(); outputWrite = null;
            result.process = info.Process;
            CloseHandle(info.Thread);
            result.job = CreateJobObject(IntPtr.Zero, null);
            if (result.job == IntPtr.Zero) throw new Win32Exception();
            var limits = new JobLimits { BasicLimitInformation = new BasicJobLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(result.job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception();
            if (!AssignProcessToJobObject(result.job, result.process)) throw new Win32Exception();
            result.Input = new FileStream(inputWrite, FileAccess.Write, 4096, false); inputWrite = null;
            result.Output = new FileStream(outputRead, FileAccess.Read, 4096, false); outputRead = null;
            return result;
        } catch { result.Dispose(); throw; }
        finally {
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
        }
    }
    public void Resize(int cols, int rows) {
        if (console != IntPtr.Zero) ResizePseudoConsole(console, new Coord((short)Math.Clamp(cols, 2, 500), (short)Math.Clamp(rows, 2, 300)));
    }
    public void Stop() {
        // Closing the job terminates the entire shell process tree on disconnect.
        if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
        if (process != IntPtr.Zero) { TerminateProcess(process, 0); CloseHandle(process); process = IntPtr.Zero; }
        Input?.Dispose();
    }
    public void Dispose() {
        Stop();
        CloseConsole();
        Output?.Dispose();
    }
    public void CloseConsole() {
        var current = Interlocked.Exchange(ref console, IntPtr.Zero);
        if (current != IntPtr.Zero) ClosePseudoConsole(current);
    }

    [StructLayout(LayoutKind.Sequential)] readonly struct Coord(short x, short y) { public readonly short X = x; public readonly short Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo {
        public int cb; public string? reserved, desktop, title; public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags; public short showWindow, reserved2; public IntPtr reserved2Ptr, stdInput, stdOutput, stdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct BasicJobLimits {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] struct JobLimits {
        public BasicJobLimits BasicLimitInformation; public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateProcessW")] static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateJobObjectW")] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

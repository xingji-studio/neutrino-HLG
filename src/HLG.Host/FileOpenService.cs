using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

// Shell association handlers have to be used on an STA and cannot be kept
// across requests. Re-enumerate the chosen handler when opening a file.
sealed class FileOpenService
{
    public record Choice(string Id, string Name);
    public record OpenOptions(Choice? DefaultApp, IReadOnlyList<Choice> Recommended);
    public record OpenRequest(string? Path, string? Kind, string? HandlerId, string? Executable);

    public async Task<IResult> Options(string? path) {
        try {
            var file = ExistingFile(path, "文件");
            return Results.Ok(await OnSta(() => ShellAssociations.Options(file)));
        } catch (Exception e) when (IsExpected(e)) { return Error(e, "无法读取打开方式"); }
    }

    public async Task<IResult> Open(OpenRequest request) {
        try {
            var file = ExistingFile(request.Path, "文件");
            switch (request.Kind) {
                case "default":
                    await OnSta(() => {
                        if (ShellAssociations.Default(Path.GetExtension(file)) is null)
                            throw new InvalidOperationException("此文件类型尚未设置默认应用。");
                        using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "open" });
                        return true;
                    });
                    break;
                case "recommended":
                    if (string.IsNullOrWhiteSpace(request.HandlerId)) throw new ArgumentException("请选择打开应用。");
                    await OnSta(() => { ShellAssociations.Invoke(file, request.HandlerId); return true; });
                    break;
                case "executable": {
                    var executable = ExistingFile(request.Executable, "应用程序");
                    if (!string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("请选择 .exe 应用程序。");
                    using var process = StartWithExecutable(executable, file);
                    break;
                }
                default: throw new ArgumentException("无效的打开方式。");
            }
            return Results.Ok(new { ok = true });
        } catch (Exception e) when (IsExpected(e)) { return Error(e, "无法打开文件"); }
    }

    internal static string ExistingFile(string? path, string description) {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException(description + "路径无效。");
        var full = Path.GetFullPath(path);
        if ((File.GetAttributes(full) & FileAttributes.Directory) != 0) throw new ArgumentException("请选择" + description + "，不能选择文件夹。");
        return full;
    }

    internal static Process StartWithExecutable(string executable, string file) {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        info.ArgumentList.Add(file); // Pass the complete filename as one argument, including spaces and metacharacters.
        return Process.Start(info) ?? throw new InvalidOperationException("应用程序未能启动。");
    }

    static bool IsExpected(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception or COMException or InvalidOperationException or System.Security.SecurityException;
    static IResult Error(Exception e, string prefix) => Results.Json(new { error = prefix + "：" + e.Message }, statusCode:
        e is FileNotFoundException or DirectoryNotFoundException ? 404 : e is UnauthorizedAccessException or System.Security.SecurityException ? 403 : 400);

    static Task<T> OnSta<T>(Func<T> action) {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { result.SetResult(action()); } catch (Exception e) { result.SetException(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    static class ShellAssociations
    {
        record NativeChoice(Choice Choice, string NativeName);

        internal static OpenOptions Options(string file) {
            var extension = Path.GetExtension(file);
            var defaultApp = Default(extension);
            var recommended = new List<Choice>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var defaultExe = defaultApp is null ? null : QueryAssociation(extension, 2);
            Enumerate(extension, (handler, choice) => {
                if ((defaultExe is not null && string.Equals(defaultExe, choice.NativeName, StringComparison.OrdinalIgnoreCase)) ||
                    (defaultExe is null && defaultApp is not null && string.Equals(defaultApp.Name, choice.Choice.Name, StringComparison.CurrentCultureIgnoreCase))) return false;
                if (ids.Add(choice.Choice.Id)) recommended.Add(choice.Choice);
                return false;
            });
            return new OpenOptions(defaultApp, recommended);
        }

        internal static Choice? Default(string extension) {
            if (string.IsNullOrEmpty(extension)) return null;
            var registration = (IApplicationAssociationRegistration)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("591209c7-767b-42b2-9fba-44ee4615f2c7"), true)!)!;
            try {
                if (registration.QueryCurrentDefault(extension, 0, 1, out var progId) < 0 || string.IsNullOrWhiteSpace(progId)) return null;
                var name = QueryAssociation(extension, 4) ?? QueryAssociation(progId, 4);
                if (string.IsNullOrWhiteSpace(name)) {
                    var executable = QueryAssociation(extension, 2) ?? QueryAssociation(progId, 2);
                    if (!string.IsNullOrWhiteSpace(executable)) {
                        try { name = FileVersionInfo.GetVersionInfo(executable).FileDescription; } catch (Exception e) when (e is IOException or ArgumentException or Win32Exception) { }
                        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(executable);
                    }
                }
                return new Choice("default", string.IsNullOrWhiteSpace(name) ? "系统默认应用" : name);
            } finally { Marshal.ReleaseComObject(registration); }
        }

        internal static void Invoke(string file, string handlerId) {
            var invoked = false;
            Enumerate(Path.GetExtension(file), (handler, choice) => {
                if (!string.Equals(choice.Choice.Id, handlerId, StringComparison.Ordinal)) return false;
                var shellItemId = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(file, IntPtr.Zero, ref shellItemId, out var item));
                try {
                    var dataHandler = new Guid("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
                    var dataId = typeof(ComDataObject).GUID;
                    Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref dataHandler, ref dataId, out var data));
                    try { Marshal.ThrowExceptionForHR(handler.Invoke(data)); invoked = true; }
                    finally { Marshal.ReleaseComObject(data); }
                } finally { Marshal.ReleaseComObject(item); }
                return true;
            });
            if (!invoked) throw new InvalidOperationException("打开方式已更改，请重新选择应用。");
        }

        static void Enumerate(string extension, Func<IAssocHandler, NativeChoice, bool> visit) {
            if (string.IsNullOrEmpty(extension)) return;
            var hr = SHAssocEnumHandlers(extension, 1, out var list);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (list is null) return;
            try {
                while (list.Next(1, out var handler, out var fetched) == 0 && fetched == 1 && handler is not null) {
                    try {
                        if (handler.GetName(out var nativeName) < 0 || string.IsNullOrWhiteSpace(nativeName)) continue;
                        if (handler.GetUIName(out var name) < 0 || string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(nativeName);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nativeName.ToUpperInvariant() + "\n" + name.ToUpperInvariant())));
                        if (visit(handler, new NativeChoice(new Choice(id, name), nativeName))) return;
                    } finally { Marshal.ReleaseComObject(handler); }
                }
            } finally { Marshal.ReleaseComObject(list); }
        }

        static string? QueryAssociation(string association, uint field) {
            uint length = 0;
            _ = AssocQueryString(0, field, association, "open", null, ref length);
            if (length == 0 || length > 32768) return null;
            var buffer = new StringBuilder((int)length);
            return AssocQueryString(0, field, association, "open", buffer, ref length) == 0 ? buffer.ToString() : null;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "AssocQueryStringW")]
        static extern int AssocQueryString(uint flags, uint field, string association, string extra, StringBuilder? output, ref uint length);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHAssocEnumHandlers(string extension, uint filter, out IEnumAssocHandlers? handlers);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid id, out IShellItem item);

        [ComImport, Guid("4e530b0a-e611-4c77-a3ac-9031d022281b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationAssociationRegistration {
            [PreserveSig] int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string query, int type, int level, [MarshalAs(UnmanagedType.LPWStr)] out string? association);
        }
        [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IEnumAssocHandlers {
            [PreserveSig] int Next(uint count, out IAssocHandler? handler, out uint fetched);
        }
        [ComImport, Guid("F04061AC-1659-4a3f-A954-775AA57FC083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAssocHandler {
            [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string? name);
            [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string? name);
            [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string? path, out int index);
            [PreserveSig] int IsRecommended();
            [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
            [PreserveSig] int Invoke([MarshalAs(UnmanagedType.Interface)] ComDataObject data);
        }
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem {
            [PreserveSig] int BindToHandler(IntPtr context, ref Guid handlerId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out ComDataObject result);
        }
    }
}

using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;

static class AppIcons
{
    // Only expose icons for the same Start Menu shortcut roots used by the app list.
    public static IResult Get(string? path, IEnumerable<string>? roots = null)
    {
        roots ??= new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) };
        try {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return Results.NotFound();
            var full = Path.GetFullPath(path);
            if (!full.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
            var root = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Path.GetFullPath)
                .FirstOrDefault(r => full.StartsWith(Path.TrimEndingDirectorySeparator(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (root is null) return Results.NotFound();
            // A junction or symbolic link must not bypass the allowed roots.
            string? entry = full;
            while (entry is not null && !string.Equals(Path.TrimEndingDirectorySeparator(entry), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return Results.NotFound();
                entry = Path.GetDirectoryName(entry);
            }
            if (entry is null) return Results.NotFound();
            var png = ReadPng(full, false);
            return png is null ? Results.NotFound() : Results.File(png, "image/png");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ExternalException or InvalidOperationException) {
            return Results.NotFound();
        }
    }

    // The caller must resolve identity from the enumerated AppsFolder first.
    public static IResult GetShell(string identity) {
        try { var png = ReadPng(identity, true); return png is null ? Results.NotFound() : Results.File(png, "image/png"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ExternalException or InvalidOperationException) { return Results.NotFound(); }
    }

    static byte[]? ReadPng(string path, bool isShellItem)
    {
        // Shell resolves custom shortcut icons and icons embedded in the target.
        var initialized = CoInitializeEx(IntPtr.Zero, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) return null;
        IntPtr handle = IntPtr.Zero;
        IntPtr pidl = IntPtr.Zero;
        try {
            var info = new ShellFileInfo();
            IntPtr result;
            if (isShellItem) {
                if (SHParseDisplayName("shell:AppsFolder\\" + path, IntPtr.Zero, out pidl, 0, out _) < 0 || pidl == IntPtr.Zero) return null;
                result = SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x108);
            } else result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            handle = info.Icon;
            using var icon = System.Drawing.Icon.FromHandle(handle);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        } finally {
            if (handle != IntPtr.Zero) DestroyIcon(handle);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            if (initialized >= 0) CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellFileInfo {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")] static extern IntPtr SHGetFileInfoPidl(IntPtr path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
}

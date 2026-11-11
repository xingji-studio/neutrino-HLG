using System.Runtime.InteropServices;

static class ShellApps
{
    public record App(string Name, string Identity);
    public static IReadOnlyList<App> List() {
        var result = new List<App>();
        object? shell = null, folder = null, items = null;
        try {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return result;
            shell = Activator.CreateInstance(type);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) return result;
            items = ((dynamic)folder).Items();
            var count = (int)((dynamic)items).Count;
            for (var i = 0; i < count; i++) {
                object? item = null;
                try {
                    item = ((dynamic)items).Item(i);
                    string? name = ((dynamic)item!).Name, identity = ((dynamic)item).Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(identity)) result.Add(new(name, identity));
                } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { Release(item); }
            }
        } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException) { }
        finally { Release(items); Release(folder); Release(shell); }
        return result;
    }
    static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}

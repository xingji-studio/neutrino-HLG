using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Win32;

// This same apphost is the harmless target used to check the exact argument
// passed through both the executable picker and a Shell association handler.
if (args.Length == 1 && Path.GetExtension(args[0]).StartsWith(".hlg", StringComparison.OrdinalIgnoreCase)) {
    await File.WriteAllTextAsync(args[0] + ".opening", args[0]);
    File.Move(args[0] + ".opening", args[0] + ".opened");
    return;
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static int? Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;
static T? Value<T>(IResult result) where T : class => (result as IValueHttpResult)?.Value as T;
static async Task WaitFor(string path) {
    for (var i = 0; i < 100 && !File.Exists(path); i++) await Task.Delay(50);
    Check(File.Exists(path), "The selected application did not receive the file.");
}

var root = Path.Combine(AppContext.BaseDirectory, "open-with-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var extension = ".hlg" + Guid.NewGuid().ToString("N")[..8];
var defaultProgId = "HLG.OpenWith.DefaultTest." + Guid.NewGuid().ToString("N");
try {
    var target = Path.Combine(root, "报告 & file with spaces" + extension);
    await File.WriteAllTextAsync(target, "test");
    var executable = Path.Combine(AppContext.BaseDirectory, "HLG.OpenWith.exe");
    Check(File.Exists(executable), "The test apphost was not built.");
    var service = new FileOpenService();
    var missing = await service.Options(Path.Combine(root, "missing.txt"));
    Check(Status(missing) == 404, "A missing file must fail with 404.");
    Check(Status(await service.Options("relative.txt")) == 400, "Relative paths must be rejected.");
    Check(Status(await service.Open(new(target, "executable", null, root))) == 400, "A directory is not an executable.");
    Check(Status(await service.Open(new(target, "executable", null, target))) == 400, "A non-executable must be rejected.");

    var txt = Path.Combine(root, "sample.txt"); await File.WriteAllTextAsync(txt, "test");
    var options = await service.Options(txt);
    Check(Status(options) == 200 && Value<FileOpenService.OpenOptions>(options) is not null, "Windows .txt associations could not be read.");
    var txtOptions = Value<FileOpenService.OpenOptions>(options)!;
    Console.WriteLine($"Windows .txt default: {txtOptions.DefaultApp?.Name ?? "(none)"}; recommended: {string.Join(", ", txtOptions.Recommended.Select(c => c.Name))}");

    var selected = await service.Open(new(target, "executable", null, executable));
    Check(Status(selected) == 200, "The selected executable failed to start.");
    await WaitFor(target + ".opened");
    Check(await File.ReadAllTextAsync(target + ".opened") == target, "Executable received the wrong argument.");
    File.Delete(target + ".opened");

    // A unique temporary extension exercises real Shell enumeration and invocation
    // without changing any existing extension or default application.
    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + extension)) key.SetValue("", defaultProgId);
    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + defaultProgId + @"\shell\open\command")) key.SetValue("", "\"" + executable + "\" \"%1\"");
    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + defaultProgId + @"\Application")) key.SetValue("ApplicationName", "HLG Default Open With Test");
    Native.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    options = await service.Options(target);
    var testOptions = Value<FileOpenService.OpenOptions>(options);
    Check(Status(options) == 200 && testOptions is not null, "Test association enumeration failed.");
    Console.WriteLine($"Test default: {testOptions!.DefaultApp?.Name ?? "(none)"}; recommended: {string.Join(", ", testOptions.Recommended.Select(c => c.Name))}");
    Check(Status(await service.Open(new(target, "recommended", "missing-handler", null))) == 400, "A stale handler must fail.");
    var defaultApp = testOptions.DefaultApp ?? throw new Exception("The temporary default was not found.");
    Check(defaultApp.Name == "HLG Default Open With Test", "The temporary default had the wrong name.");
    Check(testOptions.Recommended.All(choice => choice.Name != defaultApp.Name), "The default application was repeated in recommendations.");
    Check(Status(await service.Open(new(target, "default", null, null))) == 200, "The default application could not be launched.");
    await WaitFor(target + ".opened");
    var defaultArgument = await File.ReadAllTextAsync(target + ".opened");
    Check(string.Equals(defaultArgument, target, StringComparison.OrdinalIgnoreCase), "Default application received the wrong argument: " + defaultArgument);
    File.Delete(target + ".opened");
    // The default is also returned by SHAssocEnumHandlers, but is omitted from
    // the UI's recommendations. Invoke that real handler through the same path.
    var handlerId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant() + "\n" + defaultApp.Name.ToUpperInvariant())));
    var invoked = await service.Open(new(target, "recommended", handlerId, null));
    Check(Status(invoked) == 200, "The recommended Shell handler could not be invoked.");
    await WaitFor(target + ".opened");
    var handlerArgument = await File.ReadAllTextAsync(target + ".opened");
    Check(string.Equals(handlerArgument, target, StringComparison.OrdinalIgnoreCase), "Shell handler received the wrong argument: " + handlerArgument);
    Console.WriteLine("File association lookup, executable launch and real Shell handler invocation passed.");
} finally {
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + extension, false);
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + defaultProgId, false);
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension, false);
    Native.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    Directory.Delete(root, true);
}

static class Native {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}

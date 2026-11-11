using Microsoft.AspNetCore.Http;

if (((IStatusCodeHttpResult)WindowCatalog.Icon(new WindowCatalog.WindowRequest("invalid", 0))).StatusCode != 404)
    throw new Exception("Invalid window icon request was not rejected.");
if (((IStatusCodeHttpResult)WindowCatalog.Activate(new WindowCatalog.WindowRequest("invalid", 0))).StatusCode != 404)
    throw new Exception("Invalid activation request was not rejected.");
var windows = WindowCatalog.List();
var found = false;
foreach (var window in windows.Take(20)) {
    if (WindowCatalog.Icon(new WindowCatalog.WindowRequest(window.Id, window.ProcessId)) is Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult icon && icon.FileContents.Span.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 })) { found = true; break; }
}
if (windows.Count > 0 && !found) throw new Exception("No PNG icon could be read from available windows.");
Console.WriteLine($"Window validation and icon retrieval passed (windows: {windows.Count}, icon found: {found}).");
var cursor = ScreenSession.MapCursor(new System.Drawing.Rectangle(-100, -50, 201, 101), new System.Drawing.Point(-200, 100));
if (cursor.X != -.5 || cursor.Y != 1.5 || !System.Text.Json.JsonSerializer.Serialize(cursor).Contains("\"x\"")) throw new Exception("Out-of-frame cursor coordinates were not preserved.");

var root = Path.Combine(Path.GetTempPath(), "hlg-icons-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
    string CreateShortcut(string name, int iconIndex) {
        var path = Path.Combine(root, name + ".lnk");
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = Environment.ProcessPath!;
        shortcut.IconLocation = Path.Combine(Environment.SystemDirectory, "shell32.dll") + "," + iconIndex;
        shortcut.Save();
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
        return path;
    }
    var first = CreateShortcut("first", 3);
    var second = CreateShortcut("second", 44);
    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
    var firstResult = AppIcons.Get(first, new[] { root }) as Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult;
    var secondResult = AppIcons.Get(second, new[] { root }) as Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult;
    if (firstResult is null || secondResult is null || !firstResult.FileContents.Span.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 })) throw new Exception("Shortcut icon extraction failed.");
    if (firstResult.FileContents.Span.SequenceEqual(secondResult.FileContents.Span)) throw new Exception("Custom shortcut icons were not distinguished.");
    if (((IStatusCodeHttpResult)AppIcons.Get(first, new[] { Path.Combine(root, "other") })).StatusCode != 404) throw new Exception("Shortcut outside allowed root was accepted.");
    if (((IStatusCodeHttpResult)AppIcons.Get(Path.Combine(root, "../outside.lnk"), new[] { root })).StatusCode != 404) throw new Exception("Shortcut path traversal was accepted.");
    Console.WriteLine("Custom shortcut icons and root validation passed.");
    var systemApps = ShellApps.List();
    if (systemApps.Count == 0) throw new Exception("AppsFolder enumeration returned no applications.");
    var packaged = systemApps.Where(a => a.Identity.Contains('!')).ToArray();
    if (packaged.Length > 0 && !packaged.Take(20).Any(a => AppIcons.GetShell(a.Identity) is Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult))
        throw new Exception("Unable to retrieve any packaged application icons.");
    var usagePath = Path.Combine(root, "usage.json");
    var usage = new AppUsage(usagePath);
    var service = new AppService(new[] { root }, usage);
    Directory.CreateDirectory(Path.Combine(root, "nested"));
    File.Copy(first, Path.Combine(root, "nested", "first.lnk"));
    if (service.List().Count(a => a.Name == "first" && a.Kind == "shortcut") != 2) throw new Exception("Distinct shortcuts with the same name were dropped.");
    var firstApp = service.List().Single(a => a.Path == first);
    var secondApp = service.List().Single(a => a.Path == second);
    Parallel.For(0, 6, _ => usage.Record(secondApp.Id));
    usage.Record(firstApp.Id);
    var ordered = service.List();
    if (ordered[0].Id != secondApp.Id) throw new Exception("Apps were not ordered by HLG launch frequency.");
    var reloaded = new AppService(new[] { root }, new AppUsage(usagePath)).List();
    if (reloaded[0].Id != secondApp.Id || new AppUsage(usagePath).Snapshot()[secondApp.Id].Count != 6) throw new Exception("App frequency did not persist correctly.");
    if (((IStatusCodeHttpResult)service.Icon("invalid", first)).StatusCode != 404) throw new Exception("Unlisted app ID was accepted.");
    Console.WriteLine($"AppsFolder enumeration, packaged icons and persistent frequency ordering passed ({systemApps.Count} shell entries).");
} finally { Directory.Delete(root, true); }

var ready = new TaskCompletionSource<System.Windows.Forms.Form>(TaskCreationOptions.RunContinuationsAsynchronously);
var ui = new Thread(() => {
    using var form = new CaptureFixture { BackColor = System.Drawing.Color.Red, Size = new System.Drawing.Size(240, 180), StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(-3000, -3000), ShowInTaskbar = false };
    form.Shown += (_, _) => ready.TrySetResult(form);
    System.Windows.Forms.Application.Run(form);
});
ui.SetApartmentState(ApartmentState.STA); ui.IsBackground = true; ui.Start();
var fixture = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
try {
    void CheckFrame() {
        for (var attempt = 0; attempt < 20; attempt++) {
            var bounds = ScreenSession.Bounds(fixture.Handle);
            var bytes = ScreenSession.CaptureJpeg(fixture.Handle, bounds);
            using var stream = new MemoryStream(bytes);
            using var image = new System.Drawing.Bitmap(stream);
            if (image.Width != bounds.Width || image.Height != bounds.Height) throw new Exception("Captured frame does not match selected window bounds.");
            var center = image.GetPixel(image.Width / 2, image.Height / 2);
            if (center.R >= 150 && center.G <= 80 && center.B <= 80) return;
            Thread.Sleep(100);
        }
        throw new Exception("Frame did not capture the selected test window.");
    }
    CheckFrame();
    fixture.Invoke(() => fixture.Size = new System.Drawing.Size(340, 250));
    CheckFrame();
    Console.WriteLine("Selected-window capture and resize bounds passed.");
} finally { fixture.BeginInvoke(fixture.Close); ui.Join(5000); }

sealed class CaptureFixture : System.Windows.Forms.Form { protected override bool ShowWithoutActivation => true; }

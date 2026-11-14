using System.Drawing;
using System.Net;
using System.Windows.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

static class Program
{
    [STAThread]
    static void Main(string[] args) {
        var root = Path.Combine(AppContext.BaseDirectory, "desktop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            using var log = new FileLog(root);
            log.Write(LogLevel.Information, "test", "token=private-value Authorization: Bearer bearer-secret {\"api_key\":\"json-secret\"} 请求 https://example.test/a?secret=value");
            var contents = File.ReadAllText(log.Path);
            if (contents.Contains("private-value") || contents.Contains("bearer-secret") || contents.Contains("json-secret") || contents.Contains("secret=value")) throw new Exception("Log secrets were not redacted.");
            if (!Path.GetFileName(log.Path).EndsWith(".log")) throw new Exception("Log filename invalid.");
            var pairing = new PairingAuth(new TrustedDevices(Path.Combine(root, "trusted.json")));
            var code = pairing.PairingCode;
            pairing.RefreshCode();
            if (pairing.PairingCode.Length != 6 || pairing.PairingExpires <= DateTimeOffset.UtcNow) throw new Exception("Pairing refresh failed.");
            var devices = new DeviceConnections();
            var mobile = new DefaultHttpContext(); mobile.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.10"); mobile.Request.Headers.UserAgent = "Android Chrome";
            var session = devices.Create("test-session", mobile, new string('A', 64))!;
            using var presence = devices.Track(session, mobile, "presence");
            using var screen = devices.Track(session, mobile, "screen");
            using var form = new PreviewForm(pairing, devices, log) { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
            form.Show(); Application.DoEvents(); Thread.Sleep(250); Application.DoEvents();
            static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
            var controls = Descendants(form).ToArray();
            if (!controls.Any(c => c.Text.Contains("Android 设备")) || !controls.Any(c => c.Text == "断开连接") || !controls.Any(c => c.Text == "取消信任")) throw new Exception("Connected device controls missing.");
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            if (args.Length == 1) image.Save(args[0]);
            if (image.Width < 850 || image.Height < 620) throw new Exception("Dashboard dimensions invalid.");
            form.Close();
            Console.WriteLine("Desktop layout, code refresh and log redaction passed.");
        } finally { Directory.Delete(root, true); }
    }
    sealed class PreviewForm(PairingAuth pairing, DeviceConnections devices, FileLog log) : DashboardForm(pairing, devices, log) { protected override bool ShowWithoutActivation => true; }
}

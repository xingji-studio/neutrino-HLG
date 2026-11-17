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
            if (!controls.Any(c => c.Text.Contains("Android 设备")) || !controls.Any(c => c.Text == "断开连接") || !controls.Any(c => c.Text == "取消信任") || !controls.Any(c => c.Text == "设置") || !controls.Any(c => c.Text == "手机首次连接")) throw new Exception("Connected device, settings or certificate controls missing.");
            Exception? setupError = null; bool inspected = false;
            using var setupTimer = new System.Windows.Forms.Timer { Interval = 100 };
            setupTimer.Tick += (_, _) => {
                var setup = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "手机首次连接");
                if (setup is null) return;
                try {
                    var fields = Descendants(setup).OfType<TextBox>().Select(c => c.Text).ToArray();
                    if (!fields.Any(text => text.StartsWith("http://", StringComparison.Ordinal) && text.Contains(":8768/")) || !fields.Contains("TEST-FINGERPRINT")) throw new Exception("Certificate setup address or fingerprint missing.");
                    if (args.Length >= 3) { using var preview = new Bitmap(setup.Width, setup.Height); setup.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size)); preview.Save(args[2]); }
                    inspected = true;
                } catch (Exception e) { setupError = e; }
                finally { setupTimer.Stop(); setup.Close(); }
            };
            setupTimer.Start(); ((Button)controls.Single(c => c.Text == "手机首次连接")).PerformClick();
            if (setupError is not null) throw setupError;
            if (!inspected) throw new Exception("Certificate setup dialog was not opened.");
            using var image = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            if (args.Length >= 1) image.Save(args[0]);
            if (image.Width < 850 || image.Height < 620) throw new Exception("Dashboard dimensions invalid.");
            form.Close();
            var autoStart = new FakeAutoStart();
            using var settingsForm = new PreviewUnlockForm(new UnlockSettings(Path.Combine(root, "unlock.json")), log, autoStart) { StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false };
            settingsForm.Show(); Application.DoEvents();
            var settingsControls = Descendants(settingsForm).ToArray();
            if (!settingsControls.Any(c => c.Text == "启用远程解锁") || !settingsControls.Any(c => c.Text == "自动输入密码解锁") || !settingsControls.OfType<TextBox>().Any(c => c.UseSystemPasswordChar)) throw new Exception("Unlock settings controls missing.");
            var startupToggle = settingsControls.OfType<CheckBox>().Single(c => c.Text == "开机自启");
            if (startupToggle.Checked) throw new Exception("Autostart should initially be off.");
            startupToggle.Checked = true;
            for (var i = 0; i < 100 && (!startupToggle.Enabled || !autoStart.Enabled); i++) { Application.DoEvents(); Thread.Sleep(10); }
            if (!startupToggle.Enabled || !autoStart.Enabled) throw new Exception("The autostart toggle did not enable the task.");
            startupToggle.Checked = false;
            for (var i = 0; i < 100 && (!startupToggle.Enabled || autoStart.Enabled); i++) { Application.DoEvents(); Thread.Sleep(10); }
            if (!startupToggle.Enabled || autoStart.Enabled) throw new Exception("The autostart toggle did not disable the task.");
            var saveButton = settingsControls.OfType<Button>().Single(c => c.Text == "保存"); var cancelButton = settingsControls.OfType<Button>().Single(c => c.Text == "取消");
            if (!saveButton.Enabled || !cancelButton.Enabled) throw new Exception($"The autostart toggle left the other settings controls disabled: save={saveButton.Enabled}, cancel={cancelButton.Enabled}.");
            if (args.Length >= 2) { using var settingsImage = new Bitmap(settingsForm.Width, settingsForm.Height); settingsForm.DrawToBitmap(settingsImage, new Rectangle(Point.Empty, settingsImage.Size)); settingsImage.Save(args[1]); }
            settingsForm.Close();
            Console.WriteLine("Desktop layout, code refresh and log redaction passed.");
        } finally { Directory.Delete(root, true); }
    }
    sealed class PreviewForm(PairingAuth pairing, DeviceConnections devices, FileLog log) : DashboardForm(pairing, devices, log, certificateFingerprint: "TEST-FINGERPRINT", rootCertificate: new byte[] { 1 }, appleProfile: new byte[] { 2 }) { protected override bool ShowWithoutActivation => true; }
    sealed class PreviewUnlockForm(UnlockSettings settings, FileLog log, IAutoStartControl autoStart) : UnlockSettingsForm(settings, log, true, autoStart) { protected override bool ShowWithoutActivation => true; }
    sealed class FakeAutoStart : IAutoStartControl { public volatile bool Enabled; public bool IsEnabled() => Enabled; public void SetEnabled(bool enabled) => Enabled = enabled; }
}

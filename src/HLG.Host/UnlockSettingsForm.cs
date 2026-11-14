using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

class UnlockSettingsForm : Form
{
    static readonly Color Background = ColorTranslator.FromHtml("#111416"), Surface = ColorTranslator.FromHtml("#202527"), TextColor = ColorTranslator.FromHtml("#F1F5F5"), Muted = ColorTranslator.FromHtml("#9CA8A9"), Theme = ColorTranslator.FromHtml("#45ACD7");
    readonly UnlockSettings settings;
    readonly FileLog log;
    readonly bool https;
    readonly CheckBox enabled = new() { Text = "启用远程解锁", AutoSize = true };
    readonly CheckBox automatic = new() { Text = "自动输入密码解锁", AutoSize = true };
    readonly CheckBox unattended = new() { Text = "没有远程连接时也自动解锁", AutoSize = true };
    readonly TextBox password = new() { UseSystemPasswordChar = true, MaxLength = 512, Dock = DockStyle.Top };
    readonly Label saved = new() { AutoSize = true }, status = new() { AutoSize = false, Dock = DockStyle.Fill };
    readonly Button save = Button("保存", true), remove = Button("删除已保存密码"), cancel = Button("取消");
    bool deletePassword;
    bool saving;

    public UnlockSettingsForm(UnlockSettings settings, FileLog log, bool https) {
        this.settings = settings; this.log = log; this.https = https;
        Text = "设置 · 远程解锁（测试中）"; Icon = AppBrand.Icon; StartPosition = FormStartPosition.CenterParent;
        BackColor = Background; ForeColor = TextColor; Font = new Font("Microsoft YaHei UI", 10); ClientSize = new Size(630, 605); MinimumSize = new Size(600, 600);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 13, BackColor = Background, AutoScroll = true };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 38, 34, 37, 42, 34, 40, 36, 44, 38, 45, 46, 58, 48 }) content.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(content);
        var title = Label("远程解锁 · 测试中", 16, true); title.ForeColor = Theme; content.Controls.Add(title, 0, 0);
        content.Controls.Add(Label("Windows 用户：" + Environment.UserDomainName + @"\" + Environment.UserName, 9), 0, 1);
        content.Controls.Add(enabled, 0, 2);
        content.Controls.Add(Label("启用时会安装本机 Windows 系统服务，用于访问锁屏画面。", 9, muted: true), 0, 3);
        content.Controls.Add(automatic, 0, 4);
        content.Controls.Add(unattended, 0, 5);
        content.Controls.Add(Label("Windows 账户密码（不是 Windows Hello PIN）", 9), 0, 6);
        password.BackColor = Surface; password.ForeColor = TextColor; password.BorderStyle = BorderStyle.FixedSingle;
        password.PlaceholderText = "留空则保留已保存的密码"; content.Controls.Add(password, 0, 7);
        var passwordActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Background };
        saved.Padding = new Padding(0, 8, 12, 0); passwordActions.Controls.Add(saved); passwordActions.Controls.Add(remove); content.Controls.Add(passwordActions, 0, 8);
        content.Controls.Add(Label("密码由 Windows 按当前用户加密保存，修改 Windows 密码后可能需要重新设置。", 9, muted: true), 0, 9);
        content.Controls.Add(Label("无人连接也自动解锁会让电脑在锁屏后立即重新解锁。", 9, muted: true), 0, 10);
        status.ForeColor = https ? Muted : ColorTranslator.FromHtml("#EFA397");
        var serviceStatus = SecureService.ServiceProcessId() != 0 ? "安全桌面服务：运行中。" : "安全桌面服务：未运行。";
        status.Text = https ? serviceStatus + " 请确保手机信任 HTTPS 证书；登录画面兼容性取决于 Windows 登录方式。" : serviceStatus + " 尚未配置 HTTPS。请设置 HLG_HTTPS_CERT 和 HLG_HTTPS_PASSWORD 后重新启动。";
        content.Controls.Add(status, 0, 11);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Background };
        cancel.Click += (_, _) => Close(); actions.Controls.Add(save); actions.Controls.Add(cancel); content.Controls.Add(actions, 0, 12);
        try {
            var current = settings.Read();
            enabled.Checked = current.Enabled; automatic.Checked = current.Automatic; unattended.Checked = current.UnlockWithoutConnection;
            saved.Text = current.ProtectedPassword is null ? "尚未设置密码" : "密码已设置";
            remove.Enabled = current.ProtectedPassword is not null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { status.Text = "无法读取远程解锁设置，请检查日志。"; log.Write(LogLevel.Error, "远程解锁", "读取设置失败", e); save.Enabled = false; }
        enabled.CheckedChanged += (_, _) => UpdateControls(); automatic.CheckedChanged += (_, _) => UpdateControls();
        remove.Click += (_, _) => { deletePassword = true; password.Clear(); saved.Text = "保存后删除密码"; remove.Enabled = false; };
        password.TextChanged += (_, _) => { if (password.TextLength > 0) { deletePassword = false; saved.Text = "保存后更新密码"; } };
        save.Click += async (_, _) => await Save();
        FormClosing += (_, e) => { if (saving && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        UpdateControls();
    }

    void UpdateControls() { automatic.Enabled = enabled.Checked; unattended.Enabled = enabled.Checked && automatic.Checked; }
    async Task Save() {
        var turnOn = enabled.Checked; var auto = turnOn && automatic.Checked; var always = auto && unattended.Checked;
        if (turnOn && !https) { MessageBox.Show("启用远程解锁前，请先配置受手机信任的 HTTPS 证书并重启程序。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try {
            var old = settings.Read();
            if (auto && password.TextLength == 0 && (deletePassword || string.IsNullOrEmpty(old.ProtectedPassword))) { MessageBox.Show("自动解锁需要先设置当前 Windows 账户密码。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            saving = true; save.Enabled = false; cancel.Enabled = false; enabled.Enabled = false; automatic.Enabled = false; unattended.Enabled = false; password.Enabled = false; remove.Enabled = false;
            if (turnOn && SecureService.ServiceProcessId() == 0) await Task.Run(SecureService.InstallAndStart);
            try { settings.Save(turnOn, auto, always, password.Text, deletePassword); }
            catch { if (turnOn && !old.Enabled) try { SecureService.StopIfRunning(); } catch { } throw; }
            password.Clear();
            if (!turnOn) await Task.Run(SecureService.StopIfRunning);
            log.Write(LogLevel.Information, "远程解锁", turnOn ? "远程解锁设置已启用" : "远程解锁已关闭");
            saving = false; DialogResult = DialogResult.OK; Close();
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.ComponentModel.Win32Exception or TimeoutException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException) {
            log.Write(LogLevel.Error, "远程解锁", "保存设置失败", e);
            MessageBox.Show("无法保存远程解锁设置或启动安全服务，请查看日志。\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        } finally { saving = false; save.Enabled = true; cancel.Enabled = true; enabled.Enabled = true; password.Enabled = true; UpdateControls(); }
    }
    static Label Label(string text, float size, bool bold = false, bool muted = false) => new() { Text = text, ForeColor = muted ? Muted : TextColor, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Dock = DockStyle.Fill };
    static Button Button(string text, bool primary = false) { var button = new Button { Text = text, AutoSize = true, Height = 32, BackColor = primary ? Theme : Surface, ForeColor = primary ? Background : TextColor, FlatStyle = FlatStyle.Flat, Padding = new Padding(8, 0, 8, 0) }; button.FlatAppearance.BorderSize = 0; return button; }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); var dark = 1; try { if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int)); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { } }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

class DashboardForm : Form
{
    static readonly Color Background = ColorTranslator.FromHtml("#111416"), CardColor = ColorTranslator.FromHtml("#202527"), Border = ColorTranslator.FromHtml("#394347"), TextColor = ColorTranslator.FromHtml("#F1F5F5"), Muted = ColorTranslator.FromHtml("#9CA8A9"), Theme = ColorTranslator.FromHtml("#45ACD7");
    readonly PairingAuth pairing;
    readonly DeviceConnections devices;
    readonly FileLog log;
    readonly Label code = Label("", 30, FontStyle.Bold), expiry = Label("", 10, color: Muted), server = Label("正在启动…", 10, color: Muted), neutrino = Label("Neutrino · 检查中", 10, color: Muted), footer = Label("", 9, color: Muted), deviceCount = Label("0 台设备在线", 10, color: Muted);
    readonly ComboBox addresses = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = ColorTranslator.FromHtml("#30383B"), ForeColor = TextColor, Font = new Font("Microsoft YaHei UI", 10) };
    readonly FlowLayoutPanel deviceList = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = CardColor, Padding = new Padding(0, 4, 12, 8) };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    readonly HttpClient health = new() { Timeout = TimeSpan.FromSeconds(3) };
    string signature = "";
    int ticks;
    bool checking;
    record Address(string Label, string Url, int Priority = 0) { public override string ToString() => Label; }

    public DashboardForm(PairingAuth pairing, DeviceConnections devices, FileLog log, Action? openSettings = null) {
        this.pairing = pairing; this.devices = devices; this.log = log;
        Text = "Neutrino HLG"; Icon = AppBrand.Icon; BackColor = Background; ForeColor = TextColor; Font = new Font("Microsoft YaHei UI", 10);
        MinimumSize = new Size(850, 620); Size = new Size(1040, 720); StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 18, 28, 18), ColumnCount = 1, RowCount = 3, BackColor = Background };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 75)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43)); Controls.Add(root);
        var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Background };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200)); head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        var title = Label("✳  NEUTRINO HLG", 16, FontStyle.Bold); title.Dock = DockStyle.Fill; title.TextAlign = ContentAlignment.MiddleLeft;
        server.Dock = DockStyle.Fill; server.TextAlign = ContentAlignment.MiddleRight; head.Controls.Add(title, 0, 0); head.Controls.Add(server, 1, 0);
        var settings = Button("设置"); settings.Anchor = AnchorStyles.Right; settings.Click += (_, _) => openSettings?.Invoke(); head.Controls.Add(settings, 2, 0); root.Controls.Add(head, 0, 0);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Background };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54)); root.Controls.Add(body, 0, 1);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 9, 0), BackColor = Background };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 52)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 48)); body.Controls.Add(left, 0, 0);
        left.Controls.Add(PairCard(), 0, 0); left.Controls.Add(AddressCard(), 0, 1);
        body.Controls.Add(DevicesCard(), 1, 0);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Background };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        footer.Dock = DockStyle.Fill; footer.TextAlign = ContentAlignment.MiddleLeft; neutrino.Dock = DockStyle.Fill; neutrino.TextAlign = ContentAlignment.MiddleRight;
        bottom.Controls.Add(footer, 0, 0); bottom.Controls.Add(neutrino, 1, 0); root.Controls.Add(bottom, 0, 2);
        timer.Tick += async (_, _) => { RefreshCode(); RefreshDevices(); if (++ticks % 5 == 1) { RefreshAddresses(); await CheckNeutrino(); } };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); health.Dispose(); };
        deviceList.SizeChanged += (_, _) => ResizeDeviceCards();
        RefreshCode(); RefreshAddresses(); RefreshDevices(); timer.Start();
    }
    static Label Label(string text, float size, FontStyle style = FontStyle.Regular, Color? color = null) => new() { Text = text, ForeColor = color ?? TextColor, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", size, style), AutoEllipsis = true };
    static Button Button(string text, bool primary = false) {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme : ColorTranslator.FromHtml("#30383B"), ForeColor = primary ? ColorTranslator.FromHtml("#10232B") : TextColor, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold), Cursor = Cursors.Hand, Height = 36, AutoSize = true, Padding = new Padding(12, 0, 12, 0), Margin = new Padding(0, 0, 8, 0) };
        b.FlatAppearance.BorderSize = 0; return b;
    }
    static TableLayoutPanel Content(int rows) { var t = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = CardColor, ColumnCount = 1, RowCount = rows, Padding = new Padding(22, 16, 22, 16) }; t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return t; }
    static Panel Card(Control child, Padding margin) { var p = new Panel { Dock = DockStyle.Fill, BackColor = CardColor, Padding = new Padding(1), Margin = margin }; p.Paint += (_, e) => { using var pen = new Pen(Border); e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1); }; p.Controls.Add(child); return p; }
    Control PairCard() {
        var t = Content(5); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var title = Label("连接此电脑", 14, FontStyle.Bold); title.Dock = DockStyle.Fill; var note = Label(Environment.MachineName + "  ·  在手机网页输入下方配对码", 9, color: Muted); note.Dock = DockStyle.Fill;
        code.Dock = DockStyle.Fill; code.TextAlign = ContentAlignment.MiddleLeft; code.ForeColor = Theme; expiry.Dock = DockStyle.Fill;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = CardColor }; var copy = Button("复制配对码", true); var refresh = Button("刷新配对码");
        copy.Click += (_, _) => Copy(pairing.PairingCode); refresh.Click += (_, _) => { pairing.RefreshCode(); RefreshCode(); log.Write(LogLevel.Information, "配对", "已刷新配对码"); };
        actions.Controls.Add(copy); actions.Controls.Add(refresh); t.Controls.Add(title, 0, 0); t.Controls.Add(note, 0, 1); t.Controls.Add(code, 0, 2); t.Controls.Add(expiry, 0, 3); t.Controls.Add(actions, 0, 4); return Card(t, new Padding(0, 0, 0, 8));
    }
    Control AddressCard() {
        var t = Content(5); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var title = Label("访问地址", 14, FontStyle.Bold); title.Dock = DockStyle.Fill;
        var note = Label("选择手机可以访问的网络地址", 9, color: Muted); note.Dock = DockStyle.Fill;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = CardColor };
        var copy = Button("复制地址", true); var open = Button("本机打开"); var logs = Button("日志目录");
        copy.Click += (_, _) => { if (addresses.SelectedItem is Address address) Copy(address.Url); };
        open.Click += (_, _) => Open((string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HLG_HTTPS_CERT")) ? "http" : "https") + "://localhost:8767");
        logs.Click += (_, _) => Open(Path.GetDirectoryName(log.Path)!);
        actions.Controls.Add(copy); actions.Controls.Add(open); actions.Controls.Add(logs);
        t.Controls.Add(title, 0, 0); t.Controls.Add(note, 0, 1); t.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = CardColor }, 0, 2); t.Controls.Add(addresses, 0, 3); t.Controls.Add(actions, 0, 4);
        return Card(t, new Padding(0, 8, 0, 0));
    }
    Control DevicesCard() {
        var t = Content(3); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = Label("连接的设备", 14, FontStyle.Bold); title.Dock = DockStyle.Fill; deviceCount.Dock = DockStyle.Fill;
        t.Controls.Add(title, 0, 0); t.Controls.Add(deviceCount, 0, 1); t.Controls.Add(deviceList, 0, 2); return Card(t, new Padding(9, 0, 0, 0));
    }
    void RefreshCode() { var remaining = pairing.PairingExpires - DateTimeOffset.UtcNow; code.Text = pairing.PairingCode.Insert(3, "  "); expiry.Text = pairing.PairingLocked ? "尝试次数已用尽，请刷新配对码" : remaining > TimeSpan.Zero ? $"有效期剩余 {Math.Max(0, (int)Math.Ceiling(remaining.TotalMinutes))} 分钟" : "配对码已过期，请刷新"; }
    void RefreshAddresses() {
        var scheme = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HLG_HTTPS_CERT")) ? "http" : "https";
        var list = new List<Address>();
        try {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)) {
                var properties = adapter.GetIPProperties();
                var physical = adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;
                foreach (var ip in properties.UnicastAddresses.Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))) {
                    var octets = ip.GetAddressBytes();
                    var lan = octets[0] == 10 || octets[0] == 172 && octets[1] is >= 16 and <= 31 || octets[0] == 192 && octets[1] == 168;
                    var priority = (physical ? 100 : 0) + (properties.GatewayAddresses.Count > 0 ? 40 : 0) + (lan ? 20 : 0);
                    list.Add(new Address(ip + "  ·  " + adapter.Name, $"{scheme}://{ip}:8767", priority));
                }
            }
        } catch (NetworkInformationException e) { log.Write(LogLevel.Warning, "网络", "读取网络地址失败", e); }
        list = list.OrderByDescending(a => a.Priority).ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (list.Count == 0) list.Add(new Address("127.0.0.1  ·  本机", $"{scheme}://127.0.0.1:8767"));
        var previous = (addresses.SelectedItem as Address)?.Url; var next = string.Join("|", list.Select(a => a.Label + a.Url));
        if (next == (string?)addresses.Tag) return;
        addresses.BeginUpdate(); addresses.Items.Clear(); foreach (var a in list) addresses.Items.Add(a); addresses.Tag = next;
        var index = list.FindIndex(a => a.Url == previous); addresses.SelectedIndex = index >= 0 ? index : 0; addresses.EndUpdate();
    }
    void RefreshDevices() {
        var current = devices.List(); var pending = pairing.PendingApprovals();
        deviceCount.Text = current.Count(d => d.Online) + " 台设备在线" + (pending.Count > 0 ? "  ·  " + pending.Count + " 项信任申请" : "");
        var next = string.Join("|", current.Select(d => $"{d.Id},{d.Name},{d.Address},{d.Browser},{d.Trusted},{d.Online},{d.Screen},{d.Terminal},{d.Blocked}")) + ";" + string.Join("|", pending.Select(p => $"{p.Id},{p.Name},{p.Address},{p.Browser}"));
        if (signature == next && deviceList.Controls.Count > 0) return;
        signature = next; deviceList.SuspendLayout(); foreach (Control c in deviceList.Controls.Cast<Control>().ToArray()) c.Dispose(); deviceList.Controls.Clear();
        if (current.Count == 0 && pending.Count == 0) { var empty = Label("暂无连接设备\n\n手机访问左侧地址并完成配对后，将显示在这里。", 10, color: Muted); empty.Width = Math.Max(200, deviceList.ClientSize.Width - 20); empty.Height = 90; deviceList.Controls.Add(empty); }
        foreach (var item in pending) {
            var card = new Panel { Height = 108, Width = Math.Max(200, deviceList.ClientSize.Width - 24), Margin = new Padding(0, 0, 0, 9), BackColor = ColorTranslator.FromHtml("#293134") };
            var name = Label(item.Name + "  ·  请求信任", 11, FontStyle.Bold); name.SetBounds(14, 10, Math.Max(140, card.Width - 28), 25); name.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            var info = Label(item.Address + "  ·  " + item.Browser + "  ·  请确认是否为你的设备", 9, color: Muted); info.SetBounds(14, 36, Math.Max(140, card.Width - 28), 23); info.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            var actions = new FlowLayoutPanel { Left = 14, Top = 65, Width = Math.Max(180, card.Width - 28), Height = 36, BackColor = card.BackColor, WrapContents = false, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            var approve = Button("允许信任", true); approve.Height = 30;
            approve.Click += (_, _) => { if (MessageBox.Show("允许 " + item.Name + "（" + item.Address + "）在此 HTTPS 地址免配对码控制电脑？", "确认信任设备", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return; try { pairing.Approve(item.Id); log.Write(LogLevel.Information, "设备", "已允许设备信任"); footer.Text = "已允许信任，手机将自动连接。"; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException) { log.Write(LogLevel.Error, "设备", "无法保存设备信任", e); MessageBox.Show("无法保存信任，请查看日志。", "Neutrino HLG", MessageBoxButtons.OK, MessageBoxIcon.Error); } RefreshDevices(); };
            var reject = Button("拒绝"); reject.Height = 30; reject.Click += (_, _) => { pairing.RejectApproval(item.Id); RefreshDevices(); };
            actions.Controls.Add(approve); actions.Controls.Add(reject); card.Controls.Add(name); card.Controls.Add(info); card.Controls.Add(actions); deviceList.Controls.Add(card);
        }
        foreach (var item in current) {
            var card = new Panel { Height = 108, Width = Math.Max(200, deviceList.ClientSize.Width - 24), Margin = new Padding(0, 0, 0, 9), BackColor = ColorTranslator.FromHtml("#293134") };
            var name = Label(item.Name + (item.Trusted ? "  ·  已信任" : ""), 11, FontStyle.Bold); name.SetBounds(14, 10, Math.Max(140, card.Width - 28), 25); name.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            var status = item.Blocked ? "已断开 · 等待允许重连" : (item.Screen ? "远程画面" : item.Terminal ? "终端" : "在线");
            var info = Label(item.Address + "  ·  " + item.Browser + "  ·  " + status + "  ·  " + item.ConnectedAt.ToLocalTime().ToString("HH:mm") + " 连接", 9, color: Muted); info.SetBounds(14, 36, Math.Max(140, card.Width - 28), 23); info.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            var actions = new FlowLayoutPanel { Left = 14, Top = 65, Width = Math.Max(180, card.Width - 28), Height = 36, BackColor = card.BackColor, WrapContents = false, Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right };
            var action = Button(item.Blocked ? "允许重连" : "断开连接"); action.Height = 30;
            action.Click += (_, _) => { if (item.Blocked) { devices.Allow(item.Id); footer.Text = "已允许重连，请在手机刷新页面。"; } else if (MessageBox.Show("确定断开此设备的连接？", "Neutrino HLG", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) devices.Disconnect(item.Id); RefreshDevices(); };
            actions.Controls.Add(action);
            if (item.Trusted) { var revoke = Button("取消信任"); revoke.Height = 30; revoke.Click += (_, _) => { if (MessageBox.Show("取消信任后，设备需要重新配对。继续吗？", "Neutrino HLG", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return; try { pairing.Revoke(item.Id); log.Write(LogLevel.Information, "设备", "已取消设备信任"); RefreshDevices(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { log.Write(LogLevel.Error, "设备", "无法取消设备信任", e); MessageBox.Show("无法保存信任记录，请查看日志。", "Neutrino HLG", MessageBoxButtons.OK, MessageBoxIcon.Error); } }; actions.Controls.Add(revoke); }
            card.Controls.Add(name); card.Controls.Add(info); card.Controls.Add(actions); deviceList.Controls.Add(card);
        }
        deviceList.ResumeLayout();
    }
    void ResizeDeviceCards() { foreach (Control c in deviceList.Controls) c.Width = Math.Max(200, deviceList.ClientSize.Width - 24); }
    async Task CheckNeutrino() {
        if (checking) return; checking = true;
        try {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(NeutrinoPaths.Home, "api.json")));
            var url = new Uri(json?["url"]?.GetValue<string>() ?? "");
            if (url.Scheme != "http" || url.Host != "127.0.0.1") throw new InvalidDataException();
            using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString().TrimEnd('/') + "/capabilities");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", json?["token"]?.GetValue<string>() ?? "");
            using var response = await health.SendAsync(request); if (!IsDisposed) neutrino.Text = response.IsSuccessStatusCode ? "Neutrino · 已连接" : "Neutrino · 未连接";
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException or HttpRequestException or TaskCanceledException or InvalidDataException or ObjectDisposedException) { if (!IsDisposed) neutrino.Text = "Neutrino · 未连接"; }
        finally { checking = false; }
    }
    void Copy(string text) { try { Clipboard.SetText(text); footer.Text = "已复制到剪切板"; } catch (System.Runtime.InteropServices.ExternalException) { footer.Text = "复制失败，请重试"; } }
    void Open(string target) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { log.Write(LogLevel.Warning, "桌面", "打开目标失败", e); footer.Text = "打开失败，请查看日志"; } }
    public void SetServerStatus(bool online, string? error) { server.Text = online ? "●  服务运行中" : error is null ? "●  服务未运行" : "●  服务未运行"; server.ForeColor = online ? Theme : ColorTranslator.FromHtml("#EFA397"); if (!online && error is not null) footer.Text = error; }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); var dark = 1; try { if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int)); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { } }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

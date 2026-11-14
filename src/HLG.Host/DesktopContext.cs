using System.Windows.Forms;
using Microsoft.Extensions.Logging;

sealed class DesktopContext : ApplicationContext
{
    readonly WebApplication host;
    readonly DashboardForm form;
    readonly NotifyIcon tray;
    readonly FileLog log;
    bool exiting;
    public DesktopContext(WebApplication host, PairingAuth pairing, DeviceConnections devices, FileLog log) {
        this.host = host; this.log = log;
        form = new DashboardForm(pairing, devices, log);
        MainForm = form;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 Neutrino HLG", null, (_, _) => ShowWindow());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        tray = new NotifyIcon { Text = "Neutrino HLG", Icon = AppBrand.Icon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowWindow();
        form.FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; form.Hide(); } };
        form.Shown += async (_, _) => {
            try { await host.StartAsync(); form.SetServerStatus(true, null); log.Write(LogLevel.Information, "主机", "服务已启动，监听端口 8767"); }
            catch (Exception e) { log.Write(LogLevel.Error, "主机", "服务启动失败", e); form.SetServerStatus(false, e.Message); MessageBox.Show("服务启动失败，请检查端口 8767 是否被占用。\n详情请查看日志。", "Neutrino HLG", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        form.Show();
    }
    public void ShowWindow() {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try { form.BeginInvoke(() => { if (form.IsDisposed) return; form.Show(); if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal; form.Activate(); }); }
        catch (InvalidOperationException) { }
    }
    async Task ExitAsync() {
        if (exiting) return; exiting = true;
        form.SetServerStatus(false, "正在退出…");
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await host.StopAsync(timeout.Token); }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException) { log.Write(LogLevel.Warning, "主机", "停止服务时发生问题", e); }
        finally { log.Write(LogLevel.Information, "主机", "服务已退出"); tray.Visible = false; form.Close(); ExitThread(); }
    }
    protected override void Dispose(bool disposing) { if (disposing) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); form.Dispose(); } base.Dispose(disposing); }
}

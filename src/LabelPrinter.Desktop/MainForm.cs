using System.Diagnostics;
using LabelPrinter.Core;
using LabelPrinter.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LabelPrinter.Desktop;

/// <summary>Native window hosting the app UI (WebView2) plus a tray icon. Closing the window keeps the app in the tray so phones can still print.</summary>
public sealed class MainForm : Form
{
    readonly string _url;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon _tray = new();
    bool _quitting, _trayTipShown;

    public MainForm(string url, bool startHidden)
    {
        _url = url;
        Text = AppInfo.Name;
        Width = 1220; Height = 860; MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Controls.Add(_web);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open RetroLabel", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitApp());
        _tray.Icon = Icon ?? SystemIcons.Application;
        _tray.Text = AppInfo.Name;
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        if (startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; Load += (_, _) => Hide(); }
        Shown += async (_, _) => await InitWebAsync();
    }

    async Task InitWebAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataPaths.Root, "webview"));
            await _web.EnsureCoreWebView2Async(env);
            var s = _web.CoreWebView2.Settings;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = true;
            s.IsZoomControlEnabled = true;
            // Links that leave the app open in the normal browser
            _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            _web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; if (e.Uri.StartsWith("http")) OpenExternal(e.Uri); }
            };
            _web.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 failed", ex);
            MessageBox.Show(this, "The Microsoft Edge WebView2 component is missing or failed to start, so the app will open in your browser instead.\n\n" + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            OpenExternal(_url);
        }
    }

    static void OpenExternal(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_trayTipShown)
            {
                _trayTipShown = true;
                _tray.ShowBalloonTip(4000, "Still running", "RetroLabel keeps running in the tray so phones can print. Right-click the tray icon → Quit to exit.", ToolTipIcon.Info);
            }
            return;
        }
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate(); BringToFront();
    }

    public void QuitApp() { _quitting = true; _tray.Visible = false; Close(); Application.Exit(); }
}

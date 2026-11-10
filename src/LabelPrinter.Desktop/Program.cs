using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LabelPrinter.Core;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Core.Web;
using AppContext = LabelPrinter.Core.Web.AppContext;

namespace LabelPrinter.Desktop;

static class Program
{
    const int DefaultPort = 47213;
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--diagnose"))
        {
            AttachConsole(-1);
            var text = DiagnosticsService.ToText(DiagnosticsService.Collect(new SettingsStore().Get(), true));
            var file = Path.Combine(DataPaths.Root, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, text);
            Console.WriteLine(text + $"\nSaved to {file}");
            return 0;
        }
        bool background = args.Contains("--background");
        int port = DefaultPort;

        // Already running? Ask it to show its window and leave.
        if (PingExisting(port, show: !background)) return 0;
        if (!PortFree(port)) port = FreePort();

        ApplicationConfiguration.Initialize();
        Log.Info($"Desktop app {AppInfo.Version} starting on port {port}");
        AppContext ctx;
        WebApplicationHolder web;
        try
        {
            ctx = new AppContext();
            web = new WebApplicationHolder(WebHost.BuildDesktop(ctx, port));
            web.App.StartAsync().GetAwaiter().GetResult();
            ctx.ApplyRemoteSetting();
#if HAS_ZXING
            LabelPrinter.Core.Imaging.BarcodeCheck.Decoder = LabelPrinter.Shared.ZxingDecoder.Decode;
#endif
            ctx.Watcher.Restart();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show("RetroLabel could not start:\n\n" + ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        using var form = new MainForm($"http://127.0.0.1:{port}/", background);
        ctx.OnQuit = () => form.BeginInvoke(form.QuitApp);
        ctx.OnShow = () => form.BeginInvoke(form.ShowFromTray);
        Application.Run(form);
        try { ctx.Remote.Stop(); web.App.StopAsync().GetAwaiter().GetResult(); ctx.Jobs.Dispose(); } catch { }
        return 0;
    }

    sealed class WebApplicationHolder { public Microsoft.AspNetCore.Builder.WebApplication App; public WebApplicationHolder(Microsoft.AspNetCore.Builder.WebApplication a) { App = a; } }

    static bool PingExisting(int port, bool show)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            var r = http.GetAsync($"http://127.0.0.1:{port}/icon.svg").GetAwaiter().GetResult();
            if (!r.IsSuccessStatusCode || r.Content.Headers.ContentType?.MediaType != "image/svg+xml") return false;
            if (show)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/show");
                req.Headers.Add("X-ZLP", "1");
                http.Send(req);
            }
            return true;
        }
        catch { return false; }
    }
    static bool PortFree(int port) { try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; } catch { return false; } }
    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
}

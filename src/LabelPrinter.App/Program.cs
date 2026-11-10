using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LabelPrinter.Core;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Core.Web;
using AppContext = LabelPrinter.Core.Web.AppContext;

namespace LabelPrinter.App;

static class Program
{
    const int DefaultPort = 47213;
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    static async Task<int> Main(string[] args)
    {
        bool background = args.Contains("--background");
        bool diagnose = args.Contains("--diagnose");
        string? dataDir = ArgValue(args, "--data-dir");
        int port = int.TryParse(ArgValue(args, "--port"), out var p) ? p : DefaultPort;
        if (dataDir != null) DataPaths.Root = dataDir;

        if (diagnose) return RunDiagnose();

        // Single instance: if our port already answers as this app, just show its window.
        if (await IsOurServerAsync(port))
        {
            if (!background) OpenWindow($"http://127.0.0.1:{port}/");
            return 0;
        }
        if (!PortFree(port)) port = FindFreePort(port + 1);

        Log.Info($"Starting RetroLabel {AppInfo.Version} on port {port}");
        var ctx = new AppContext();
        var desktop = WebHost.BuildDesktop(ctx, port);
        ctx.OnQuit = () => { try { desktop.Lifetime.StopApplication(); } catch { } };
        try { await desktop.StartAsync(); }
        catch (Exception ex)
        {
            Log.Error("Could not start the local UI server", ex);
            return 2;
        }
        ctx.ApplyRemoteSetting();
        ctx.Watcher.Restart();
        if (!background && !args.Contains("--no-window")) OpenWindow($"http://127.0.0.1:{port}/");
        await desktop.WaitForShutdownAsync();
        ctx.Remote.Stop();
        ctx.Jobs.Dispose();
        return 0;
    }

    static string? ArgValue(string[] a, string name)
    {
        var i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    static int RunDiagnose()
    {
        if (OperatingSystem.IsWindows()) AttachConsole(-1);
        var st = new SettingsStore().Get();
        var text = DiagnosticsService.ToText(DiagnosticsService.Collect(st, true));
        var file = Path.Combine(DataPaths.Root, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(file, text);
        Console.WriteLine(text);
        Console.WriteLine($"\nSaved to {file}");
        return 0;
    }

    static async Task<bool> IsOurServerAsync(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            var r = await http.GetAsync($"http://127.0.0.1:{port}/icon.svg");
            return r.IsSuccessStatusCode && (r.Content.Headers.ContentType?.MediaType == "image/svg+xml");
        }
        catch { return false; }
    }
    static bool PortFree(int port)
    {
        try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; } catch { return false; }
    }
    static int FindFreePort(int from)
    {
        for (int p = from; p < from + 50; p++) if (PortFree(p)) return p;
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port;
    }

    /// <summary>Open the UI as a standalone app window (Edge/Chrome --app mode), falling back to the default browser.</summary>
    static void OpenWindow(string url)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine($"UI: {url}"); return; }
        foreach (var exe in new[] { "msedge", "chrome" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, $"--app={url} --window-size=1180,820") { UseShellExecute = true });
                return;
            }
            catch { /* try next */ }
        }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("Could not open a browser window", ex); }
    }
}

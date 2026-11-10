using System.Diagnostics;
using System.Text;
using LabelPrinter.Core.Printing;

namespace LabelPrinter.Core.Diagnostics;

public class Finding { public string Level { get; set; } = "info"; public string Text { get; set; } = ""; }

public class DiagnosticsReport
{
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public string AppVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public bool WindowsSpooler { get; set; }
    public string SelectedPrinter { get; set; } = "";
    public string Backend { get; set; } = "";
    public PrinterInfo? Printer { get; set; }
    public List<PrinterInfo> AllPrinters { get; set; } = new();
    public List<PortInfo> UsbAndParallelPorts { get; set; } = new();
    public List<SpoolJob> QueueJobs { get; set; } = new();
    public List<string> PnpDevices { get; set; } = new();
    public List<UsbPrinterDevice> UsbPrinters { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
}

public static class DiagnosticsService
{
    public static DiagnosticsReport Collect(AppSettings st, bool includePnp)
    {
        var r = new DiagnosticsReport
        {
            AppVersion = AppInfo.Version,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            WindowsSpooler = SpoolerApi.IsAvailable,
            SelectedPrinter = st.PrinterName,
            Backend = st.Backend.ToString(),
        };
        if (!SpoolerApi.IsAvailable)
        {
            r.Findings.Add(new() { Level = "warn", Text = "Not running on Windows: the Windows print spooler is unavailable. Only 'Save EPL to file' works here." });
            return r;
        }
        r.UsbPrinters = DirectUsb.List();
        r.AllPrinters = SpoolerApi.GetPrinters();
        r.UsbAndParallelPorts = SpoolerApi.GetPorts().Where(p => p.Name.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("DOT4", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(st.PrinterName))
        {
            r.Printer = r.AllPrinters.FirstOrDefault(p => string.Equals(p.Name, st.PrinterName, StringComparison.OrdinalIgnoreCase));
            if (r.Printer != null) r.QueueJobs = SpoolerApi.GetJobs(r.Printer.Name);
        }
        if (includePnp) r.PnpDevices = QueryPnp();
        Analyze(r, st);
        return r;
    }

    static void Analyze(DiagnosticsReport r, AppSettings st)
    {
        void Add(string lvl, string t) => r.Findings.Add(new Finding { Level = lvl, Text = t });
        if (st.Backend == BackendKind.DirectUsb)
        {
            if (r.UsbPrinters.Count == 0) { Add("bad", "No USB printer device found. Plug the StarTech adapter into the PC and turn the LP 2443 on. Device Manager should then show 'USB Printing Support' under Universal Serial Bus controllers. If it shows a yellow warning instead, the adapter needs StarTech's driver."); return; }
            foreach (var u in r.UsbPrinters)
                Add(u.LooksLikeZebra ? "ok" : "info", $"USB printer device: {u.Name}. Printer ID: {(string.IsNullOrEmpty(u.DeviceId1284) ? "(printer did not report one; normal for some older printers)" : u.DeviceId1284)}");
            try { var d = DirectUsb.Resolve(st.DirectDevicePath); Add("ok", $"Labels will be sent directly to: {d.Name}. No Windows printer queue is needed."); }
            catch (PrintException e) { Add("bad", e.Full); }
            return;
        }
        if (string.IsNullOrWhiteSpace(st.PrinterName)) { Add("bad", "No printer selected. Choose your Zebra queue in Settings > Printer."); return; }
        var p = r.Printer;
        if (p == null)
        {
            Add("bad", $"Windows has no printer named '{st.PrinterName}'. Pick another queue in Settings, or add the printer (see README, step 3).");
            return;
        }
        Add("ok", $"Queue '{p.Name}' exists (driver: {p.Driver}, port: {p.Port}).");
        var port = r.UsbAndParallelPorts.FirstOrDefault(x => x.Name.Equals(p.Port, StringComparison.OrdinalIgnoreCase));
        if (p.Offline) Add("bad", "Windows reports the printer OFFLINE / not available. Check: printer powered on, parallel cable seated at both ends, adapter plugged into USB. If you moved the adapter to a different USB socket, Windows may have created a NEW port (USB002 ...) and the queue still points to the old one: see 'Move queue to port' below.");
        else if (p.Status != 0) Add("warn", $"Queue status: {p.StatusText}.");
        else Add("ok", "Queue status: ready.");
        if (p.Port.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
        {
            if (port == null) Add("bad", $"The queue uses port {p.Port} but that port no longer exists. The adapter is unplugged or is now on another USB socket.");
            else Add("ok", $"Port {p.Port} exists ({port.Description}).");
            var others = r.UsbAndParallelPorts.Where(x => x.Name.StartsWith("USB", StringComparison.OrdinalIgnoreCase) && !x.Name.Equals(p.Port, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).ToList();
            if (others.Count > 0) Add("info", $"Other USB printer ports present: {string.Join(", ", others)}.");
        }
        else if (p.Port.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            Add("info", $"Queue is on {p.Port}. Fine for a real parallel port; a USB-to-parallel adapter normally appears as USB00x instead.");
        else if (p.Port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            Add("warn", "Queue is on a COM port (serial). You asked for the parallel path; serial needs baud/handshake settings.");
        if (st.Backend == BackendKind.RawEpl2 && !p.Driver.Contains("Generic", StringComparison.OrdinalIgnoreCase) && !p.Driver.Contains("Zebra", StringComparison.OrdinalIgnoreCase) && !p.Driver.Contains("ZDesigner", StringComparison.OrdinalIgnoreCase) && !p.Driver.Contains("Eltron", StringComparison.OrdinalIgnoreCase))
            Add("info", $"Driver '{p.Driver}' is not a Generic/Text-Only or Zebra driver. RAW EPL2 normally works with any driver that allows the RAW data type, but 'Generic / Text Only' is the simplest.");
        if (r.QueueJobs.Count > 0)
        {
            var stuck = r.QueueJobs.Where(j => (DateTime.UtcNow - j.Submitted).TotalMinutes > 2).ToList();
            if (stuck.Count > 0) Add("warn", $"{stuck.Count} job(s) have been waiting in the Windows queue for over 2 minutes. Usually the printer is offline, or the job is stuck. Cancel them below and retry.");
            else Add("info", $"{r.QueueJobs.Count} job(s) currently in the queue.");
        }
        else Add("ok", "No jobs waiting in the Windows queue.");
    }

    /// <summary>Which relevant devices does Windows currently see? (PowerShell Get-PnpDevice, 15 s timeout)</summary>
    public static List<string> QueryPnp()
    {
        const string script = "Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'ICUSB|1284|parallel|IEEE|Prolific|PL2305|USB Printing|Zebra|2443|Eltron|DOT4|LPT' -or $_.Class -in 'Printer','PrintQueue' } | ForEach-Object { '{0} | {1} | {2} | {3}' -f $_.Status,$_.Class,$_.FriendlyName,$_.InstanceId }";
        var o = RunPowerShell(script, 15000);
        return o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public static string RunPowerShell(string script, int timeoutMs)
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return "(PowerShell timed out)"; }
            return o.Result;
        }
        catch (Exception ex) { return "(could not run PowerShell: " + ex.Message + ")"; }
    }

    /// <summary>Point an existing queue at another port (e.g. USB002 after the adapter moved). Needs admin rights, so it shows the Windows UAC prompt.</summary>
    public static string MoveQueueToPort(string printer, string port)
    {
        if (!OperatingSystem.IsWindows()) return "Windows only.";
        if (printer.Any(c => c is '\'' or '"' or '`' or '$' or ';' or '\n') || port.Any(c => !(char.IsLetterOrDigit(c) || c == ':' || c == '_')))
            return "Refused: printer or port name contains unsafe characters.";
        var cmd = $"Set-Printer -Name '{printer}' -PortName '{port}'";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{cmd.Replace("\"", "\\\"")}\"") { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            Process.Start(psi);
            return "Windows asked for administrator permission. After approving, wait 5 seconds and refresh Diagnostics.";
        }
        catch (Exception ex) { return "Could not start the elevated command: " + ex.Message; }
    }

    public static string ToText(DiagnosticsReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"RetroLabel LP2443 diagnostics  {r.TimeUtc:u}");
        sb.AppendLine($"App {r.AppVersion} | {r.Os}");
        sb.AppendLine($"Selected printer : {(string.IsNullOrEmpty(r.SelectedPrinter) ? "(none)" : r.SelectedPrinter)}   Backend: {r.Backend}");
        if (r.Printer != null)
            sb.AppendLine($"Driver           : {r.Printer.Driver}\nPort             : {r.Printer.Port}\nStatus           : {r.Printer.StatusText} (0x{r.Printer.Status:X})\nJobs in queue    : {r.Printer.JobCount}");
        sb.AppendLine("\nFindings:");
        foreach (var f in r.Findings) sb.AppendLine($"  [{f.Level.ToUpper()}] {f.Text}");
        sb.AppendLine("\nUSB printer devices (direct mode):");
        foreach (var u in r.UsbPrinters) sb.AppendLine($"  {u.Name} | ID: {u.DeviceId1284} | {u.Path}");
        sb.AppendLine("\nAll Windows printers:");
        foreach (var p in r.AllPrinters) sb.AppendLine($"  {p.Name} | {p.Driver} | {p.Port} | {p.StatusText}");
        sb.AppendLine("\nUSB / parallel ports:");
        foreach (var p in r.UsbAndParallelPorts) sb.AppendLine($"  {p.Name} | {p.Monitor} | {p.Description}");
        sb.AppendLine("\nQueue jobs:");
        foreach (var j in r.QueueJobs) sb.AppendLine($"  #{j.Id} {j.Document} | {j.User} | {j.StatusText}");
        if (r.PnpDevices.Count > 0) { sb.AppendLine("\nPnP devices (status | class | name | instance):"); foreach (var d in r.PnpDevices) sb.AppendLine("  " + d); }
        return sb.ToString();
    }
}

public static class AppInfo
{
    public const string Version = "2.0.0";
    public const string Name = "RetroLabel LP2443";
    public const string Repo = "lanceamcwilson-dot/retrolabel-lp2443";
    public const string Disclaimer = "Works with Zebra/Eltron LP 2443 and other EPL2 printers. Not affiliated with or endorsed by Zebra Technologies.";
}

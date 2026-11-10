using LabelPrinter.Core.Imaging;
using LabelPrinter.Core.Pdf;
using AppContext = LabelPrinter.Core.Web.AppContext;

namespace LabelPrinter.Core.Jobs;

/// <summary>
/// Watches a folder (Downloads by default). When a new PDF lands there and looks like a shipping label, it is loaded,
/// auto-cropped and shown in the app window, ready for one click on Print. Nothing prints automatically.
/// </summary>
public class FolderWatcher : IDisposable
{
    readonly AppContext _ctx;
    FileSystemWatcher? _fsw;
    readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    long _seq;
    public bool Active => _fsw != null;
    public string Folder { get; private set; } = "";
    public string? LastError { get; private set; }

    public FolderWatcher(AppContext ctx) { _ctx = ctx; }

    public static string DefaultFolder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public void Restart()
    {
        Stop();
        var s = _ctx.Settings.Get();
        if (!s.WatchEnabled) return;
        Folder = string.IsNullOrWhiteSpace(s.WatchFolder) ? DefaultFolder() : s.WatchFolder;
        try
        {
            if (!Directory.Exists(Folder)) { LastError = $"Folder not found: {Folder}"; return; }
            _fsw = new FileSystemWatcher(Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            _fsw.Created += (_, e) => Consider(e.FullPath);
            _fsw.Renamed += (_, e) => Consider(e.FullPath);    // browsers download to .crdownload/.part then rename
            _fsw.EnableRaisingEvents = true;
            LastError = null;
            Log.Info($"Watching {Folder} for label PDFs");
        }
        catch (Exception ex) { LastError = ex.Message; _fsw = null; Log.Warn("Folder watch failed: " + ex.Message); }
    }

    public void Stop() { _fsw?.Dispose(); _fsw = null; }

    void Consider(string path)
    {
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        lock (_seen) { if (!_seen.Add(path)) return; }
        Task.Run(async () =>
        {
            try
            {
                var data = await WaitUntilComplete(path);
                if (data == null) return;
                var r = Ingest(data, Path.GetFileName(path));
                if (r == null) return;
                _ctx.Incoming = (r.Id, Interlocked.Increment(ref _seq));
                _ctx.OnShow?.Invoke();
            }
            catch (Exception ex) { Log.Warn($"Could not load {path}: {ex.Message}"); }
            finally { _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => { lock (_seen) _seen.Remove(path); }); }
        });
    }

    /// <summary>Wait until the browser has finished writing the file (size stable and readable), max ~60 s.</summary>
    static async Task<byte[]?> WaitUntilComplete(string path)
    {
        long last = -1;
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(1000);
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                if (fi.Length > DocumentStore.MaxBytes) return null;
                if (fi.Length > 0 && fi.Length == last)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var b = new byte[fs.Length]; fs.ReadExactly(b);
                    return b;
                }
                last = fi.Length;
            }
            catch (IOException) { /* still being written */ }
        }
        return null;
    }

    /// <summary>Store the PDF if it looks like a label. Returns null when it doesn't (and only-labels mode is on).</summary>
    public DocInfo? Ingest(byte[] data, string name)
    {
        var s = _ctx.Settings.Get();
        DocInfo info;
        try { info = _ctx.Docs.Save(data, name, "Downloads"); }
        catch (PdfException) { return null; }
        if (!s.WatchOnlyLabels || LooksLikeLabel(info)) { Log.Info($"Picked up {name} from the watched folder"); return info; }
        Log.Info($"Ignored {name}: does not look like a shipping label");
        return null;
    }

    bool LooksLikeLabel(DocInfo info)
    {
        bool labelSized = info.PageSizesIn.Any(p => (Math.Abs(p[0] - 4) < 0.2 && Math.Abs(p[1] - 6) < 0.2) || (Math.Abs(p[0] - 6) < 0.2 && Math.Abs(p[1] - 4) < 0.2));
        if (labelSized) return true;
        if (info.Detected.Any(r => r.IsCrop && r.Note.StartsWith("auto-cropped"))) return true;   // dashed/solid cut-line box
        if (info.PdfPageCount > 6) return false;
        if (!BarcodeCheck.Available) return false;
        using var doc = _ctx.Docs.Open(info.Id);
        foreach (var r in info.Detected.Take(4))
            if (BarcodeCheck.DecodeSource(doc, r).Count > 0) return true;
        return false;
    }

    public void Dispose() => Stop();
}

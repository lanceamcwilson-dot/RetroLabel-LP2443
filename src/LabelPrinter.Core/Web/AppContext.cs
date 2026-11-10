using LabelPrinter.Core.Imaging;
using LabelPrinter.Core.Jobs;
using LabelPrinter.Core.Remote;

namespace LabelPrinter.Core.Web;

public class AppContext
{
    public SettingsStore Settings { get; } = new();
    public DocumentStore Docs { get; } = new();
    public JobManager Jobs { get; }
    public DeviceRegistry Devices { get; } = new();
    public RemoteServer Remote { get; }
    public PreviewCache Previews { get; } = new();
    public CheckCache Checks { get; } = new();
    public int DesktopPort { get; set; }
    public Action? OnQuit { get; set; }
    public Action? OnShow { get; set; }
    public Jobs.FolderWatcher Watcher { get; }
    public Diagnostics.UpdateCheck Updates { get; }
    /// <summary>Last PDF picked up from the watched folder (the desktop UI opens it automatically).</summary>
    public (string docId, long seq)? Incoming { get; set; }

    public AppContext()
    {
        Jobs = new JobManager(Settings, Docs);
        Remote = new RemoteServer(this);
        Watcher = new Jobs.FolderWatcher(this);
        Updates = new Diagnostics.UpdateCheck(Settings);
    }
    public void ApplyRemoteSetting()
    {
        var s = Settings.Get();
        if (s.RemoteEnabled) Remote.Restart(); else Remote.Stop();
    }
}

public class CheckCache
{
    readonly Dictionary<string, BarcodeCheck.Result> _map = new();
    readonly object _l = new();
    public BarcodeCheck.Result Get(AppContext ctx, string docId, int page, LabelRaster printed)
    {
        var s = ctx.Settings.Get(); var meta = ctx.Docs.Get(docId);
        var key = $"{docId}|{page}|{printed.BlackCount()}|{printed.Width}x{printed.Height}|{System.Text.Json.JsonSerializer.Serialize(meta?.Manual)}|{s.AutoCrop}";
        lock (_l) if (_map.TryGetValue(key, out var hit)) return hit;
        var view = Jobs.DocumentStore.View(meta ?? throw new Pdf.PdfException("That PDF is no longer stored."), s.AutoCrop);
        BarcodeCheck.Result r;
        using (var doc = ctx.Docs.Open(docId)) r = BarcodeCheck.Check(doc, view.Labels[page], printed);
        lock (_l) { if (_map.Count > 50) _map.Clear(); _map[key] = r; }
        return r;
    }
}

/// <summary>Tiny cache of composed rasters so dragging a slider does not re-render every time.</summary>
public class PreviewCache
{
    readonly object _l = new();
    readonly Dictionary<string, LabelRaster> _map = new();
    readonly Queue<string> _order = new();
    public LabelRaster Get(AppContext ctx, string docId, int page)
    {
        var s = ctx.Settings.Get();
        var meta = ctx.Docs.Get(docId);
        var key = $"{docId}|{page}|{(meta == null ? "" : System.Text.Json.JsonSerializer.Serialize(meta.Manual))}|{System.Text.Json.JsonSerializer.Serialize(new { s.LabelWidthIn, s.LabelHeightIn, s.AutoCrop, s.FitCroppedLabels, s.ScaleMode, s.ScalePercent, s.AutoRotate, s.RotationDegrees, s.Anchor, s.OffsetXDots, s.OffsetYDots, s.Threshold, s.Supersample, s.InvertImage, s.PrintableWidthDots })}";
        lock (_l) if (_map.TryGetValue(key, out var hit)) return hit;
        LabelRaster r;
        var view = Jobs.DocumentStore.View(meta ?? throw new Pdf.PdfException("That PDF is no longer stored. Upload it again."), s.AutoCrop);
        if (page < 0 || page >= view.Labels.Count) throw new Pdf.PdfException($"Label {page + 1} does not exist (this PDF has {view.Labels.Count}).");
        using (var doc = ctx.Docs.Open(docId)) r = LabelComposer.Compose(doc, view.Labels[page], s);
        lock (_l)
        {
            _map[key] = r; _order.Enqueue(key);
            while (_order.Count > 24) _map.Remove(_order.Dequeue());
        }
        return r;
    }
}

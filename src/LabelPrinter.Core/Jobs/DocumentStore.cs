using LabelPrinter.Core.Imaging;
using LabelPrinter.Core.Pdf;

namespace LabelPrinter.Core.Jobs;

public class DocInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Number of LABELS (after auto-crop) when returned through <see cref="DocumentStore.View"/>.</summary>
    public int PageCount { get; set; }
    public int PdfPageCount { get; set; }
    public List<LabelRegion> Detected { get; set; } = new();
    public List<LabelRegion> Labels { get; set; } = new();
    public bool AutoCropped { get; set; }
    /// <summary>Areas the user drew by hand. When present they replace auto-crop for this PDF.</summary>
    public List<LabelRegion> Manual { get; set; } = new();
    public bool ManualCrop { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "Desktop";
    public string OwnerId { get; set; } = "desktop";
    public List<double[]> PageSizesIn { get; set; } = new();
}

/// <summary>Uploaded PDFs on disk (uploads/&lt;id&gt;.pdf + .json). Deleted automatically after the retention period.</summary>
public class DocumentStore
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int MaxPages = 50;
    readonly object _l = new();

    public string PdfPath(string id) => Path.Combine(DataPaths.Uploads, SafeId(id) + ".pdf");
    string MetaPath(string id) => Path.Combine(DataPaths.Uploads, SafeId(id) + ".json");
    static string SafeId(string id)
    {
        if (id.Length is < 4 or > 40 || !id.All(c => char.IsAsciiLetterOrDigit(c))) throw new PdfException("Invalid document id.");
        return id;
    }

    public DocInfo Save(byte[] pdf, string name, string source, string ownerId = "desktop")
    {
        if (pdf.Length > MaxBytes) throw new PdfException($"PDF is too large ({pdf.Length / 1048576.0:0.#} MB, limit {MaxBytes / 1048576} MB).");
        using var d = PdfDocument.Open(pdf);   // validates header + parses
        if (d.PageCount < 1) throw new PdfException("The PDF has no pages.");
        if (d.PageCount > MaxPages) throw new PdfException($"The PDF has {d.PageCount} pages; the limit is {MaxPages}.");
        var info = new DocInfo
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            Name = CleanName(name),
            PageCount = d.PageCount,
            Source = source,
            OwnerId = ownerId,
        };
        for (int i = 0; i < d.PageCount; i++) { var (w, h) = d.GetPageSizeInches(i); info.PageSizesIn.Add(new[] { Math.Round(w, 3), Math.Round(h, 3) }); }
        info.PdfPageCount = d.PageCount;
        try { info.Detected = LabelDetector.Detect(d); }
        catch (Exception ex) { Log.Warn("Label detection failed: " + ex.Message); }
        lock (_l)
        {
            File.WriteAllBytes(PdfPath(info.Id), pdf);
            JsonFile.Save(MetaPath(info.Id), info);
        }
        return info;
    }

    static string CleanName(string n)
    {
        n = Path.GetFileName(n ?? "label.pdf");
        n = new string(n.Where(c => !char.IsControl(c)).ToArray());
        if (n.Length > 80) n = n[..80];
        return string.IsNullOrWhiteSpace(n) ? "label.pdf" : n;
    }

    public DocInfo? Get(string id)
    {
        try
        {
            var m = MetaPath(id);
            if (!File.Exists(m) || !File.Exists(PdfPath(id))) return null;
            return JsonFile.Load<DocInfo?>(m, () => null);
        }
        catch { return null; }
    }

    /// <summary>The document as the user sees it: one entry per label (auto-cropped boxes, or whole pages).</summary>
    public static DocInfo View(DocInfo d, bool autoCrop)
    {
        var v = JsonFile.Clone(d);
        if (d.Manual.Count > 0)
        {
            v.Labels = d.Manual; v.ManualCrop = true; v.AutoCropped = false; v.PageCount = v.Labels.Count; v.Detected = new();
            return v;
        }
        bool crop = autoCrop && d.Detected.Any(r => r.IsCrop || r.Fit);
        v.Labels = crop ? d.Detected : Enumerable.Range(0, d.PdfPageCount == 0 ? d.PageSizesIn.Count : d.PdfPageCount)
            .Select(p => new LabelRegion { Page = p, W = d.PageSizesIn[p][0], H = d.PageSizesIn[p][1], Note = "whole page" }).ToList();
        v.AutoCropped = crop;
        v.PageCount = v.Labels.Count;
        v.Detected = new();
        return v;
    }

    /// <summary>Set (replace=true), add, or clear (region=null) a hand-drawn crop area.</summary>
    public DocInfo SetManual(string id, LabelRegion? region, bool replace)
    {
        lock (_l)
        {
            var d = Get(id) ?? throw new PdfException("That PDF is no longer stored. Upload it again.");
            if (region == null) d.Manual.Clear();
            else
            {
                if (region.Page < 0 || region.Page >= d.PageSizesIn.Count) throw new PdfException("Page out of range.");
                var pw = d.PageSizesIn[region.Page][0]; var ph = d.PageSizesIn[region.Page][1];
                region.X = Math.Clamp(region.X, 0, pw); region.Y = Math.Clamp(region.Y, 0, ph);
                region.W = Math.Clamp(region.W, 0, pw - region.X); region.H = Math.Clamp(region.H, 0, ph - region.Y);
                if (region.W < 0.3 || region.H < 0.3) throw new PdfException("That crop box is too small (minimum 0.3 in).");
                region.IsCrop = true; region.Fit = true; region.Note = $"your crop {region.W:0.##}×{region.H:0.##} in from page {region.Page + 1}";
                if (replace) d.Manual.Clear();
                if (d.Manual.Count >= MaxPages) throw new PdfException("Too many crop areas.");
                d.Manual.Add(region);
            }
            JsonFile.Save(MetaPath(id), d);
            return d;
        }
    }

    public PdfDocument Open(string id)
    {
        if (Get(id) == null) throw new PdfException("The PDF is no longer stored (it was deleted after the retention period). Upload it again.");
        return PdfDocument.Open(PdfPath(id));
    }

    /// <summary>Delete documents older than retention that are not needed by pending jobs.</summary>
    public int Purge(TimeSpan retention, ISet<string> inUse)
    {
        int n = 0;
        lock (_l)
        {
            foreach (var f in Directory.GetFiles(DataPaths.Uploads))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (inUse.Contains(id)) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > retention) { try { File.Delete(f); n++; } catch { } }
            }
        }
        if (n > 0) Log.Info($"Purged {n} stored upload file(s) older than {retention.TotalHours:0.#} h");
        return n;
    }
}

using LabelPrinter.Core.Pdf;

namespace LabelPrinter.Core.Imaging;

/// <summary>
/// "Will it scan?" check. Barcodes are decoded from a high-resolution (300 dpi) render of the PDF (what SHOULD be there) and
/// from the exact 1-bit bitmap that goes to the printer (what WILL be there). Anything readable in the PDF but not in the
/// printed bitmap is reported before a label is wasted. The decoder (ZXing.Net) is plugged in by the app at startup.
/// </summary>
public static class BarcodeCheck
{
    /// <summary>Decoder hook: gray8 pixels, width, height -> decoded barcode texts (format:text).</summary>
    public static Func<byte[], int, int, List<string>>? Decoder { get; set; }
    public static bool Available => Decoder != null;

    public class Result
    {
        public bool Available { get; set; }
        public List<string> Source { get; set; } = new();
        public List<string> Printed { get; set; } = new();
        public List<string> Missing { get; set; } = new();
        public string Summary { get; set; } = "";
    }

    public static List<string> DecodeSource(PdfDocument doc, LabelRegion r)
    {
        if (Decoder == null) return new();
        const int dpi = 300;
        int w = Math.Max(1, (int)Math.Round(r.W * dpi)), h = Math.Max(1, (int)Math.Round(r.H * dpi));
        double k = (double)dpi;
        if ((long)w * h > 16_000_000) { k = Math.Sqrt(16_000_000.0 / (r.W * r.H)); w = (int)(r.W * k); h = (int)(r.H * k); }
        var (pw, ph) = doc.GetPageSizeInches(r.Page);
        var g = doc.RenderGrayRegion(r.Page, w, h, (int)Math.Round(pw * k), (int)Math.Round(ph * k), (int)Math.Round(r.X * k), (int)Math.Round(r.Y * k));
        return SafeDecode(g, w, h);
    }

    public static List<string> DecodeRaster(LabelRaster ras)
    {
        if (Decoder == null) return new();
        var g = new byte[ras.Width * ras.Height];
        for (int y = 0; y < ras.Height; y++) for (int x = 0; x < ras.Width; x++) g[y * ras.Width + x] = ras.Get(x, y) ? (byte)0 : (byte)255;
        return SafeDecode(g, ras.Width, ras.Height);
    }

    static List<string> SafeDecode(byte[] g, int w, int h)
    {
        try { return Decoder!(g, w, h).Distinct().ToList(); }
        catch (Exception ex) { Log.Warn("Barcode decode failed: " + ex.Message); return new(); }
    }

    public static Result Check(PdfDocument doc, LabelRegion r, LabelRaster printed)
    {
        var res = new Result { Available = Available };
        if (!Available) { res.Summary = "Barcode check not available in this build."; return res; }
        res.Source = DecodeSource(doc, r);
        res.Printed = DecodeRaster(printed);
        res.Missing = res.Source.Where(s => !res.Printed.Contains(s)).ToList();
        res.Summary = res.Source.Count == 0 && res.Printed.Count == 0 ? "No barcodes found on this label."
            : res.Missing.Count == 0 ? $"{res.Printed.Count} barcode{(res.Printed.Count == 1 ? "" : "s")} verified: they read correctly from the printed dots."
            : $"{res.Missing.Count} barcode{(res.Missing.Count == 1 ? "" : "s")} in the PDF did NOT read from the printed dots. Check Darkness/threshold/scale before printing.";
        return res;
    }
}

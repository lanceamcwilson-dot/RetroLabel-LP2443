using LabelPrinter.Core.Pdf;

namespace LabelPrinter.Core.Imaging;

/// <summary>A label to print: a whole PDF page, or a rectangle cut out of one (inches from the page's top-left).</summary>
public class LabelRegion
{
    public int Page { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public bool IsCrop { get; set; }
    /// <summary>Scale this region to fill the label (crops and whole pages that are not label-sized).</summary>
    public bool Fit { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// Finds the label(s) on "full page" PDFs such as Amazon / UPS / USPS return printouts, where the label sits inside a dashed
/// or solid cut-line box among instructions. Works on a 100 dpi raster of the page:
///  1. find thin horizontal and vertical lines (dashes are bridged, text and barcodes are rejected because they are thick),
///  2. pair them into rectangles whose corners meet,
///  3. keep the outermost rectangles that are big enough to be a label.
/// </summary>
public static class LabelDetector
{
    const int Dpi = 100;

    public static List<LabelRegion> Detect(PdfDocument doc, double labelW = 4, double labelH = 6)
    {
        var res = new List<LabelRegion>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            var (pw, ph) = doc.GetPageSizeInches(p);
            bool isLabelSized = (Near(pw, labelW) && Near(ph, labelH)) || (Near(pw, labelH) && Near(ph, labelW));
            if (isLabelSized) { res.Add(new LabelRegion { Page = p, W = pw, H = ph, Note = "whole page" }); continue; }
            res.AddRange(FindBoxes(doc, p, pw, ph));
        }
        // Nothing recognisable: fall back to every whole page (the user can still print them, fit-to-label).
        if (res.Count == 0)
            for (int p = 0; p < doc.PageCount; p++) { var (pw, ph) = doc.GetPageSizeInches(p); res.Add(new LabelRegion { Page = p, W = pw, H = ph, Fit = true, Note = "whole page (no label box found)" }); }
        return res;
    }

    static bool Near(double a, double b) => Math.Abs(a - b) < 0.15;

    record Seg(int Pos, int A, int B);   // line at Pos (row or column), spanning A..B

    public static List<LabelRegion> FindBoxes(PdfDocument doc, int page, double pw, double ph)
    {
        int w = (int)Math.Round(pw * Dpi), h = (int)Math.Round(ph * Dpi);
        var g = doc.RenderGray(page, w, h);
        var dark = new bool[w * h];
        for (int i = 0; i < dark.Length; i++) dark[i] = g[i] < 200;   // cut lines are often light grey

        var hs = Lines(dark, w, h, horizontal: true);
        var vs = Lines(dark, w, h, horizontal: false);
        int tol = (int)(0.12 * Dpi), minSide = (int)(2.0 * Dpi);
        var rects = new List<(int x0, int y0, int x1, int y1)>();
        foreach (var top in hs)
            foreach (var bot in hs)
            {
                if (bot.Pos - top.Pos < minSide) continue;
                if (Math.Abs(top.A - bot.A) > tol || Math.Abs(top.B - bot.B) > tol) continue;
                int x0 = (top.A + bot.A) / 2, x1 = (top.B + bot.B) / 2;
                if (x1 - x0 < minSide) continue;
                bool left = vs.Any(v => Math.Abs(v.Pos - x0) <= tol && v.A <= top.Pos + tol && v.B >= bot.Pos - tol);
                bool right = vs.Any(v => Math.Abs(v.Pos - x1) <= tol && v.A <= top.Pos + tol && v.B >= bot.Pos - tol);
                if (left && right) rects.Add((x0, top.Pos, x1, bot.Pos));
            }
        // Boxes split by a page break (common when a website prints a label across two pages): two matching verticals
        // that run into the top (or bottom) page margin, closed by one horizontal at the other end.
        int edge = (int)(1.0 * Dpi);
        foreach (var l in vs) foreach (var r in vs)
        {
            if (r.Pos - l.Pos < minSide || Math.Abs(l.A - r.A) > tol || Math.Abs(l.B - r.B) > tol || l.B - l.A < minSide) continue;
            int a = Math.Max(l.A, r.A), b = Math.Min(l.B, r.B);
            bool HAt(int y) => hs.Any(hh => Math.Abs(hh.Pos - y) <= tol && Math.Abs(hh.A - l.Pos) <= tol && Math.Abs(hh.B - r.Pos) <= tol);
            if (a <= edge && HAt(b) && !HAt(a)) rects.Add((l.Pos, a, r.Pos, b));
            else if (b >= h - edge && HAt(a) && !HAt(b)) rects.Add((l.Pos, a, r.Pos, b));
        }
        // A frame covering most of the page means the whole page IS the label (e.g. an eBay/UPS label scaled up to letter
        // size): its inner boxes are label sections, not labels. Use the inked area of the page instead.
        if (rects.Any(r => (r.x1 - r.x0) * (r.y1 - r.y0) >= 0.5 * w * h)) return new() { InkBox(dark, w, h, page, "label fills the page") };
        rects = rects.Distinct().ToList();
        var outer = rects.Where(r => !rects.Any(o => o != r && o.x0 <= r.x0 + 2 && o.y0 <= r.y0 + 2 && o.x1 >= r.x1 - 2 && o.y1 >= r.y1 - 2 && (o.x1 - o.x0) * (o.y1 - o.y0) > (r.x1 - r.x0) * (r.y1 - r.y0))).OrderBy(r => r.y0).ThenBy(r => r.x0).ToList();

        var list = new List<LabelRegion>();
        if (outer.Count == 0) return new() { InkBox(dark, w, h, page, "no cut-line box found") };
        // Safety net: the boxes must hold most of the page's ink. If not, they are probably sections of a bigger label.
        long total = 0, inside = 0;
        for (int y = 0; y < h; y += 2) for (int x = 0; x < w; x += 2)
        {
            if (!dark[y * w + x]) continue;
            total++;
            if (outer.Any(r => x >= r.x0 && x <= r.x1 && y >= r.y0 && y <= r.y1)) inside++;
        }
        if (total > 0 && inside < 0.45 * total) return new() { InkBox(dark, w, h, page, "boxes looked like parts of a larger label") };
        const double inset = 0.06;   // stay just inside the cut line so the dashes are not printed
        foreach (var r in outer)
        {
            double x = r.x0 / (double)Dpi + inset, y = r.y0 / (double)Dpi + inset;
            double rw = (r.x1 - r.x0) / (double)Dpi - 2 * inset, rh = (r.y1 - r.y0) / (double)Dpi - 2 * inset;
            if (InkFraction(dark, w, r) < 0.004) continue;   // empty box
            list.Add(new LabelRegion { Page = page, X = x, Y = y, W = rw, H = rh, IsCrop = true, Fit = true, Note = $"auto-cropped {rw:0.##}×{rh:0.##} in box from page {page + 1}" });
        }
        return list;
    }

    /// <summary>Whole page trimmed to its printed content (+ small margin), to be scaled to fill the label.</summary>
    static LabelRegion InkBox(bool[] d, int w, int h, int page, string why)
    {
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            if (d[y * w + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        if (x1 < 0) return new LabelRegion { Page = page, W = w / (double)Dpi, H = h / (double)Dpi, Fit = true, Note = "blank page" };
        double pad = 0.04;
        double X = Math.Max(0, x0 / (double)Dpi - pad), Y = Math.Max(0, y0 / (double)Dpi - pad);
        double X1 = Math.Min(w / (double)Dpi, (x1 + 1) / (double)Dpi + pad), Y1 = Math.Min(h / (double)Dpi, (y1 + 1) / (double)Dpi + pad);
        return new LabelRegion { Page = page, X = X, Y = Y, W = X1 - X, H = Y1 - Y, IsCrop = true, Fit = true, Note = $"whole page trimmed to its content ({why})" };
    }

    static double InkFraction(bool[] d, int w, (int x0, int y0, int x1, int y1) r)
    {
        long n = 0, t = 0;
        for (int y = r.y0 + 8; y < r.y1 - 8; y += 2) for (int x = r.x0 + 8; x < r.x1 - 8; x += 2) { t++; if (d[y * w + x]) n++; }
        return t == 0 ? 0 : n / (double)t;
    }

    /// <summary>Thin lines ≥ 1.5 in long. Gaps up to 0.1 in are bridged (dashed cut lines).</summary>
    static List<Seg> Lines(bool[] d, int w, int h, bool horizontal)
    {
        int len = horizontal ? w : h, across = horizontal ? h : w;
        bool D(int along, int at) => horizontal ? d[at * w + along] : d[along * w + at];
        int maxGap = (int)(0.10 * Dpi), minLen = (int)(1.5 * Dpi);
        var raw = new List<Seg>();
        for (int at = 3; at < across - 3; at++)
        {
            int start = -1, last = -1, ink = 0;
            for (int i = 0; i <= len; i++)
            {
                bool on = i < len && D(i, at);
                if (on)
                {
                    if (start < 0) { start = i; ink = 0; }
                    else if (i - last > maxGap) { Flush(); start = i; ink = 0; }
                    last = i; ink++;
                }
                else if (i == len && start >= 0) Flush();
            }
            void Flush()
            {
                int L = last - start + 1;
                if (L < minLen || ink < 0.35 * L) { start = -1; return; }
                // thin: the lines 3 px either side must be mostly empty along the same span
                int side = 0, samples = 0;
                for (int i = start; i <= last; i += 2) { samples++; if (D(i, at - 3)) side++; if (D(i, at + 3)) side++; }
                if (side <= 0.25 * samples) raw.Add(new Seg(at, start, last));
                start = -1;
            }
        }
        // merge parallel neighbours (lines 1-3 px thick)
        var merged = new List<Seg>();
        foreach (var s in raw.OrderBy(s => s.Pos))
        {
            var m = merged.FindLast(x => s.Pos - x.Pos <= 3 && Math.Abs(x.A - s.A) < 15 && Math.Abs(x.B - s.B) < 15);
            if (m == null) merged.Add(s);
        }
        return merged;
    }
}

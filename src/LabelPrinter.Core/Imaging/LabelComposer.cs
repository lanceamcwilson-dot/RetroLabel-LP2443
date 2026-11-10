using LabelPrinter.Core.Pdf;

namespace LabelPrinter.Core.Imaging;

/// <summary>
/// Turns one PDF page into the exact 1-bit bitmap that is printed (and previewed).
/// Pipeline: size -> choose rotation -> render vector page at (203 dpi x scale) -> quarter-turn (lossless)
/// -> place on label canvas with offsets (crop what falls outside) -> threshold to 1 bit.
/// No bitmap resampling is ever applied; scaling happens in the PDF renderer on vector data.
/// </summary>
public static class LabelComposer
{
    public static LabelRaster Compose(PdfDocument doc, int pageIndex, AppSettings s)
    {
        var (w, h) = doc.GetPageSizeInches(pageIndex);
        return Compose(doc, new LabelRegion { Page = pageIndex, W = w, H = h }, s);
    }

    public static LabelRaster Compose(PdfDocument doc, LabelRegion region, AppSettings s)
    {
        int pageIndex = region.Page;
        var (fullW, fullH) = doc.GetPageSizeInches(pageIndex);
        double pw = region.W, ph = region.H;
        int labelW = s.LabelWidthDots, labelH = s.LabelHeightDots;
        var warnings = new List<string>();

        // 1. rotation
        int rot = s.RotationDegrees;
        bool autoRotated = false;
        double effW = (rot % 180 == 0) ? pw : ph, effH = (rot % 180 == 0) ? ph : pw;
        if (s.AutoRotate)
        {
            bool labelPortrait = labelH >= labelW;
            bool pagePortrait = effH >= effW;
            if (labelPortrait != pagePortrait && Math.Max(effW, effH) / Math.Min(effW, effH) > 1.2)   // near-square stickers are left upright
            {
                rot = (rot + 90) % 360; autoRotated = true;
                (effW, effH) = (effH, effW);
            }
        }

        // 2. scale
        double scale = s.ScalePercent / 100.0;
        if ((region.IsCrop || region.Fit) && s.FitCroppedLabels)
        {
            scale = Math.Min(s.LabelWidthIn / effW, s.LabelHeightIn / effH);
            warnings.Add($"{(region.Note.StartsWith("whole page") ? "Whole page trimmed to its content" : "Label was cut out of a " + fullW.ToString("0.#") + "×" + fullH.ToString("0.#") + " in page")} and scaled {scale * 100:0.#}% to fill the label (rendered from vector data). Scan-check the first one.");
        }
        else if (s.ScaleMode == ScaleMode.FitToLabel)
        {
            scale = Math.Min(s.LabelWidthIn / effW, s.LabelHeightIn / effH);
            warnings.Add($"Fit-to-label is ON: page scaled to {scale * 100:0.#}%. Barcodes are re-rendered from vector data, but verify they scan.");
        }
        else if (Math.Abs(scale - 1.0) > 1e-9)
            warnings.Add($"Scale is {s.ScalePercent:0.#}% (not 100%). Verify barcodes scan.");

        // 3. render (unrotated page, at final pixel size)
        int rw = Math.Max(1, (int)Math.Round(pw * s.Dpi * scale));
        int rh = Math.Max(1, (int)Math.Round(ph * s.Dpi * scale));
        var gray = RenderSupersampled(doc, region, fullW, rw, rh, Math.Clamp(s.Supersample, 1, 4));

        // 4. lossless quarter turns (clockwise)
        int turns = rot / 90 % 4;
        int cw = rw, ch = rh;
        for (int i = 0; i < turns; i++) { gray = Rotate90(gray, cw, ch); (cw, ch) = (ch, cw); }

        // 5. size sanity
        double effWin = cw / (double)s.Dpi / scale, effHin = ch / (double)s.Dpi / scale;
        if (!region.IsCrop && s.ScaleMode == ScaleMode.ActualSize && (Math.Abs(cw - labelW) > 3 || Math.Abs(ch - labelH) > 3))
            warnings.Add($"PDF page is {effWin:0.##} x {effHin:0.##} in; label is {s.LabelWidthIn:0.##} x {s.LabelHeightIn:0.##} in. Not resized: content is placed at actual size.");

        // 6. place + threshold
        int ox = s.OffsetXDots, oy = s.OffsetYDots;
        if (s.Anchor == Anchor.Center || region.IsCrop || region.Fit) { ox += (labelW - cw) / 2; oy += (labelH - ch) / 2; }
        var raster = new LabelRaster(labelW, labelH);
        int thr = s.Threshold;
        long lost = 0;
        for (int y = 0; y < ch; y++)
        {
            int dy = y + oy;
            for (int x = 0; x < cw; x++)
            {
                bool black = gray[y * cw + x] < thr;
                if (s.InvertImage) black = !black;
                if (!black) continue;
                int dx = x + ox;
                if (dx < 0 || dx >= labelW || dy < 0 || dy >= labelH) { lost++; continue; }
                raster.Set(dx, dy);
            }
        }
        // Inverted output: pixels outside the page area are "white" in source -> black after invert; fill canvas gaps
        if (s.InvertImage) FillOutsideBlack(raster, ox, oy, cw, ch);
        if (lost > 0) { raster.Cropped = true; warnings.Add($"{lost:N0} black dots fall outside the label and are CROPPED. Check offsets/scale/rotation."); }
        if (labelW > s.PrintableWidthDots) warnings.Add($"Label is wider ({labelW} dots) than the assumed printable width ({s.PrintableWidthDots} dots).");

        raster.Warnings.AddRange(warnings);
        raster.Summary = $"{(region.IsCrop ? "Auto-cropped from page " + (pageIndex + 1) : "Page " + (pageIndex + 1))}: {pw:0.##}x{ph:0.##} in, rotate {rot}°{(autoRotated ? " (auto)" : "")}, scale {scale * 100:0.#}%, offset {s.OffsetXDots},{s.OffsetYDots} dots, threshold {thr}";
        return raster;
    }

    /// <summary>
    /// PDFium snaps axis-aligned filled rectangles (barcode bars!) OUTWARD to whole pixels, which widens bars by up to a dot.
    /// Rendering at N x and box-averaging (area coverage) before the threshold puts every edge on the nearest dot instead.
    /// </summary>
    public static byte[] RenderSupersampled(PdfDocument doc, LabelRegion r, double pageWIn, int w, int h, int ss)
    {
        ss = Math.Max(1, ss);
        int W = w * ss, H = h * ss;
        double pxPerIn = W / r.W;
        var (_, pageHIn) = doc.GetPageSizeInches(r.Page);
        var big = doc.RenderGrayRegion(r.Page, W, H, (int)Math.Round(pageWIn * pxPerIn), (int)Math.Round(pageHIn * pxPerIn), (int)Math.Round(r.X * pxPerIn), (int)Math.Round(r.Y * pxPerIn));
        if (ss == 1) return big;
        var res = new byte[w * h];
        int bw = w * ss, area = ss * ss;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < ss; dy++)
                {
                    int o = (y * ss + dy) * bw + x * ss;
                    for (int dx = 0; dx < ss; dx++) sum += big[o + dx];
                }
                res[y * w + x] = (byte)((sum + area / 2) / area);
            }
        return res;
    }

    static void FillOutsideBlack(LabelRaster r, int ox, int oy, int cw, int ch)
    {
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
                if (x < ox || x >= ox + cw || y < oy || y >= oy + ch) r.Set(x, y);
    }

    /// <summary>Rotate 90° clockwise, exact pixel mapping.</summary>
    public static byte[] Rotate90(byte[] src, int w, int h)
    {
        var dst = new byte[src.Length];
        // new size: h x w ; new(x', y') = old(x = y', y = h-1-x')
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dst[x * h + (h - 1 - y)] = src[y * w + x];
        return dst;
    }
}

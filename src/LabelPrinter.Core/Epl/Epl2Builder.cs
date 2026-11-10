using System.Text;
using LabelPrinter.Core.Imaging;

namespace LabelPrinter.Core.Epl;

/// <summary>
/// Generates EPL2 (Eltron Programming Language 2) job data. Per third-party driver compatibility tables the Eltron/Zebra
/// LP 2443 uses EPL2; this has NOT been confirmed on the physical printer. Commands used: N q Q D S ZT/ZB R GW P A B LO X.
/// </summary>
public static class Epl2Builder
{
    static readonly Encoding Ascii = Encoding.ASCII;

    static void Line(Stream s, string t) { var b = Ascii.GetBytes(t + "\n"); s.Write(b, 0, b.Length); }

    static void Header(Stream s, AppSettings st)
    {
        if (st.AutoHealLineMode) { var b = BuildExitLineMode(); s.Write(b, 0, b.Length); }   // self-heal if the printer slipped into Line Mode
        Line(s, "");                       // flush any partial command left in the printer
        Line(s, "N");                      // clear image buffer
        Line(s, $"q{st.LabelWidthDots}");  // label width in dots
        string gap = st.Media switch
        {
            MediaKind.Continuous => "0",
            MediaKind.BlackMark => "B" + st.GapDots,
            _ => st.GapDots.ToString(),
        };
        Line(s, $"Q{st.LabelHeightDots},{gap}");
        Line(s, $"D{st.Density}");
        Line(s, $"S{st.Speed}");
        Line(s, st.PrintDirectionRotated ? "ZB" : "ZT");
        Line(s, "R0,0");
    }

    /// <summary>Full-page raster job. The image is sent in horizontal strips (GW) so no single command is huge.</summary>
    public static byte[] BuildRasterJob(LabelRaster r, AppSettings st, int copies)
    {
        copies = Math.Clamp(copies, 1, 999);
        using var ms = new MemoryStream();
        Header(ms, st);
        const int StripRows = 128;
        for (int y0 = 0; y0 < r.Height; y0 += StripRows)
        {
            int rows = Math.Min(StripRows, r.Height - y0);
            // Trim all-white strips (nothing to burn, saves link time on a slow parallel adapter)
            bool any = false;
            for (int i = y0 * r.Stride; i < (y0 + rows) * r.Stride && !any; i++) any = r.Bits[i] != 0;
            if (!any) continue;
            Line2(ms, $"GW0,{y0},{r.Stride},{rows},");
            var strip = new byte[rows * r.Stride];
            Buffer.BlockCopy(r.Bits, y0 * r.Stride, strip, 0, strip.Length);
            // padding bits in the last byte of each row must be WHITE
            int pad = r.Stride * 8 - r.Width;
            for (int row = 0; row < rows; row++)
            {
                if (pad > 0) strip[row * r.Stride + r.Stride - 1] &= (byte)~((1 << pad) - 1);
                if (st.GwZeroIsBlack)
                    for (int i = 0; i < r.Stride; i++) strip[row * r.Stride + i] = (byte)~strip[row * r.Stride + i];
            }
            ms.Write(strip, 0, strip.Length);
            Line(ms, "");
        }
        Line(ms, $"P{copies}");
        return ms.ToArray();
    }
    static void Line2(Stream s, string t) { var b = Ascii.GetBytes(t); s.Write(b, 0, b.Length); }

    /// <summary>
    /// Leave EPL "Line Mode" (EPL1 emulation: the printer prints incoming bytes as plain text) and return to EPL2 page mode.
    /// Zebra's Line Mode Programmer's Manual (980353-001, covers the 2443/2844 family): line-mode commands start with ESC,
    /// and "EPL2" switches to Page Mode; it must be alone on its line. (ESC OEPL2 is the newer-printer form and did NOT work
    /// on a real LP 2443.) The mode is stored in the printer.
    /// </summary>
    public static byte[] BuildExitLineMode() => Ascii.GetBytes("\r\n\u001bEPL2\r\n");

    /// <summary>Tiny sanity job: one line of text, no graphics. Use it to prove the data path (queue -> adapter -> printer) works.</summary>
    /// <summary>EPL2 page-mode command that puts the printer INTO Line Mode (used only by the protection test).</summary>
    public static byte[] BuildEnterLineMode() => Ascii.GetBytes("\nOEPL1\n");

    public static byte[] BuildHelloJob(AppSettings st, string title = "RAW EPL2 PATH OK")
    {
        using var ms = new MemoryStream();
        Header(ms, st);
        Line(ms, $"A40,40,0,3,1,1,N,\"{title}\"");
        Line(ms, "A40,90,0,2,1,1,N,\"If you can read this the queue,\"");
        Line(ms, "A40,120,0,2,1,1,N,\"adapter and printer are talking.\"");
        Line(ms, "P1");
        return ms.ToArray();
    }

    /// <summary>
    /// Calibration / diagnostic label: frame at the label edge, rulers every 50 dots, native text, a Code 128 barcode (native),
    /// and a GW block whose colour proves the bit polarity setting.
    /// </summary>
    public static byte[] BuildTestLabel(AppSettings st)
    {
        int w = st.LabelWidthDots, h = st.LabelHeightDots;
        using var ms = new MemoryStream();
        Header(ms, st);
        Line(ms, $"X0,0,2,{w - 1},{h - 1}");                                // frame at label edge
        for (int x = 50; x < w; x += 50) Line(ms, $"LO{x},0,1,{(x % 100 == 0 ? 30 : 15)}");  // top ruler
        for (int y = 50; y < h; y += 50) Line(ms, $"LO0,{y},{(y % 100 == 0 ? 30 : 15)},1");
        Line(ms, $"A60,60,0,4,1,1,N,\"LP 2443 TEST LABEL\"");
        Line(ms, $"A60,110,0,2,1,1,N,\"Label {w}x{h} dots ({st.LabelWidthIn:0.##}x{st.LabelHeightIn:0.##} in) @ {st.Dpi} dpi\"");
        Line(ms, $"A60,135,0,2,1,1,N,\"Density D{st.Density}  Speed S{st.Speed}  Offset {st.OffsetXDots},{st.OffsetYDots}\"");
        Line(ms, $"A60,160,0,2,1,1,N,\"Ruler ticks: 50 dots (6.3 mm); long tick = 100 dots\"");
        Line(ms, "B60,210,0,1,3,7,120,B,\"ZLP-TEST-12345\"");
        Line(ms, "A60,370,0,2,1,1,N,\"Native Code 128 above. It must scan.\"");

        // GW polarity block 96x96 dots (12 bytes x 96 rows)
        Line(ms, "A60,430,0,2,1,1,N,\"GW graphic test (96x96 block):\"");
        Line2(ms, "GW60,460,12,96,");
        var blk = new byte[12 * 96];
        Array.Fill(blk, st.GwZeroIsBlack ? (byte)0x00 : (byte)0xFF);
        ms.Write(blk, 0, blk.Length);
        Line(ms, "");
        Line(ms, "A180,490,0,2,1,1,N,\"<- must be a SOLID BLACK square.\"");
        Line(ms, "A180,515,0,2,1,1,N,\"If it is white/blank: open Settings,\"");
        Line(ms, "A180,540,0,2,1,1,N,\"toggle 'GW 0-bits are black'.\"");

        // fine line pattern (1-dot lines) to judge head/darkness
        Line(ms, "A60,600,0,2,1,1,N,\"1-dot / 2-dot / 3-dot bars:\"");
        for (int i = 0; i < 12; i++) Line(ms, $"LO{60 + i * 12},630,{1 + i / 4},60");
        Line(ms, $"A60,{h - 90},0,2,1,1,N,\"Frame touching label edges = margins OK\"");
        Line(ms, "P1");
        return ms.ToArray();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LabelPrinter.Core;
using LabelPrinter.Core.Epl;
using LabelPrinter.Core.Imaging;
using LabelPrinter.Core.Jobs;
using LabelPrinter.Core.Pdf;
using LabelPrinter.Core.Printing;
using LabelPrinter.Core.Remote;
using LabelPrinter.Core.Web;
using AppContext = LabelPrinter.Core.Web.AppContext;

// Self-contained test runner (no NuGet packages needed). Exit code 0 = all passed.
static class T
{
    public static int Pass, Fail;
    public static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS  {name}"); }
        else { Fail++; Console.WriteLine($"  FAIL  {name} {detail}"); }
    }
    public static string Data(string f)
    {
        var d = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 8; i++) { var p = Path.Combine(d, "tests", "data", f); if (File.Exists(p)) return p; var q = Path.Combine(d, "data", f); if (File.Exists(q)) return q; d = Path.GetFullPath(Path.Combine(d, "..")); }
        throw new FileNotFoundException(f);
    }
}

class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--detect")
        {
            using var dd = PdfDocument.Open(args[1]);
            var st = new AppSettings();
            int n = 0;
            foreach (var r in LabelDetector.Detect(dd))
            {
                Console.WriteLine($"label {n}: page {r.Page} x={r.X:0.00} y={r.Y:0.00} w={r.W:0.00} h={r.H:0.00} crop={r.IsCrop} {r.Note}");
                var ras = LabelComposer.Compose(dd, r, st);
                Console.WriteLine("   " + ras.Summary + " | " + string.Join(" | ", ras.Warnings));
                File.WriteAllBytes($"{args[1]}.label{n++}.png", PngWriter.Encode(ras));
            }
            return 0;
        }
        var tmp = Path.Combine(Path.GetTempPath(), "zlp-selftest-" + Guid.NewGuid().ToString("N")[..6]);
        DataPaths.Root = tmp;
        Console.WriteLine($"Data dir: {tmp}");

#if HAS_ZXING
        BarcodeCheck.Decoder = LabelPrinter.Shared.ZxingDecoder.Decode;
        Console.WriteLine("Barcode decoder: ZXing.Net");
#else
        if (PyDecoderWorks()) { BarcodeCheck.Decoder = PyDecode; Console.WriteLine("Barcode decoder: zxing-cpp via Python (test only)"); }
        else Console.WriteLine("Barcode decoder: none (barcode checks skipped)");
#endif
        Console.WriteLine("\n[1] PDF rendering + composition");
        Compose();
        Console.WriteLine("\n[2] EPL2 generation");
        Epl();
        Console.WriteLine("\n[2b] Barcode check");
        Barcodes();
        Console.WriteLine("\n[3] PNG preview encoder");
        Png();
        Console.WriteLine("\n[4] Settings sanitising");
        Settings();
        T.Check("update check: version compare", LabelPrinter.Core.Diagnostics.UpdateCheck.IsNewer("v2.1.0-build4", "2.0.0") && !LabelPrinter.Core.Diagnostics.UpdateCheck.IsNewer("v2.0.0-build9", "2.0.0") && !LabelPrinter.Core.Diagnostics.UpdateCheck.IsNewer("v1.9.0", "2.0.0"));
        Console.WriteLine("\n[5] Pairing / device registry");
        Devices();
        Console.WriteLine("\n[6] Job queue (file backend)");
        await Jobs();
        Console.WriteLine("\n[7] HTTP: desktop UI server + phone server");
        await Http();

        Console.WriteLine($"\n{T.Pass} passed, {T.Fail} failed");
        try { Directory.Delete(tmp, true); } catch { }
        return T.Fail == 0 ? 0 : 1;
    }

    static void Compose()
    {
        var s = new AppSettings();
        using var d = PdfDocument.Open(T.Data("label_4x6.pdf"));
        var (w, h) = d.GetPageSizeInches(0);
        T.Check("4x6 page reports 4.00 x 6.00 in", Math.Abs(w - 4) < .01 && Math.Abs(h - 6) < .01, $"{w}x{h}");
        var r = LabelComposer.Compose(d, 0, s);
        T.Check("raster is 812 x 1218 dots", r.Width == 812 && r.Height == 1218, $"{r.Width}x{r.Height}");
        T.Check("raster has ink but is not mostly black", r.BlackCount() > 5000 && r.BlackCount() < r.Width * r.Height / 3, r.BlackCount().ToString());
        T.Check("100% default produces no warnings for a true 4x6 PDF", r.Warnings.Count == 0, string.Join("|", r.Warnings));
        T.Check("not cropped", !r.Cropped);

        // exact-grid bars must keep their exact widths (no scaling/interpolation artefacts)
        using var bars = PdfDocument.Open(T.Data("bars_exact.pdf"));
        var rb = LabelComposer.Compose(bars, 0, s);
        var runs = BlackRuns(rb, 250);
        var expect = new[] { (40, 1), (44, 2), (48, 4), (56, 8), (72, 3) };
        T.Check("bar widths exact at 203 dpi (1,2,4,8,3 dots)", runs.SequenceEqual(expect), "got " + string.Join(",", runs.Select(x => $"{x.start}+{x.len}")));
        T.Check("bar edges identical on every row of the bar block", Enumerable.Range(200, 100).All(y => BlackRuns(rb, y).SequenceEqual(expect)));

        // landscape page + auto rotate
        using var ls = PdfDocument.Open(T.Data("label_6x4_landscape.pdf"));
        var ra = LabelComposer.Compose(ls, 0, s);
        T.Check("landscape PDF auto-rotated to fit portrait label with no crop", !ra.Cropped && ra.Summary.Contains("rotate 90") && ra.Summary.Contains("(auto)"), ra.Summary);
        var off = s.Clone(); off.AutoRotate = false;
        var rn = LabelComposer.Compose(ls, 0, off);
        T.Check("without auto-rotate the landscape page is cropped and warned", rn.Cropped && rn.Warnings.Any(x => x.Contains("CROPPED")));

        // manual rotation 180
        var r180 = s.Clone(); r180.RotationDegrees = 180;
        var rr = LabelComposer.Compose(d, 0, r180);
        bool mirrored = true; for (int y = 0; y < 50 && mirrored; y++) for (int x = 0; x < 812 && mirrored; x++) mirrored = rr.Get(x, y) == r.Get(811 - x, 1217 - y);
        T.Check("rotation 180 is an exact pixel rotation", mirrored);

        // offsets
        var o = s.Clone(); o.OffsetXDots = 16; o.OffsetYDots = 8;
        var ro = LabelComposer.Compose(bars, 0, o);
        var runsO = BlackRuns(ro, 258);
        T.Check("X offset +16 shifts bars by exactly 16 dots", runsO.SequenceEqual(expect.Select(e => (e.Item1 + 16, e.Item2))));
        var oy = ro; bool yshift = ro.Get(40 + 16, 208) && !ro.Get(40 + 16, 207);
        T.Check("Y offset +8 shifts bar top edge by exactly 8 dots", yshift);
        var big = s.Clone(); big.OffsetXDots = 400;
        T.Check("large offset flags cropping", LabelComposer.Compose(d, 0, big).Cropped);

        // scale
        var sc = s.Clone(); sc.ScalePercent = 50;
        var r50 = LabelComposer.Compose(d, 0, sc);
        T.Check("50% scale warns that barcodes should be verified", r50.Warnings.Any(x => x.Contains("Scale is 50")));
        T.Check("50% scale shrinks ink coverage", r50.BlackCount() < r.BlackCount() * 0.4);

        // fit
        var fit = s.Clone(); fit.ScaleMode = ScaleMode.FitToLabel;
        var rf = LabelComposer.Compose(ls, 0, fit);
        T.Check("fit-to-label never crops", !rf.Cropped && rf.Warnings.Any(x => x.Contains("Fit-to-label")));

        // threshold + invert
        var inv = s.Clone(); inv.InvertImage = true;
        var ri = LabelComposer.Compose(d, 0, inv);
        T.Check("invert flips ink", ri.BlackCount() > r.Width * r.Height / 2);

        // errors
        try { PdfDocument.Open(Encoding.ASCII.GetBytes("hello not a pdf")); T.Check("non-PDF rejected", false); }
        catch (PdfException e) { T.Check("non-PDF rejected with friendly text", e.Message.Contains("not a PDF")); }
        try { PdfDocument.Open(Encoding.ASCII.GetBytes("%PDF-1.4\ngarbage that is not a pdf at all")); T.Check("damaged PDF rejected", false); }
        catch (PdfException e) { T.Check("damaged PDF rejected with friendly text", e.Message.Length > 10, e.Message); }
        using (var fp = PdfDocument.Open(T.Data("fullpage_return.pdf")))
        {
            var regs = LabelDetector.Detect(fp);
            T.Check("auto-crop finds the dashed label box and the box split across the page break", regs.Count == 2 && regs.All(x => x.IsCrop) && regs[0].Page == 0 && regs[1].Page == 1, string.Join("; ", regs.Select(x => $"p{x.Page} {x.X:0.0},{x.Y:0.0} {x.W:0.0}x{x.H:0.0}")));
            T.Check("crop box is ~5.3 x 3.4 in (inside the cut line)", regs.Count > 0 && Math.Abs(regs[0].W - 5.28) < 0.1 && Math.Abs(regs[0].H - 3.38) < 0.1);
            var cr = LabelComposer.Compose(fp, regs[0], s);
            T.Check("cropped label is rotated and scaled to fill 4x6, nothing cropped", cr.Width == 812 && !cr.Cropped && cr.Summary.Contains("rotate 90"));
            File.WriteAllBytes(Path.Combine(DataPaths.Root, "crop0.png"), PngWriter.Encode(cr));
            var view = DocumentStore.View(new DocInfo { PdfPageCount = 2, PageSizesIn = new() { new[] { 8.5, 11.0 }, new[] { 8.5, 11.0 } }, Detected = regs }, false);
            T.Check("auto-crop off -> whole pages", view.PageCount == 2 && view.Labels.All(l => !l.IsCrop));
        }
        using (var pf = PdfDocument.Open(T.Data("page_filling_label.pdf")))
        {
            var regs = LabelDetector.Detect(pf);
            T.Check("label scaled up to fill a letter page -> whole page (trimmed), not an inner section", regs.Count == 1 && regs[0].Fit && regs[0].W > 7 && regs[0].H > 10, string.Join("; ", regs.Select(x => $"{x.W:0.0}x{x.H:0.0} {x.Note}")));
            var rpf = LabelComposer.Compose(pf, regs[0], s);
            T.Check("page-filling label is scaled down to fit 4x6 with nothing cut off", !rpf.Cropped && rpf.Summary.Contains("scale"));
        }
        T.Check("multi-page PDF has 3 pages", PdfDocument.Open(T.Data("multi_3page.pdf")).PageCount == 3);
    }

    static List<string> PyDecode(byte[] g, int w, int h)
    {
        var f = Path.Combine(Path.GetTempPath(), $"zlp-{Guid.NewGuid():N}.pgm");
        using (var fs = File.Create(f)) { var hdr = Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n"); fs.Write(hdr); fs.Write(g); }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("python3", new[] { T.Data("../py_decode.py"), f }) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
            return o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        finally { File.Delete(f); }
    }
    static bool PyDecoderWorks() { try { T.Data("../py_decode.py"); return PyDecode(new byte[100], 10, 10) != null; } catch { return false; } }

    static void Barcodes()
    {
        if (!BarcodeCheck.Available) { Console.WriteLine("  (skipped: no decoder)"); return; }
        var s = new AppSettings();
        using var d = PdfDocument.Open(T.Data("label_4x6.pdf"));
        var reg = new LabelRegion { Page = 0, W = 4, H = 6 };
        var good = BarcodeCheck.Check(d, reg, LabelComposer.Compose(d, reg, s));
        T.Check("barcode check: Code 128 found in PDF and in the printed dots", good.Source.Any(x => x.EndsWith("9400111899223456789012")) && good.Missing.Count == 0, string.Join(",", good.Source) + " | " + string.Join(",", good.Printed));
        var bad = s.Clone(); bad.ScalePercent = 25;
        var badR = BarcodeCheck.Check(d, reg, LabelComposer.Compose(d, reg, bad));
        T.Check("barcode check: shrinking to 25% is caught (barcode would not scan)", badR.Missing.Count > 0, badR.Summary);
        using var fp = PdfDocument.Open(T.Data("fullpage_return.pdf"));
        var crop = LabelDetector.Detect(fp)[0];
        var cr = BarcodeCheck.Check(fp, crop, LabelComposer.Compose(fp, crop, s));
        T.Check("barcode check works on an auto-cropped, rotated label", cr.Source.Any(x => x.EndsWith("1ZTEST0000001")) && cr.Missing.Count == 0, cr.Summary);
    }

    static List<(int start, int len)> BlackRuns(LabelRaster r, int y)
    {
        var l = new List<(int, int)>(); int st = -1;
        for (int x = 0; x <= r.Width; x++)
        {
            bool b = x < r.Width && r.Get(x, y);
            if (b && st < 0) st = x; else if (!b && st >= 0) { l.Add((st, x - st)); st = -1; }
        }
        // ignore anything outside the bar block columns
        return l.Where(t => t.Item1 >= 30 && t.Item1 < 120).ToList();
    }

    static void Epl()
    {
        var s = new AppSettings { Density = 10, Speed = 3, GapDots = 24 };
        using var d = PdfDocument.Open(T.Data("label_4x6.pdf"));
        var r = LabelComposer.Compose(d, 0, s);
        foreach (var zeroBlack in new[] { true, false })
        {
            s.GwZeroIsBlack = zeroBlack;
            var job = Epl2Builder.BuildRasterJob(r, s, 3);
            var txt = Encoding.Latin1.GetString(job);
            T.Check($"[{zeroBlack}] header has N, q812, Q1218,24, D10, S3, ZT", txt.StartsWith("\nN\nq812\nQ1218,24\nD10\nS3\nZT\nR0,0\n"), txt[..Math.Min(60, txt.Length)].Replace("\n", "\\n"));
            T.Check($"[{zeroBlack}] ends with P3", txt.EndsWith("P3\n"));
            // decode GW strips back to a bitmap
            var back = new LabelRaster(r.Width, r.Height);
            int pos = 0, strips = 0; bool lengthsOk = true;
            while ((pos = txt.IndexOf("\nGW", pos, StringComparison.Ordinal)) >= 0)
            {
                pos += 1;
                int hdrEnd = 0, commas = 0; for (int i = pos; ; i++) { if (job[i] == ',') { commas++; if (commas == 4) { hdrEnd = i + 1; break; } } }
                var p = Encoding.ASCII.GetString(job, pos + 2, hdrEnd - pos - 3).Split(',').Select(int.Parse).ToArray();
                int x0 = p[0], y0 = p[1], bytesW = p[2], rows = p[3];
                lengthsOk &= bytesW == r.Stride && x0 == 0;
                for (int row = 0; row < rows; row++)
                    for (int bx = 0; bx < bytesW; bx++)
                    {
                        byte v = job[hdrEnd + row * bytesW + bx];
                        if (zeroBlack) v = (byte)~v;
                        for (int bit = 0; bit < 8; bit++) { int x = bx * 8 + bit; if (x < r.Width && (v & (0x80 >> bit)) != 0) back.Set(x, y0 + row); }
                    }
                pos = hdrEnd + rows * bytesW; strips++;
            }
            T.Check($"[{zeroBlack}] GW strips decode back to the exact raster ({strips} strips)", lengthsOk && strips > 3 && back.Bits.SequenceEqual(r.Bits));
        }
        T.Check("test label has Code128 + GW block + frame", Encoding.Latin1.GetString(Epl2Builder.BuildTestLabel(s)) is var tl && tl.Contains("B60,210,0,1,3,7,120,B,\"ZLP-TEST-12345\"") && tl.Contains("GW60,460,12,96,") && tl.Contains("X0,0,2,811,1217"));
        T.Check("exit-line-mode command is ESC EPL2 on its own line", Epl2Builder.BuildExitLineMode().SequenceEqual(Encoding.ASCII.GetBytes("\r\n\u001bEPL2\r\n")));
        var heal = new AppSettings { AutoHealLineMode = true };
        T.Check("auto-heal prefixes every job with ESC EPL2", Encoding.ASCII.GetString(Epl2Builder.BuildHelloJob(heal)).StartsWith("\r\n\u001bEPL2\r\n\nN\n") && Encoding.Latin1.GetString(Epl2Builder.BuildRasterJob(new LabelRaster(812, 1218), heal, 1)).StartsWith("\r\n\u001bEPL2"));
        T.Check("auto-heal off by default (no prefix)", Encoding.ASCII.GetString(Epl2Builder.BuildHelloJob(new AppSettings())).StartsWith("\nN\n"));
        T.Check("enter-line-mode test command is OEPL1", Encoding.ASCII.GetString(Epl2Builder.BuildEnterLineMode()) == "\nOEPL1\n");
        T.Check("hello job is plain text", Encoding.Latin1.GetString(Epl2Builder.BuildHelloJob(s)).Contains("RAW EPL2 PATH OK"));
        var cont = new AppSettings { Media = MediaKind.Continuous };
        T.Check("continuous media -> Q1218,0", Encoding.ASCII.GetString(Epl2Builder.BuildHelloJob(cont)).Contains("Q1218,0\n"));
        var bm = new AppSettings { Media = MediaKind.BlackMark, GapDots = 30 };
        T.Check("black-mark media -> Q1218,B30", Encoding.ASCII.GetString(Epl2Builder.BuildHelloJob(bm)).Contains("Q1218,B30\n"));
        var all = new LabelRaster(812, 1218);
        T.Check("blank label sends no GW data", !Encoding.Latin1.GetString(Epl2Builder.BuildRasterJob(all, new AppSettings(), 1)).Contains("GW"));
    }

    static void Png()
    {
        var r = new LabelRaster(13, 5); r.Set(0, 0); r.Set(12, 4);
        var png = PngWriter.Encode(r);
        File.WriteAllBytes(Path.Combine(DataPaths.Root, "t.png"), png);
        T.Check("PNG signature + IHDR 13x5 1-bit", png[1] == 'P' && png.Length > 40 && BitConverter.ToUInt32(png[16..20].Reverse().ToArray()) == 13 && BitConverter.ToUInt32(png[20..24].Reverse().ToArray()) == 5 && png[24] == 1);
    }

    static void Settings()
    {
        var s = new AppSettings { ScalePercent = 9999, Density = 99, Speed = -4, OffsetXDots = 99999, RotationDegrees = 450, Threshold = 0, RemotePort = 3, UploadRetentionHours = 0, LabelWidthIn = 99 };
        s.Sanitize();
        T.Check("settings are clamped", s.ScalePercent == 400 && s.Density == 15 && s.Speed == 1 && s.OffsetXDots == 400 && s.RotationDegrees == 90 && s.Threshold == 1 && s.RemotePort == 1024 && s.UploadRetentionHours == 1 && s.LabelWidthIn == 4.5, JsonSerializer.Serialize(s));
        var store = new SettingsStore(); store.Update(x => x.PrinterName = "Zebra LP 2443");
        T.Check("settings persist to disk", new SettingsStore().Get().PrinterName == "Zebra LP 2443");
    }

    static void Devices()
    {
        var reg = new DeviceRegistry();
        var (code, _) = reg.NewPairingCode();
        var (d0, _, e0) = reg.Pair("WRONGCODE1", "x", "10.0.0.9");
        T.Check("wrong code rejected", d0 == null && e0 != null);
        var (dev, tok, err) = reg.Pair(code, "My iPhone", "10.0.0.5");
        T.Check("correct code pairs and returns a token", dev != null && tok != null && tok.Length >= 40 && err == null);
        T.Check("code is single use", reg.Pair(code, "again", "10.0.0.5").dev == null);
        T.Check("token authenticates", reg.Authenticate(tok, "10.0.0.5")?.Id == dev!.Id);
        T.Check("bad token rejected", reg.Authenticate(tok + "x", "10.0.0.5") == null && reg.Authenticate("", "1") == null);
        T.Check("token is not stored in plaintext", !File.ReadAllText(DataPaths.DevicesFile).Contains(tok!));
        var (c2, _) = reg.NewPairingCode();
        for (int i = 0; i < 5; i++) reg.Pair("BAD", "x", "10.0.0.77");
        T.Check("5 bad attempts lock that IP out, even for the right code", reg.Pair(c2, "x", "10.0.0.77").dev == null);
        T.Check("revoke works", reg.Revoke(dev.Id) && reg.Authenticate(tok, "10.0.0.5") == null);
        var (c3, exp) = reg.NewPairingCode(); T.Check("pairing code is 10 chars, expires in ~10 min", c3.Length == 10 && (exp - DateTime.UtcNow).TotalMinutes is > 9 and <= 10);
    }

    class SlowBackend : IPrintBackend
    {
        public ManualResetEventSlim Gate = new(false);
        public int Sent;
        public SendResult SendLabel(AppSettings st, string n, LabelRaster r, int c) { Gate.Wait(5000); Interlocked.Increment(ref Sent); return new SendResult(0, "ok"); }
        public SendResult SendRawEpl(AppSettings st, string n, byte[] e) { Gate.Wait(5000); Interlocked.Increment(ref Sent); return new SendResult(0, "ok"); }
    }

    static async Task WaitFor(Func<bool> c, int ms = 8000) { var t = DateTime.UtcNow; while (!c() && (DateTime.UtcNow - t).TotalMilliseconds < ms) await Task.Delay(50); }

    static async Task Jobs()
    {
        var outDir = Path.Combine(DataPaths.Root, "epl-out");
        var settings = new SettingsStore(); settings.Update(s => { s.PrinterName = "FILE:" + outDir; });
        var docs = new DocumentStore();
        using var jm = new JobManager(settings, docs);
        var info = docs.Save(File.ReadAllBytes(T.Data("multi_3page.pdf")), "multi_3page.pdf", "Desktop");
        T.Check("document stored with 3 pages and sizes", info.PageCount == 3 && info.PageSizesIn[0][0] == 4);
        var made = jm.Submit(info.Id, null, 2, "Desktop", null, false);
        T.Check("one job per PDF page", made.Count == 3);
        await WaitFor(() => jm.Snapshot().All(j => j.IsFinal));
        T.Check("all jobs completed", jm.Snapshot().All(j => j.Status == JobStatus.Completed), string.Join(",", jm.Snapshot().Select(j => j.Status + ":" + j.Message)));
        var files = Directory.GetFiles(outDir, "*.epl");
        T.Check("3 EPL files written, each ends with P2 (copies)", files.Length == 3 && files.All(f => Encoding.Latin1.GetString(File.ReadAllBytes(f)).EndsWith("P2\n")));

        // manual crop
        var mc = docs.SetManual(info.Id, new LabelRegion { Page = 1, X = 0.5, Y = 0.5, W = 3, H = 2 }, true);
        var mv = DocumentStore.View(mc, true);
        T.Check("manual crop replaces auto labels", mv.ManualCrop && mv.PageCount == 1 && mv.Labels[0].Page == 1 && mv.Labels[0].Fit);
        mv = DocumentStore.View(docs.SetManual(info.Id, new LabelRegion { Page = 0, X = 0, Y = 0, W = 4, H = 3 }, false), true);
        T.Check("manual crop: add another area", mv.PageCount == 2);
        try { docs.SetManual(info.Id, new LabelRegion { Page = 0, X = 1, Y = 1, W = 0.1, H = 0.1 }, false); T.Check("tiny crop rejected", false); } catch (PdfException) { T.Check("tiny crop rejected", true); }
        var mjob = jm.Submit(info.Id, new[] { 0 }, 1, "Desktop", null, false)[0]; await WaitFor(() => jm.Get(mjob.Id)!.IsFinal);
        T.Check("manual-crop label prints", jm.Get(mjob.Id)!.Status == JobStatus.Completed, jm.Get(mjob.Id)!.Message);
        mv = DocumentStore.View(docs.SetManual(info.Id, null, true), true);
        T.Check("manual crop reset returns to automatic", !mv.ManualCrop && mv.PageCount == 3);

        // reprint
        var rp = jm.ReprintLast("Desktop"); await WaitFor(() => jm.Get(rp!.Id)!.IsFinal);
        T.Check("reprint last works", jm.Get(rp!.Id)!.Status == JobStatus.Completed && Directory.GetFiles(outDir, "*.epl").Length == 5);

        // test label job
        var tj = jm.SubmitTest("label"); await WaitFor(() => jm.Get(tj.Id)!.IsFinal);
        T.Check("test label job completes", jm.Get(tj.Id)!.Status == JobStatus.Completed, jm.Get(tj.Id)!.Message);

        // cancel while queued: block worker with a slow backend
        var slow = new SlowBackend(); jm.BackendOverride = _ => slow;
        var a = jm.Submit(info.Id, new[] { 0 }, 1, "Desktop", null, false)[0];
        await WaitFor(() => jm.Get(a.Id)!.Status == JobStatus.Printing);
        var b = jm.Submit(info.Id, new[] { 1 }, 1, "Desktop", null, false)[0];
        var (ok, msg) = jm.Cancel(b.Id);
        T.Check("queued job can be cancelled", ok && jm.Get(b.Id)!.Status == JobStatus.Cancelled, msg);
        var (ok2, msg2) = jm.Cancel(a.Id);
        T.Check("job already being sent cannot be cancelled (clear message)", !ok2 && msg2.Contains("being sent"), msg2);
        slow.Gate.Set(); await WaitFor(() => jm.Get(a.Id)!.IsFinal);
        T.Check("cancelled job never reached the printer", slow.Sent == 1);

        // approval flow
        jm.BackendOverride = null;
        var ap = jm.Submit(info.Id, new[] { 2 }, 1, "Phone", "dev1", true)[0];
        await Task.Delay(300);
        T.Check("approval-required job waits", jm.Get(ap.Id)!.Status == JobStatus.AwaitingApproval && jm.PendingApprovalCount() == 1);
        jm.Approve(ap.Id); await WaitFor(() => jm.Get(ap.Id)!.IsFinal);
        T.Check("approved job prints", jm.Get(ap.Id)!.Status == JobStatus.Completed);
        var rj = jm.Submit(info.Id, new[] { 2 }, 1, "Phone", "dev1", true)[0]; jm.Reject(rj.Id);
        T.Check("rejected job is final and not printed", jm.Get(rj.Id)!.Status == JobStatus.Rejected);

        // friendly errors
        settings.Update(s => { s.PrinterName = ""; s.Backend = BackendKind.RawEpl2; });
        var bad = jm.Submit(info.Id, new[] { 0 }, 1, "Desktop", null, false)[0]; await WaitFor(() => jm.Get(bad.Id)!.IsFinal);
        T.Check("no printer -> friendly message", jm.Get(bad.Id)!.Status == JobStatus.Failed && jm.Get(bad.Id)!.Message.Contains("No printer is selected"), jm.Get(bad.Id)!.Message);
        settings.Update(s => s.PrinterName = "Zebra LP 2443");
        var bad2 = jm.Submit(info.Id, new[] { 0 }, 1, "Desktop", null, false)[0]; await WaitFor(() => jm.Get(bad2.Id)!.IsFinal);
        settings.Update(s => { s.PrinterName = ""; s.Backend = BackendKind.DirectUsb; s.PrinterVerified = false; });
        var safe = jm.Submit(info.Id, new[] { 0 }, 1, "Desktop", null, false)[0]; await WaitFor(() => jm.Get(safe.Id)!.IsFinal);
        T.Check("paper-safe mode blocks label jobs until the raw text test is confirmed", jm.Get(safe.Id)!.Status == JobStatus.Failed && jm.Get(safe.Id)!.Message.Contains("Paper-safe"), jm.Get(safe.Id)!.Message);
        var slow2 = new SlowBackend(); jm.BackendOverride = _ => slow2;
        var s1 = jm.Submit(info.Id, new[] { 0 }, 1, "Desktop", null, false)[0];
        var s2 = jm.Submit(info.Id, new[] { 1 }, 1, "Desktop", null, false)[0];
        var stopMsg = jm.StopAll(); slow2.Gate.Set(); await WaitFor(() => jm.Get(s1.Id)!.IsFinal && jm.Get(s2.Id)!.IsFinal);
        T.Check("STOP cancels waiting jobs", jm.Get(s2.Id)!.Status == JobStatus.Cancelled && stopMsg.StartsWith("Stopped"), stopMsg);
        jm.BackendOverride = null;
        settings.Update(s => { s.PrinterName = "Zebra LP 2443"; s.Backend = BackendKind.RawEpl2; s.PrinterVerified = true; });
        T.Check("non-Windows / missing printer -> clear failure, no crash", jm.Get(bad2.Id)!.Status == JobStatus.Failed && jm.Get(bad2.Id)!.Message.Length > 20, jm.Get(bad2.Id)!.Message);

        // retention
        var oldPdf = docs.PdfPath(info.Id); File.SetLastWriteTimeUtc(oldPdf, DateTime.UtcNow.AddHours(-30));
        var purged = docs.Purge(TimeSpan.FromHours(24), new HashSet<string>());
        T.Check("uploads older than retention are deleted automatically", purged >= 1 && !File.Exists(oldPdf));
        var info2 = docs.Save(File.ReadAllBytes(T.Data("label_4x6.pdf")), "x.pdf", "Desktop");
        File.SetLastWriteTimeUtc(docs.PdfPath(info2.Id), DateTime.UtcNow.AddHours(-30));
        T.Check("...but not while a job still needs the file", docs.Purge(TimeSpan.FromHours(24), new HashSet<string> { info2.Id }) == 0 && File.Exists(docs.PdfPath(info2.Id)));
    }

    static async Task Http()
    {
        var ctx = new AppContext();
        ctx.Settings.Update(s => { s.PrinterVerified = true; s.PrinterName = "FILE:" + Path.Combine(DataPaths.Root, "http-out"); s.RemoteEnabled = true; s.RemotePort = 47391; });
        int port = 47390;
        var desktop = WebHost.BuildDesktop(ctx, port); await desktop.StartAsync();
        ctx.ApplyRemoteSetting();
        T.Check("phone server started", ctx.Remote.Running, ctx.Remote.LastError ?? "");
        var pdf = File.ReadAllBytes(T.Data("label_4x6.pdf"));
        using var h = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        HttpRequestMessage Req(HttpMethod m, string u, HttpContent? c = null, bool zlp = true) { var r = new HttpRequestMessage(m, u) { Content = c }; if (zlp) r.Headers.Add("X-ZLP", "1"); return r; }

        T.Check("desktop UI page served", (await h.GetStringAsync("/")).Contains("RetroLabel"));
        var csp = (await h.GetAsync("/")).Headers.GetValues("Content-Security-Policy").First();
        T.Check("CSP forbids inline script + framing", csp.Contains("script-src 'self'") && csp.Contains("frame-ancestors 'none'"));
        var evil = Req(HttpMethod.Post, "/api/test/hello"); evil.Headers.Add("Origin", "https://evil.example");
        T.Check("cross-site POST is blocked (403)", (await h.SendAsync(evil)).StatusCode == HttpStatusCode.Forbidden);
        T.Check("POST without X-ZLP header is blocked", (await h.SendAsync(Req(HttpMethod.Post, "/api/test/hello", null, false))).StatusCode == HttpStatusCode.Forbidden);
        var rebind = Req(HttpMethod.Get, "/api/state"); rebind.Headers.Host = "evil.example:" + port;
        T.Check("DNS-rebinding Host header blocked", (await h.SendAsync(rebind)).StatusCode == HttpStatusCode.Forbidden);

        var bad = await h.SendAsync(Req(HttpMethod.Post, "/api/docs", new ByteArrayContent(Encoding.ASCII.GetBytes("not a pdf"))));
        T.Check("uploading a non-PDF returns 400 + message", bad.StatusCode == HttpStatusCode.BadRequest && (await bad.Content.ReadAsStringAsync()).Contains("not a PDF"));
        var upC = new ByteArrayContent(pdf); upC.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var up = Req(HttpMethod.Post, "/api/docs", upC); up.Headers.Add("X-File-Name", "label%20one.pdf");
        var doc = JsonDocument.Parse(await (await h.SendAsync(up)).Content.ReadAsStringAsync()).RootElement;
        var docId = doc.GetProperty("id").GetString()!;
        T.Check("upload returns doc info", doc.GetProperty("pageCount").GetInt32() == 1 && doc.GetProperty("name").GetString() == "label one.pdf");
        var png = await h.GetByteArrayAsync($"/api/docs/{docId}/preview/0.png");
        File.WriteAllBytes(Path.Combine(DataPaths.Root, "preview.png"), png);
        T.Check("preview PNG returned", png.Length > 500 && png[1] == 'P');
        var info = JsonDocument.Parse(await h.GetStringAsync($"/api/docs/{docId}/info/0")).RootElement;
        T.Check("preview info reports 812x1218", info.GetProperty("width").GetInt32() == 812 && info.GetProperty("height").GetInt32() == 1218);
        T.Check("path traversal in doc id rejected", (await h.GetAsync("/api/docs/..%2f..%2fsettings/info/0")).StatusCode != HttpStatusCode.OK);

        var pr = await h.SendAsync(Req(HttpMethod.Post, "/api/print", new StringContent($"{{\"docId\":\"{docId}\",\"copies\":2}}", Encoding.UTF8, "application/json")));
        T.Check("desktop print accepted", pr.IsSuccessStatusCode);
        await WaitFor(() => ctx.Jobs.Snapshot().Any() && ctx.Jobs.Snapshot().All(j => j.IsFinal));
        T.Check("desktop print job completed", ctx.Jobs.Snapshot().Any(j => j.Status == JobStatus.Completed));

        // ---- phone side
        using var m = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:47391") };
        T.Check("PWA shell is public", (await m.GetStringAsync("/m")).Contains("RetroLabel") && (await m.GetAsync("/m/manifest.webmanifest")).IsSuccessStatusCode);
        T.Check("desktop API is NOT reachable on the phone port", (await m.GetAsync("/api/state")).StatusCode == HttpStatusCode.NotFound && (await m.GetAsync("/api/settings")).StatusCode != HttpStatusCode.OK);
        T.Check("phone API without token -> 401", (await m.GetAsync("/api/m/state")).StatusCode == HttpStatusCode.Unauthorized);
        var wrong = await m.PostAsync("/api/m/pair", new StringContent("{\"code\":\"NOPE\",\"name\":\"x\"}", Encoding.UTF8, "application/json"));
        T.Check("wrong pairing code -> 403", wrong.StatusCode == HttpStatusCode.Forbidden);
        var pj = JsonDocument.Parse(await (await h.SendAsync(Req(HttpMethod.Post, "/api/remote/pairing"))).Content.ReadAsStringAsync()).RootElement;
        var code = pj.GetProperty("code").GetString()!;
        var paired = JsonDocument.Parse(await (await m.PostAsync("/api/m/pair", new StringContent($"{{\"code\":\"{code}\",\"name\":\"Test <b>phone</b>\"}}", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).RootElement;
        var token = paired.GetProperty("token").GetString()!;
        T.Check("phone pairs with QR code value", token.Length > 30);
        m.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var st = JsonDocument.Parse(await m.GetStringAsync("/api/m/state")).RootElement;
        T.Check("paired phone can read state", st.GetProperty("device").GetProperty("name").GetString()!.StartsWith("Test"));

        var pc = new ByteArrayContent(pdf);
        var pup = new HttpRequestMessage(HttpMethod.Post, "/api/m/docs") { Content = pc }; pup.Headers.Add("X-File-Name", "phone.pdf");
        var pdoc = JsonDocument.Parse(await (await m.SendAsync(pup)).Content.ReadAsStringAsync()).RootElement;
        var pid = pdoc.GetProperty("id").GetString()!;
        T.Check("phone can preview its upload", (await m.GetByteArrayAsync($"/api/m/docs/{pid}/preview/0.png")).Length > 500);
        T.Check("phone CANNOT preview a desktop document", (await m.GetAsync($"/api/m/docs/{docId}/preview/0.png")).StatusCode == HttpStatusCode.NotFound);

        ctx.Settings.Update(s => s.RequireApproval = true);
        var mp = JsonDocument.Parse(await (await m.PostAsync("/api/m/print", new StringContent($"{{\"docId\":\"{pid}\",\"copies\":1}}", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).RootElement;
        var jid = mp.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await Task.Delay(400);
        T.Check("with approval required, the phone job waits", ctx.Jobs.Get(jid)!.Status == JobStatus.AwaitingApproval);
        var cancel = JsonDocument.Parse(await (await m.PostAsync($"/api/m/jobs/{jid}/cancel", null)).Content.ReadAsStringAsync()).RootElement;
        T.Check("phone can cancel an unprinted job", cancel.GetProperty("ok").GetBoolean() && ctx.Jobs.Get(jid)!.Status == JobStatus.Cancelled);
        ctx.Settings.Update(s => s.RequireApproval = false);
        var mp2 = JsonDocument.Parse(await (await m.PostAsync("/api/m/print", new StringContent($"{{\"docId\":\"{pid}\",\"copies\":1}}", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).RootElement;
        var jid2 = mp2.GetProperty("jobs")[0].GetProperty("id").GetString()!;
        await WaitFor(() => ctx.Jobs.Get(jid2)!.IsFinal);
        T.Check("phone job prints without approval when not required", ctx.Jobs.Get(jid2)!.Status == JobStatus.Completed);
        var rep = await m.PostAsync($"/api/m/jobs/{jid2}/reprint", null);
        T.Check("phone can reprint a recent label", rep.IsSuccessStatusCode);

        // iPhone share-sheet shortcut endpoint
        var qp = new ByteArrayContent(File.ReadAllBytes(T.Data("fullpage_return.pdf"))); 
        var qreq = new HttpRequestMessage(HttpMethod.Post, "/api/m/quickprint?copies=1") { Content = qp }; qreq.Headers.Add("X-File-Name", "return.pdf");
        var qres = await m.SendAsync(qreq); var qtxt = await qres.Content.ReadAsStringAsync();
        T.Check("share-sheet quickprint: upload + auto-crop + print in one call", qres.IsSuccessStatusCode && qtxt.Contains("2 label"), qtxt);
        var qbad = await new HttpClient { BaseAddress = new Uri("http://127.0.0.1:47391") }.PostAsync("/api/m/quickprint", new ByteArrayContent(pdf));
        T.Check("quickprint without a paired token is refused", qbad.StatusCode == HttpStatusCode.Unauthorized);
        var qjunk = new HttpRequestMessage(HttpMethod.Post, "/api/m/quickprint") { Content = new ByteArrayContent(Encoding.ASCII.GetBytes("nope")) };
        var qj = await m.SendAsync(qjunk);
        T.Check("quickprint of a non-PDF returns a readable message", qj.StatusCode == HttpStatusCode.BadRequest && (await qj.Content.ReadAsStringAsync()).StartsWith("Not printed"));

        // other phone cannot touch this phone's jobs
        var (c4, _) = ctx.Devices.NewPairingCode();
        var other = ctx.Devices.Pair(c4, "Other", "10.1.1.1");
        using var m2 = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:47391") }; m2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.token);
        T.Check("a second phone cannot cancel/reprint the first phone's jobs", !JsonDocument.Parse(await (await m2.PostAsync($"/api/m/jobs/{jid2}/cancel", null)).Content.ReadAsStringAsync()).RootElement.GetProperty("ok").GetBoolean() && (await m2.PostAsync($"/api/m/jobs/{jid2}/reprint", null)).StatusCode == HttpStatusCode.NotFound);

        var big = new ByteArrayContent(new byte[16 * 1024 * 1024]);
        bool tooBigRejected;
        try { var rb = await m.PostAsync("/api/m/docs", big); tooBigRejected = rb.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge; }
        catch (HttpRequestException) { tooBigRejected = true; /* server cut the connection mid-upload */ }
        T.Check("oversize upload rejected", tooBigRejected);

        // revoke + disable (fresh client: the oversize upload above can leave the old connection reset)
        using var mf = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:47391") };
        mf.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        ctx.Devices.Revoke(JsonDocument.Parse(await mf.GetStringAsync("/api/m/state")).RootElement.GetProperty("device").GetProperty("id").GetString()!);
        T.Check("revoked phone is rejected", (await mf.GetAsync("/api/m/state")).StatusCode == HttpStatusCode.Unauthorized);
        var put = Req(HttpMethod.Put, "/api/settings", new StringContent(JsonSerializer.Serialize(ctx.Settings.Get().with0(false), Json.Options), Encoding.UTF8, "application/json"));
        await h.SendAsync(put);
        await Task.Delay(300);
        T.Check("disabling remote printing stops the phone listener", !ctx.Remote.Running);
        try { using var m5 = new HttpClient(); await m5.GetAsync("http://127.0.0.1:47391/m"); T.Check("phone port closed after disable", false); } catch { T.Check("phone port closed after disable", true); }

        // Downloads watcher (Ingest decides "looks like a label")
        ctx.Settings.Update(s => s.WatchOnlyLabels = true);
        T.Check("watcher accepts a 4x6 label PDF", ctx.Watcher.Ingest(File.ReadAllBytes(T.Data("label_4x6.pdf")), "a.pdf") != null);
        T.Check("watcher accepts a full-page return printout (cut-line box)", ctx.Watcher.Ingest(File.ReadAllBytes(T.Data("fullpage_return.pdf")), "b.pdf") != null);
        if (BarcodeCheck.Available) T.Check("watcher accepts a page-filling label (barcode found)", ctx.Watcher.Ingest(File.ReadAllBytes(T.Data("page_filling_label.pdf")), "c.pdf") != null);
        T.Check("watcher ignores a text-only invoice PDF", ctx.Watcher.Ingest(File.ReadAllBytes(T.Data("not_a_label_invoice.pdf")), "inv.pdf") == null);
        ctx.Settings.Update(s => s.WatchOnlyLabels = false);
        T.Check("watcher takes any PDF when 'only labels' is off", ctx.Watcher.Ingest(File.ReadAllBytes(T.Data("not_a_label_invoice.pdf")), "inv.pdf") != null);
        var wdir = Path.Combine(DataPaths.Root, "watched"); Directory.CreateDirectory(wdir);
        ctx.Settings.Update(s => { s.WatchEnabled = true; s.WatchFolder = wdir; s.WatchOnlyLabels = true; });
        ctx.Watcher.Restart();
        var before = ctx.Incoming?.seq ?? 0;
        File.Copy(T.Data("label_4x6.pdf"), Path.Combine(wdir, "download.pdf.part"));
        File.Move(Path.Combine(wdir, "download.pdf.part"), Path.Combine(wdir, "download.pdf"));   // browser-style rename
        await WaitFor(() => (ctx.Incoming?.seq ?? 0) > before, 10000);
        T.Check("watched folder: a downloaded label is picked up automatically", (ctx.Incoming?.seq ?? 0) > before && ctx.Docs.Get(ctx.Incoming!.Value.docId)!.Name == "download.pdf");
        ctx.Watcher.Stop();

        var diag = await h.GetStringAsync("/api/diagnostics");
        T.Check("diagnostics endpoint works off-Windows", diag.Contains("findings"));
        await desktop.StopAsync();
        ctx.Jobs.Dispose();
    }
}

static class Ext
{
    public static AppSettings with0(this AppSettings s, bool remote) { s.RemoteEnabled = remote; return s; }
}

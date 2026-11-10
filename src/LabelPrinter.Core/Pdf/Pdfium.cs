using System.Runtime.InteropServices;

namespace LabelPrinter.Core.Pdf;

/// <summary>Minimal PDFium binding (pdfium.dll on Windows, libpdfium.so on Linux; shipped next to the app).</summary>
static unsafe class Native
{
    const string L = "pdfium";
    [DllImport(L)] public static extern void FPDF_InitLibrary();
    [DllImport(L)] public static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport(L)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(L)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(L)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(L)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(L)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(L)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(L)] public static extern uint FPDF_GetLastError();
    [DllImport(L)] public static extern IntPtr FPDFBitmap_Create(int w, int h, int alpha);
    [DllImport(L)] public static extern void FPDFBitmap_FillRect(IntPtr bmp, int l, int t, int w, int h, uint color);
    [DllImport(L)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bmp);
    [DllImport(L)] public static extern int FPDFBitmap_GetStride(IntPtr bmp);
    [DllImport(L)] public static extern void FPDFBitmap_Destroy(IntPtr bmp);
    [DllImport(L)] public static extern void FPDF_RenderPageBitmap(IntPtr bmp, IntPtr page, int sx, int sy, int w, int h, int rotate, int flags);
}

public class PdfException : Exception { public PdfException(string m) : base(m) { } }

/// <summary>An open PDF. Thread-safe (PDFium itself is not, so every call is serialized).</summary>
public sealed class PdfDocument : IDisposable
{
    static readonly object Gate = new();
    static bool _init;
    IntPtr _doc, _mem;
    public int PageCount { get; }

    PdfDocument(IntPtr doc, IntPtr mem, int pages) { _doc = doc; _mem = mem; PageCount = pages; }

    public static PdfDocument Open(byte[] data)
    {
        if (data.Length < 8 || data[0] != '%' || data[1] != 'P' || data[2] != 'D' || data[3] != 'F')
            throw new PdfException("This file is not a PDF (missing %PDF header).");
        lock (Gate)
        {
            if (!_init) { Native.FPDF_InitLibrary(); _init = true; }
            var mem = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, mem, data.Length);
            var doc = Native.FPDF_LoadMemDocument(mem, data.Length, null);
            if (doc == IntPtr.Zero)
            {
                var err = Native.FPDF_GetLastError();
                Marshal.FreeHGlobal(mem);
                throw new PdfException(err switch
                {
                    4 => "The PDF is password protected. Remove the password and try again.",
                    3 => "The PDF uses an unsupported security handler.",
                    2 => "The PDF is damaged or incomplete.",
                    _ => $"PDF could not be opened (PDFium error {err})."
                });
            }
            return new PdfDocument(doc, mem, Native.FPDF_GetPageCount(doc));
        }
    }
    public static PdfDocument Open(string path) => Open(File.ReadAllBytes(path));

    /// <summary>Page size in inches (PDF points / 72), honouring /Rotate.</summary>
    public (double w, double h) GetPageSizeInches(int index)
    {
        lock (Gate)
        {
            var p = LoadPage(index);
            try { return (Native.FPDF_GetPageWidthF(p) / 72.0, Native.FPDF_GetPageHeightF(p) / 72.0); }
            finally { Native.FPDF_ClosePage(p); }
        }
    }

    /// <summary>
    /// Render a page to an 8-bit gray buffer, w x h pixels, on white. Rendered straight from vector data at the target
    /// size (no later resampling). Embedded raster images are NOT smoothed (nearest neighbour). Vector paths keep anti-aliasing: the later 50% threshold then places each bar edge on the nearest dot. (PDFium's no-smooth-path mode was tested and widens bars by a dot on float rounding, so it is deliberately not used.)
    /// </summary>
    public byte[] RenderGray(int index, int w, int h) => RenderGrayRegion(index, w, h, w, h, 0, 0);

    /// <summary>
    /// Render part of a page: the page is drawn at fullW x fullH pixels and the window starting at (offX, offY) of size w x h is
    /// returned. Used by auto-crop so the cropped label is still rendered from vector data at full resolution.
    /// </summary>
    public unsafe byte[] RenderGrayRegion(int index, int w, int h, int fullW, int fullH, int offX, int offY)
    {
        if (w < 1 || h < 1 || (long)w * h > 60_000_000) throw new PdfException("Requested render size is out of range.");
        lock (Gate)
        {
            var page = LoadPage(index);
            var bmp = Native.FPDFBitmap_Create(w, h, 0);
            if (bmp == IntPtr.Zero) { Native.FPDF_ClosePage(page); throw new PdfException("Out of memory while rendering."); }
            try
            {
                Native.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
                const int FPDF_ANNOT = 0x01, FPDF_PRINTING = 0x800, NO_SMOOTHIMAGE = 0x2000;
                Native.FPDF_RenderPageBitmap(bmp, page, -offX, -offY, fullW, fullH, 0, FPDF_ANNOT | FPDF_PRINTING | NO_SMOOTHIMAGE);
                var src = (byte*)Native.FPDFBitmap_GetBuffer(bmp);
                int stride = Native.FPDFBitmap_GetStride(bmp);
                var gray = new byte[w * h];
                for (int y = 0; y < h; y++)
                {
                    byte* row = src + (long)y * stride;
                    int o = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        // BGRx -> luma (integer Rec.601)
                        int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                        gray[o + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                    }
                }
                return gray;
            }
            finally { Native.FPDFBitmap_Destroy(bmp); Native.FPDF_ClosePage(page); }
        }
    }

    IntPtr LoadPage(int index)
    {
        if (_doc == IntPtr.Zero) throw new ObjectDisposedException(nameof(PdfDocument));
        if (index < 0 || index >= PageCount) throw new PdfException($"Page {index + 1} does not exist (PDF has {PageCount}).");
        var p = Native.FPDF_LoadPage(_doc, index);
        if (p == IntPtr.Zero) throw new PdfException($"Page {index + 1} could not be loaded.");
        return p;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_doc != IntPtr.Zero) { Native.FPDF_CloseDocument(_doc); _doc = IntPtr.Zero; }
            if (_mem != IntPtr.Zero) { Marshal.FreeHGlobal(_mem); _mem = IntPtr.Zero; }
        }
    }
}

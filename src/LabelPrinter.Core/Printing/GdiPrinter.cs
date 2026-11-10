using System.Runtime.InteropServices;
using LabelPrinter.Core.Imaging;

namespace LabelPrinter.Core.Printing;

/// <summary>
/// Backend 2: print the already-thresholded 1-bit bitmap through the Windows printer DRIVER (GDI), 1 dot -> 1 device pixel,
/// no print dialog. Only sensible if the queue has a real label-printer driver that is set to 4x6 / 203 dpi.
/// </summary>
public static class GdiPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DOCINFO { public int cbSize; public string lpszDocName; public string? lpszOutput; public string? lpszDatatype; public int fwType; }
    const string G = "gdi32.dll";
    [DllImport(G, CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport(G)] static extern bool DeleteDC(IntPtr dc);
    [DllImport(G, CharSet = CharSet.Unicode, SetLastError = true)] static extern int StartDoc(IntPtr dc, ref DOCINFO di);
    [DllImport(G)] static extern int EndDoc(IntPtr dc);
    [DllImport(G)] static extern int AbortDoc(IntPtr dc);
    [DllImport(G)] static extern int StartPage(IntPtr dc);
    [DllImport(G)] static extern int EndPage(IntPtr dc);
    [DllImport(G)] static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport(G)] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport(G)] static extern int StretchDIBits(IntPtr dc, int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, byte[] bits, byte[] bmi, uint usage, uint rop);

    public static void Print(string printer, string docName, LabelRaster r, int copies, AppSettings st, List<string> notes)
    {
        if (!OperatingSystem.IsWindows()) throw new PrintException("Windows printing is only available on Windows.");
        var dc = CreateDC("WINSPOOL", printer, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new PrintException($"Could not open printer '{printer}' (error {Marshal.GetLastWin32Error()}).", "Check the queue name in Settings.");
        try
        {
            int dpiX = GetDeviceCaps(dc, 88), dpiY = GetDeviceCaps(dc, 90);
            int offX = GetDeviceCaps(dc, 112), offY = GetDeviceCaps(dc, 113);   // PHYSICALOFFSETX/Y
            int horz = GetDeviceCaps(dc, 8), vert = GetDeviceCaps(dc, 10);
            notes.Add($"Driver reports {dpiX}x{dpiY} dpi, printable {horz}x{vert} px, hardware margin {offX},{offY} px.");
            int dw = r.Width, dh = r.Height;
            if (dpiX != st.Dpi || dpiY != st.Dpi)
            {
                dw = (int)Math.Round(r.Width * (double)dpiX / st.Dpi);
                dh = (int)Math.Round(r.Height * (double)dpiY / st.Dpi);
                notes.Add($"WARNING: driver is {dpiX} dpi, not {st.Dpi}. Bitmap is resampled with nearest-neighbour, barcodes may be affected. Set the driver to {st.Dpi} dpi.");
            }
            var di = new DOCINFO { cbSize = Marshal.SizeOf<DOCINFO>(), lpszDocName = docName };
            if (StartDoc(dc, ref di) <= 0) throw new PrintException($"The driver refused the print job (StartDoc error {Marshal.GetLastWin32Error()}).");
            try
            {
                SetStretchBltMode(dc, 3 /*COLORONCOLOR: no interpolation*/);
                int stride = ((r.Width + 31) / 32) * 4;
                var dib = new byte[stride * r.Height];                 // 1bpp, bit 0 = palette[0] = black
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++)
                        if (!r.Get(x, y)) dib[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                var bmi = new byte[40 + 8];
                BitConverter.GetBytes(40).CopyTo(bmi, 0);
                BitConverter.GetBytes(r.Width).CopyTo(bmi, 4);
                BitConverter.GetBytes(-r.Height).CopyTo(bmi, 8);       // top-down
                BitConverter.GetBytes((short)1).CopyTo(bmi, 12);
                BitConverter.GetBytes((short)1).CopyTo(bmi, 14);
                BitConverter.GetBytes(2).CopyTo(bmi, 32);              // colours used
                bmi[44] = bmi[45] = bmi[46] = 255;                     // palette[1] = white; [0] stays black
                for (int c = 0; c < Math.Max(1, copies); c++)
                {
                    if (StartPage(dc) <= 0) throw new PrintException("The driver refused a page (StartPage).");
                    int res = StretchDIBits(dc, -offX, -offY, dw, dh, 0, 0, r.Width, r.Height, dib, bmi, 0, 0x00CC0020);
                    if (res == 0 || res == -1) throw new PrintException("The driver could not draw the label bitmap (StretchDIBits failed).");
                    EndPage(dc);
                }
                EndDoc(dc);
            }
            catch { AbortDoc(dc); throw; }
        }
        finally { DeleteDC(dc); }
    }
}

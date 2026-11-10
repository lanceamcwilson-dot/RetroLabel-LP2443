using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LabelPrinter.Core.Printing;

public record UsbPrinterDevice(string Path, string Name, string DeviceId1284, bool LooksLikeZebra);

/// <summary>
/// "Custom driver": writes printer-language bytes straight to the USB printer-class device that Windows' built-in
/// usbprint.sys creates for the StarTech ICUSB1284 (the same device that backs the USB001 port). No print queue,
/// no Zebra driver, and it does not care which USB socket the adapter is in.
/// </summary>
public static class DirectUsb
{
    static Guid UsbPrintGuid = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");   // GUID_DEVINTERFACE_USBPRINT

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, int index, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, int prop, out int regType, byte[] buf, int size, out int required);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3;
    const uint IOCTL_USBPRINT_GET_1284_ID = 0x220034;

    public static List<UsbPrinterDevice> List()
    {
        var res = new List<UsbPrinterDevice>();
        if (!OperatingSystem.IsWindows()) return res;
        var set = SetupDiGetClassDevs(ref UsbPrintGuid, IntPtr.Zero, IntPtr.Zero, 0x12 /*PRESENT|DEVICEINTERFACE*/);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return res;
        try
        {
            for (int i = 0; ; i++)
            {
                var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref UsbPrintGuid, i, ref d)) break;
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int need, ref info);
                if (need <= 0) continue;
                var buf = Marshal.AllocHGlobal(need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out _, ref info)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4) ?? "";
                    var name = RegString(set, ref info, 0x0C) ?? RegString(set, ref info, 0x00) ?? "USB printer";
                    var id = Read1284Id(path);
                    bool zebra = (id + " " + name).Contains("Zebra", StringComparison.OrdinalIgnoreCase) || (id + name).Contains("Eltron", StringComparison.OrdinalIgnoreCase) || id.Contains("EPL", StringComparison.OrdinalIgnoreCase) || id.Contains("2443");
                    res.Add(new UsbPrinterDevice(path, name, id, zebra));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return res;
    }

    static string? RegString(IntPtr set, ref SP_DEVINFO_DATA info, int prop)
    {
        var b = new byte[1024];
        return SetupDiGetDeviceRegistryProperty(set, ref info, prop, out _, b, b.Length, out int n) && n > 2 ? Encoding.Unicode.GetString(b, 0, n).TrimEnd('\0') : null;
    }

    /// <summary>IEEE-1284 Device ID reported by the printer through the adapter (e.g. "MFG:Zebra;MDL:...;CMD:EPL2;"). Empty if none.</summary>
    public static string Read1284Id(string path)
    {
        try
        {
            using var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return "";
            var b = new byte[1024];
            if (!DeviceIoControl(h, IOCTL_USBPRINT_GET_1284_ID, IntPtr.Zero, 0, b, b.Length, out int n, IntPtr.Zero) || n <= 2) return "";
            int len = Math.Min(n, (b[0] << 8) | b[1]);
            return Encoding.ASCII.GetString(b, 2, Math.Max(0, len - 2)).Trim('\0', ' ');
        }
        catch { return ""; }
    }

    /// <summary>Pick the device: the saved one if present, else the Zebra-looking one, else the only one.</summary>
    public static UsbPrinterDevice Resolve(string preferredPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PrintException("Direct USB printing works only on Windows.");
        var all = List();
        if (all.Count == 0)
            throw new PrintException("No USB printer adapter found.", "Check the StarTech adapter is plugged into the PC and the LP 2443 is on. In Device Manager it should appear as 'USB Printing Support'.");
        var hit = all.FirstOrDefault(d => string.Equals(d.Path, preferredPath, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(d => d.LooksLikeZebra)
               ?? (all.Count == 1 ? all[0] : null);
        if (hit == null) throw new PrintException($"{all.Count} USB printers are connected and none identifies as a Zebra.", "Pick the right one in Settings → Printer.");
        return hit;
    }

    static SafeFileHandle? _current;
    static volatile bool _abort;

    /// <summary>Stop button: cancel the transfer in progress. (Data the printer already received stays in its buffer; power-cycle to clear.)</summary>
    public static bool AbortCurrent()
    {
        _abort = true;
        var h = _current;
        if (h == null || h.IsInvalid || h.IsClosed) return false;
        try { return CancelIoEx(h, IntPtr.Zero); } catch { return false; }
    }

    public static void Write(string path, byte[] data, TimeSpan timeout)
    {
        _abort = false;
        using var h = CreateFile(path, GENERIC_WRITE, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastWin32Error();
            throw new PrintException($"Could not open the USB printer (error {e}).", e == 5 || e == 32 ? "Another program (or a Windows print job) is using it right now. Wait and retry." : "Re-plug the adapter and retry.");
        }
        _current = h;
        using var fs = new FileStream(h, FileAccess.Write, 1, false);
        var t = Task.Run(() =>
        {
            for (int off = 0; off < data.Length && !_abort; off += 4096) fs.Write(data, off, Math.Min(4096, data.Length - off));
            fs.Flush();
        });
        bool done;
        try { done = t.Wait(timeout); } catch (AggregateException) { done = true; }
        if (!done)
        {
            _current = null;
            CancelIoEx(h, IntPtr.Zero);
            throw new PrintException("The printer stopped accepting data (timed out).", "Check it is on, has labels, the head is closed, and the parallel cable is seated. Then press FEED once and retry.");
        }
        _current = null;
        if (_abort) throw new PrintException("Stopped by you. Part of the label may already be in the printer.", "Turn the printer off and on to clear it.");
        if (t.Exception != null) throw new PrintException("Writing to the printer failed: " + t.Exception.InnerException?.Message, "Re-plug the adapter and retry.");
    }
}

public class DirectUsbBackend : IPrintBackend
{
    public SendResult SendLabel(AppSettings st, string docName, Imaging.LabelRaster raster, int copies)
        => SendRawEpl(st, docName, Epl.Epl2Builder.BuildRasterJob(raster, st, copies));
    public SendResult SendRawEpl(AppSettings st, string docName, byte[] epl)
    {
        var dev = DirectUsb.Resolve(st.DirectDevicePath);
        DirectUsb.Write(dev.Path, epl, TimeSpan.FromSeconds(60));
        return new SendResult(0, $"Sent {epl.Length:N0} bytes of EPL2 directly to {dev.Name}");
    }
}

using System.Runtime.InteropServices;

namespace LabelPrinter.Core.Printing;

public class PrintException : Exception
{
    public string Hint { get; }
    public PrintException(string message, string hint = "") : base(message) { Hint = hint; }
    public string Full => string.IsNullOrEmpty(Hint) ? Message : $"{Message} {Hint}";
}

public record PrinterInfo(string Name, string Driver, string Port, uint Status, int JobCount, bool IsDefault, string StatusText, bool Offline);
public record PortInfo(string Name, string Monitor, string Description);
public record SpoolJob(int Id, string Document, string User, uint Status, string StatusText, int TotalPages, DateTime Submitted);

/// <summary>Thin wrapper over winspool.drv. Everything is a no-op / empty on non-Windows (used only for automated tests).</summary>
public static class SpoolerApi
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PRINTER_INFO_2
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? pServerName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pPrinterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pShareName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pPortName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDriverName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pLocation;
        public IntPtr pDevMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pSepFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pPrintProcessor;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDatatype;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct JOB_INFO_1
    {
        public uint JobId;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pPrinterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pMachineName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pUserName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDocument;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDatatype;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pStatus;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public ushort Y, Mo, DoW, D, H, Mi, S, Ms;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PORT_INFO_2
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? pPortName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pMonitorName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDescription;
        public uint fPortType, Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDatatype;
    }

    const string W = "winspool.drv";
    [DllImport(W, SetLastError = true, CharSet = CharSet.Unicode)] static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buf, uint size, out uint needed, out uint returned);
    [DllImport(W, SetLastError = true, CharSet = CharSet.Unicode)] static extern bool EnumPorts(string? server, uint level, IntPtr buf, uint size, out uint needed, out uint returned);
    [DllImport(W, SetLastError = true, CharSet = CharSet.Unicode)] static extern bool OpenPrinter(string name, out IntPtr h, IntPtr defaults);
    [DllImport(W, SetLastError = true)] static extern bool ClosePrinter(IntPtr h);
    [DllImport(W, SetLastError = true, CharSet = CharSet.Unicode)] static extern uint StartDocPrinter(IntPtr h, uint level, ref DOC_INFO_1 di);
    [DllImport(W, SetLastError = true)] static extern bool EndDocPrinter(IntPtr h);
    [DllImport(W, SetLastError = true)] static extern bool StartPagePrinter(IntPtr h);
    [DllImport(W, SetLastError = true)] static extern bool EndPagePrinter(IntPtr h);
    [DllImport(W, SetLastError = true)] static extern bool WritePrinter(IntPtr h, IntPtr buf, uint len, out uint written);
    [DllImport(W, SetLastError = true)] static extern bool AbortPrinter(IntPtr h);
    [DllImport(W, SetLastError = true, CharSet = CharSet.Unicode)] static extern bool EnumJobs(IntPtr h, uint first, uint count, uint level, IntPtr buf, uint size, out uint needed, out uint returned);
    [DllImport(W, SetLastError = true)] static extern bool SetJob(IntPtr h, uint jobId, uint level, IntPtr jobInfo, uint command);
    [DllImport(W, SetLastError = true, CharSet = CharSet.Unicode)] static extern bool GetDefaultPrinter(System.Text.StringBuilder? buf, ref uint size);

    public static bool IsAvailable => OperatingSystem.IsWindows();

    public static string DescribeWin32(int code) => code switch
    {
        2 => "The printer or port was not found (error 2).",
        5 => "Access denied by Windows (error 5). Run the app as the same user that owns the printer, or check printer permissions.",
        6 => "Invalid handle (error 6): the queue was removed while printing.",
        1722 => "The print spooler service is not reachable (error 1722). Start the 'Print Spooler' service.",
        1801 => "Windows does not know a printer with that name (error 1801). It may have been renamed or deleted.",
        1797 => "The printer driver does not accept RAW data (error 1797).",
        1796 => "The port is unknown (error 1796).",
        1788 => "The spooler rejected the data type (error 1788).",
        _ => new System.ComponentModel.Win32Exception(code).Message + $" (error {code})."
    };

    static PrintException Fail(string what, string hint = "")
    {
        int e = Marshal.GetLastWin32Error();
        return new PrintException($"{what}: {DescribeWin32(e)}", hint);
    }

    static string StatusText(uint s)
    {
        var l = new List<string>();
        if ((s & 0x1) != 0) l.Add("paused");
        if ((s & 0x2) != 0) l.Add("error");
        if ((s & 0x4) != 0) l.Add("pending deletion");
        if ((s & 0x8) != 0) l.Add("paper jam");
        if ((s & 0x10) != 0) l.Add("paper out");
        if ((s & 0x20) != 0) l.Add("manual feed");
        if ((s & 0x40) != 0) l.Add("paper problem");
        if ((s & 0x80) != 0) l.Add("OFFLINE");
        if ((s & 0x100) != 0) l.Add("I/O active");
        if ((s & 0x200) != 0) l.Add("busy");
        if ((s & 0x400) != 0) l.Add("printing");
        if ((s & 0x800) != 0) l.Add("output bin full");
        if ((s & 0x1000) != 0) l.Add("not available");
        if ((s & 0x2000) != 0) l.Add("waiting");
        if ((s & 0x4000) != 0) l.Add("processing");
        if ((s & 0x8000) != 0) l.Add("initializing");
        if ((s & 0x10000) != 0) l.Add("warming up");
        if ((s & 0x20000) != 0) l.Add("toner low");
        if ((s & 0x40000) != 0) l.Add("no toner");
        if ((s & 0x80000) != 0) l.Add("page punt");
        if ((s & 0x100000) != 0) l.Add("user intervention");
        if ((s & 0x200000) != 0) l.Add("out of memory");
        if ((s & 0x400000) != 0) l.Add("door open");
        return l.Count == 0 ? "ready" : string.Join(", ", l);
    }

    static T[] EnumStructs<T>(Func<IntPtr, uint, (bool ok, uint needed, uint returned)> call, int structSize) where T : struct
    {
        var (ok, needed, _) = call(IntPtr.Zero, 0);
        if (needed == 0) return Array.Empty<T>();
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            (ok, needed, var returned) = call(buf, needed);
            if (!ok) return Array.Empty<T>();
            var res = new T[returned];
            for (int i = 0; i < returned; i++) res[i] = Marshal.PtrToStructure<T>(buf + i * structSize)!;
            return res;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static string? DefaultPrinter()
    {
        if (!IsAvailable) return null;
        uint n = 0; GetDefaultPrinter(null, ref n);
        if (n == 0) return null;
        var sb = new System.Text.StringBuilder((int)n);
        return GetDefaultPrinter(sb, ref n) ? sb.ToString() : null;
    }

    public static List<PrinterInfo> GetPrinters()
    {
        var list = new List<PrinterInfo>();
        if (!IsAvailable) return list;
        var def = DefaultPrinter();
        var arr = EnumStructs<PRINTER_INFO_2>((b, sz) => { var ok = EnumPrinters(0x2 | 0x4, null, 2, b, sz, out var n, out var r); return (ok || b == IntPtr.Zero, n, r); }, Marshal.SizeOf<PRINTER_INFO_2>());
        foreach (var p in arr)
            list.Add(new PrinterInfo(p.pPrinterName ?? "", p.pDriverName ?? "", p.pPortName ?? "", p.Status, (int)p.cJobs,
                string.Equals(p.pPrinterName, def, StringComparison.OrdinalIgnoreCase), StatusText(p.Status), (p.Status & 0x80) != 0 || (p.Status & 0x1000) != 0));
        return list.OrderBy(p => p.Name).ToList();
    }

    public static PrinterInfo? GetPrinter(string name) => GetPrinters().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static List<PortInfo> GetPorts()
    {
        var list = new List<PortInfo>();
        if (!IsAvailable) return list;
        var arr = EnumStructs<PORT_INFO_2>((b, sz) => { var ok = EnumPorts(null, 2, b, sz, out var n, out var r); return (ok || b == IntPtr.Zero, n, r); }, Marshal.SizeOf<PORT_INFO_2>());
        foreach (var p in arr) list.Add(new PortInfo(p.pPortName ?? "", p.pMonitorName ?? "", p.pDescription ?? ""));
        return list;
    }

    public static List<SpoolJob> GetJobs(string printer)
    {
        var list = new List<SpoolJob>();
        if (!IsAvailable || !OpenPrinter(printer, out var h, IntPtr.Zero)) return list;
        try
        {
            var arr = EnumStructs<JOB_INFO_1>((b, sz) => { var ok = EnumJobs(h, 0, 999, 1, b, sz, out var n, out var r); return (ok || b == IntPtr.Zero, n, r); }, Marshal.SizeOf<JOB_INFO_1>());
            foreach (var j in arr)
            {
                DateTime t;
                try { t = new DateTime(j.Y, j.Mo, j.D, j.H, j.Mi, j.S, DateTimeKind.Utc); } catch { t = DateTime.UtcNow; }
                list.Add(new SpoolJob((int)j.JobId, j.pDocument ?? "", j.pUserName ?? "", j.Status, StatusText(j.Status), (int)j.TotalPages, t));
            }
        }
        finally { ClosePrinter(h); }
        return list;
    }

    public static bool CancelJob(string printer, int jobId)
    {
        if (!IsAvailable || !OpenPrinter(printer, out var h, IntPtr.Zero)) return false;
        try { return SetJob(h, (uint)jobId, 0, IntPtr.Zero, 5 /*JOB_CONTROL_DELETE*/); }
        finally { ClosePrinter(h); }
    }

    /// <summary>Send bytes to the queue with datatype RAW: the driver does not touch them. Returns the spooler job id.</summary>
    public static int WriteRaw(string printer, string docName, byte[] data)
    {
        if (!IsAvailable) throw new PrintException("Windows printing is only available on Windows.");
        if (string.IsNullOrWhiteSpace(printer)) throw new PrintException("No printer selected.", "Choose your Zebra printer queue in Settings.");
        if (!OpenPrinter(printer, out var h, IntPtr.Zero))
            throw Fail($"Could not open printer '{printer}'", "Open Diagnostics to see the queues Windows knows about.");
        try
        {
            var di = new DOC_INFO_1 { pDocName = docName, pOutputFile = null, pDatatype = "RAW" };
            uint jobId = StartDocPrinter(h, 1, ref di);
            if (jobId == 0)
                throw Fail("Windows refused the RAW print job", "This queue's driver may not support RAW. Use the 'Generic / Text Only' driver, or switch backend to 'Windows driver (GDI)'.");
            var buf = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, buf, data.Length);
                if (!StartPagePrinter(h)) { var ex = Fail("StartPagePrinter failed"); AbortPrinter(h); throw ex; }
                uint off = 0;
                while (off < data.Length)
                {
                    uint chunk = (uint)Math.Min(32 * 1024, data.Length - off), written;
                    if (!WritePrinter(h, buf + (int)off, chunk, out written) || written == 0)
                    {
                        var ex = Fail("Writing to the printer failed", "Check the USB adapter, cable and that the printer is powered on.");
                        AbortPrinter(h); throw ex;
                    }
                    off += written;
                }
                EndPagePrinter(h);
            }
            finally { Marshal.FreeHGlobal(buf); }
            if (!EndDocPrinter(h)) throw Fail("EndDocPrinter failed");
            return (int)jobId;
        }
        finally { ClosePrinter(h); }
    }
}

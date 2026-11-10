using LabelPrinter.Core.Epl;
using LabelPrinter.Core.Imaging;

namespace LabelPrinter.Core.Printing;

public record SendResult(int SpoolerJobId, string Note);

public interface IPrintBackend
{
    SendResult SendLabel(AppSettings st, string docName, LabelRaster raster, int copies);
    SendResult SendRawEpl(AppSettings st, string docName, byte[] epl);
}

public static class BackendFactory
{
    public static IPrintBackend For(AppSettings st) =>
        st.PrinterName.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase) || st.Backend == BackendKind.SaveToFile
            ? new FileBackend()
            : st.Backend == BackendKind.DirectUsb ? new DirectUsbBackend()
            : st.Backend == BackendKind.WindowsDriverGdi ? new GdiBackend() : new RawEpl2Backend();
}

/// <summary>Default: EPL2 sent RAW through the Windows queue (no driver rendering).</summary>
public class RawEpl2Backend : IPrintBackend
{
    public SendResult SendLabel(AppSettings st, string docName, LabelRaster raster, int copies)
        => SendRawEpl(st, docName, Epl2Builder.BuildRasterJob(raster, st, copies));
    public SendResult SendRawEpl(AppSettings st, string docName, byte[] epl)
    {
        var id = SpoolerApi.WriteRaw(st.PrinterName, docName, epl);
        return new SendResult(id, $"{epl.Length:N0} bytes of EPL2 sent as RAW");
    }
}

public class GdiBackend : IPrintBackend
{
    public SendResult SendLabel(AppSettings st, string docName, LabelRaster raster, int copies)
    {
        var notes = new List<string>();
        GdiPrinter.Print(st.PrinterName, docName, raster, copies, st, notes);
        return new SendResult(0, string.Join(" ", notes));
    }
    public SendResult SendRawEpl(AppSettings st, string docName, byte[] epl)
    {
        // Diagnostic raw jobs always use RAW even in GDI mode: it is the only way to test the data path itself.
        var id = SpoolerApi.WriteRaw(st.PrinterName, docName, epl);
        return new SendResult(id, "Raw test data sent");
    }
}

/// <summary>Writes the generated EPL2 to a file. Used for automated tests and for inspecting exactly what would be sent.</summary>
public class FileBackend : IPrintBackend
{
    string Target(AppSettings st, string docName)
    {
        var p = st.PrinterName.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase) ? st.PrinterName[5..] : st.SaveToFilePath;
        if (string.IsNullOrWhiteSpace(p)) p = Path.Combine(DataPaths.Root, "epl-output");
        if (Path.HasExtension(p)) return p;
        Directory.CreateDirectory(p);
        var safe = string.Concat(docName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(p, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{safe}.epl");
    }
    public SendResult SendLabel(AppSettings st, string docName, LabelRaster raster, int copies)
        => SendRawEpl(st, docName, Epl2Builder.BuildRasterJob(raster, st, copies));
    public SendResult SendRawEpl(AppSettings st, string docName, byte[] epl)
    {
        var t = Target(st, docName);
        File.WriteAllBytes(t, epl);
        return new SendResult(0, $"Saved EPL2 to {t}");
    }
}

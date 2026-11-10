using System.Text.Json.Serialization;

namespace LabelPrinter.Core;

public enum BackendKind { DirectUsb, RawEpl2, WindowsDriverGdi, SaveToFile }
public enum ScaleMode { ActualSize, FitToLabel }
public enum Anchor { TopLeft, Center }
public enum MediaKind { Gap, Continuous, BlackMark }

/// <summary>Everything the user can tune. Persisted as settings.json.</summary>
public class AppSettings
{
    // --- printer ---
    public string PrinterName { get; set; } = "";
    public BackendKind Backend { get; set; } = BackendKind.DirectUsb;
    /// <summary>Direct USB: device path to use; empty = auto-detect (Zebra-looking or only USB printer).</summary>
    public string DirectDevicePath { get; set; } = "";
    public string SaveToFilePath { get; set; } = "";

    // --- label geometry (hardware facts that are ASSUMED until verified; see docs/HARDWARE-RESEARCH.md) ---
    public double LabelWidthIn { get; set; } = 4.0;
    public double LabelHeightIn { get; set; } = 6.0;
    public int Dpi { get; set; } = 203;
    /// <summary>Widest printable area in dots. 832 (=4.09") is the LP/TLP 28xx-family figure; UNVERIFIED for the LP 2443.</summary>
    public int PrintableWidthDots { get; set; } = 832;
    public MediaKind Media { get; set; } = MediaKind.Gap;
    public int GapDots { get; set; } = 24;

    // --- placement ---
    public ScaleMode ScaleMode { get; set; } = ScaleMode.ActualSize;
    /// <summary>Find the label inside full-page printouts (Amazon etc.) and crop to it.</summary>
    public bool AutoCrop { get; set; } = true;
    /// <summary>Cropped labels are usually shrunk by the website's print layout, so scale them to fill the label.</summary>
    public bool FitCroppedLabels { get; set; } = true;
    public double ScalePercent { get; set; } = 100;
    public bool AutoRotate { get; set; } = true;
    public int RotationDegrees { get; set; } = 0;   // manual: 0/90/180/270 clockwise
    public Anchor Anchor { get; set; } = Anchor.TopLeft;
    public int OffsetXDots { get; set; } = 0;
    public int OffsetYDots { get; set; } = 0;

    // --- raster ---
    /// <summary>Render at N x the printer resolution, box-average down, then threshold (1 = off). 4 gives the most faithful bar widths.</summary>
    public int Supersample { get; set; } = 4;
    public int Threshold { get; set; } = 128;       // gray < threshold => black
    public bool InvertImage { get; set; } = false;  // negative print
    /// <summary>EPL2 GW bit sense. true = 0 bits print black (documented behaviour as best known; verify with Test Label).</summary>
    public bool GwZeroIsBlack { get; set; } = true;

    // --- printer commands (EPL2) ---
    public int Density { get; set; } = 8;           // D0..D15
    public int Speed { get; set; } = 2;             // S1..S4 (model dependent)
    public bool PrintDirectionRotated { get; set; } = false; // ZB instead of ZT

    // --- remote / housekeeping ---
    public bool RemoteEnabled { get; set; } = false;
    public int RemotePort { get; set; } = 47214;
    public bool RemoteHttps { get; set; } = false;
    public bool RequireApproval { get; set; } = false;
    public int UploadRetentionHours { get; set; } = 24;
    public bool StartWithWindows { get; set; } = false;
    /// <summary>Watch a folder (default: Downloads) and open new label PDFs automatically.</summary>
    public bool WatchEnabled { get; set; } = true;
    public string WatchFolder { get; set; } = "";
    /// <summary>Only open PDFs that look like shipping labels (barcode found, label-sized page, or a cut-line box).</summary>
    public bool WatchOnlyLabels { get; set; } = true;
    /// <summary>Ask GitHub (public repo) whether a newer version exists. Never auto-installs.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Paper-safe mode: false until the user confirms the small raw text test printed correctly. Until then only that test is sent.</summary>
    public bool PrinterVerified { get; set; } = false;
    /// <summary>Start every job with ESC EPL2 so a printer that slipped into Line Mode recovers by itself. Enabled only after the
    /// on-printer protection test proves it is harmless in page mode and recovers from line mode.</summary>
    public bool AutoHealLineMode { get; set; } = false;

    public int LabelWidthDots => (int)Math.Round(LabelWidthIn * Dpi);
    public int LabelHeightDots => (int)Math.Round(LabelHeightIn * Dpi);

    public AppSettings Clone() => System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
        System.Text.Json.JsonSerializer.Serialize(this, Json.Options), Json.Options)!;

    /// <summary>Clamp every value into a safe range (settings may come from a hand-edited file or the network).</summary>
    public void Sanitize()
    {
        LabelWidthIn = Math.Clamp(LabelWidthIn, 1, 4.5);
        LabelHeightIn = Math.Clamp(LabelHeightIn, 1, 12);
        Dpi = 203;
        PrintableWidthDots = Math.Clamp(PrintableWidthDots, 200, 1000);
        GapDots = Math.Clamp(GapDots, 0, 200);
        ScalePercent = Math.Clamp(ScalePercent, 10, 400);
        RotationDegrees = ((RotationDegrees % 360) + 360) % 360 / 90 * 90;
        OffsetXDots = Math.Clamp(OffsetXDots, -400, 400);
        OffsetYDots = Math.Clamp(OffsetYDots, -400, 400);
        Threshold = Math.Clamp(Threshold, 1, 254);
        Supersample = Supersample is 1 or 2 or 4 ? Supersample : 4;
        Density = Math.Clamp(Density, 0, 15);
        Speed = Math.Clamp(Speed, 1, 4);
        RemotePort = Math.Clamp(RemotePort, 1024, 65535);
        UploadRetentionHours = Math.Clamp(UploadRetentionHours, 1, 24 * 30);
        PrinterName ??= "";
        SaveToFilePath ??= "";
        DirectDevicePath ??= "";
        WatchFolder ??= "";
    }
}

public enum JobStatus { AwaitingApproval, Queued, Printing, Spooled, Completed, Failed, Cancelled, Rejected }

public class JobRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string DocId { get; set; } = "";
    public string DocName { get; set; } = "";
    public string Kind { get; set; } = "pdf";   // pdf | test | hello
    public int Page { get; set; }              // 0-based
    public int PageCount { get; set; }
    public int Copies { get; set; } = 1;
    public string Source { get; set; } = "Desktop";   // "Desktop" or paired phone name
    public string? DeviceId { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string Message { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public int SpoolerJobId { get; set; }
    public string PrinterName { get; set; } = "";
    [JsonIgnore] public bool IsFinal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Rejected;
}

public class PairedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Phone";
    public string TokenHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string LastIp { get; set; } = "";
}

public static class Json
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };
}

using System.Net;
using System.Security.Claims;
using LabelPrinter.Core.Diagnostics;
using LabelPrinter.Core.Imaging;
using LabelPrinter.Core.Jobs;
using LabelPrinter.Core.Pdf;
using LabelPrinter.Core.Printing;
using LabelPrinter.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LabelPrinter.Core.Web;

public static class WebHost
{
    static IResult J(object o, int status = 200) => Results.Text(System.Text.Json.JsonSerializer.Serialize(o, Json.Options), "application/json; charset=utf-8", null, status);
    static IResult Err(string m, int status = 400) => J(new { error = m }, status);

    static async Task<byte[]> ReadBody(HttpRequest r, int max)
    {
        if (r.ContentLength > max) throw new PdfException($"Upload too large (limit {max / 1048576} MB).");
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n;
        while ((n = await r.Body.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max) throw new PdfException($"Upload too large (limit {max / 1048576} MB).");
        }
        return ms.ToArray();
    }

    static async Task<T?> Body<T>(HttpRequest r) where T : class
    {
        try { return await System.Text.Json.JsonSerializer.DeserializeAsync<T>(r.Body, Json.Options); } catch { return null; }
    }

    record PrintReq(string DocId, List<int>? Pages, int Copies);
    record PairReq(string Code, string Name);

    // ------------------------------------------------------------------ builders
    public static WebApplication BuildDesktop(AppContext ctx, int port)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, port); o.Limits.MaxRequestBodySize = 20 * 1024 * 1024; o.AddServerHeader = false; });
        var app = b.Build();
        ctx.DesktopPort = port;

        app.Use(async (c, next) =>
        {
            Common(c);
            if (!IPAddress.IsLoopback(c.Connection.RemoteIpAddress ?? IPAddress.None)) { c.Response.StatusCode = 403; return; }
            var host = c.Request.Host.ToString();
            if (host != $"127.0.0.1:{port}" && host != $"localhost:{port}") { c.Response.StatusCode = 403; return; }   // DNS-rebinding guard
            if (c.Request.Method != "GET" && c.Request.Method != "HEAD")
            {
                var origin = c.Request.Headers.Origin.ToString();
                bool originOk = origin == "" || origin == $"http://127.0.0.1:{port}" || origin == $"http://localhost:{port}";
                if (!originOk || c.Request.Headers["X-ZLP"] != "1") { c.Response.StatusCode = 403; await c.Response.WriteAsync("Blocked: cross-site request."); return; }
            }
            await next();
        });

        app.MapGet("/", () => Assets.Serve("index.html"));
        app.MapGet("/app.js", () => Assets.Serve("app.js"));
        app.MapGet("/app.css", () => Assets.Serve("app.css"));
        app.MapGet("/qr.js", () => Assets.Serve("qrcode.js"));
        app.MapGet("/icon.svg", () => Assets.Serve("icon.svg"));

        var api = app.MapGroup("/api");

        api.MapGet("/state", (HttpContext c) =>
        {
            long since = long.TryParse(c.Request.Query["v"], out var v) ? v : -1;
            var s = ctx.Settings.Get();
            return J(new
            {
                version = ctx.Jobs.Version,
                settings = s,
                jobs = ctx.Jobs.Snapshot(),
                pendingApproval = ctx.Jobs.PendingApprovalCount(),
                remote = RemoteStatus(ctx),
                update = ctx.Updates.Available ? new { version = ctx.Updates.LatestVersion, page = ctx.Updates.PageUrl, download = ctx.Updates.DownloadUrl } : null,
                incoming = ctx.Incoming is { } inc ? new { docId = inc.docId, seq = inc.seq } : null,
                watch = new { active = ctx.Watcher.Active, folder = ctx.Watcher.Folder, error = ctx.Watcher.LastError },
                app = new { version = AppInfo.Version, name = AppInfo.Name, windows = SpoolerApi.IsAvailable },
            });
        });

        api.MapGet("/printers", () => J(new { usb = DirectUsb.List(), printers = SpoolerApi.GetPrinters(), ports = SpoolerApi.GetPorts().Where(p => p.Name.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)), defaultPrinter = SpoolerApi.DefaultPrinter() }));

        api.MapPut("/settings", async (HttpContext c) =>
        {
            var s = await Body<AppSettings>(c.Request);
            if (s == null) return Err("Invalid settings.");
            var old = ctx.Settings.Get();
            ctx.Settings.Set(s);
            ApplyStartup(s);
            if (old.WatchEnabled != s.WatchEnabled || old.WatchFolder != s.WatchFolder) ctx.Watcher.Restart();
            if (old.RemoteEnabled != s.RemoteEnabled || old.RemotePort != s.RemotePort || old.RemoteHttps != s.RemoteHttps) ctx.ApplyRemoteSetting();
            return J(new { settings = ctx.Settings.Get() });
        });
        api.MapPost("/settings/reset-calibration", () =>
        {
            var cur = ctx.Settings.Get(); var d = new AppSettings();
            d.PrinterName = cur.PrinterName; d.Backend = cur.Backend; d.SaveToFilePath = cur.SaveToFilePath;
            d.RemoteEnabled = cur.RemoteEnabled; d.RemotePort = cur.RemotePort; d.RemoteHttps = cur.RemoteHttps; d.RequireApproval = cur.RequireApproval;
            d.UploadRetentionHours = cur.UploadRetentionHours; d.StartWithWindows = cur.StartWithWindows;
            ctx.Settings.Set(d);
            return J(new { settings = ctx.Settings.Get() });
        });

        api.MapPost("/docs", async (HttpContext c) =>
        {
            try
            {
                var data = await ReadBody(c.Request, DocumentStore.MaxBytes);
                var name = Uri.UnescapeDataString(c.Request.Headers["X-File-Name"].ToString());
                return J(DocumentStore.View(ctx.Docs.Save(data, name, "Desktop"), ctx.Settings.Get().AutoCrop));
            }
            catch (PdfException e) { return Err(e.Message); }
        });
        api.MapGet("/docs/{id}", (string id) => { var d = ctx.Docs.Get(id); return d == null ? Err("Not found", 404) : J(DocumentStore.View(d, ctx.Settings.Get().AutoCrop)); });
        api.MapGet("/docs/{id}/preview/{page:int}.png", (string id, int page) => Preview(ctx, id, page, null));
        api.MapGet("/docs/{id}/info/{page:int}", (string id, int page) => PreviewInfo(ctx, id, page));
        api.MapGet("/docs/{id}/page/{page:int}.png", (string id, int page) => PageImage(ctx, id, page, null));
        api.MapPost("/docs/{id}/crop", (HttpContext c, string id) => SetCrop(ctx, c.Request, id, null));

        api.MapPost("/print", async (HttpContext c) =>
        {
            var r = await Body<PrintReq>(c.Request);
            if (r == null || string.IsNullOrEmpty(r.DocId)) return Err("Nothing to print.");
            try { return J(new { jobs = ctx.Jobs.Submit(r.DocId, r.Pages, r.Copies, "Desktop", null, false) }); }
            catch (PdfException e) { return Err(e.Message); }
        });
        api.MapPost("/jobs/{id}/cancel", (string id) => { var (ok, m) = ctx.Jobs.Cancel(id); return J(new { ok, message = m }); });
        api.MapPost("/jobs/{id}/approve", (string id) => J(new { ok = ctx.Jobs.Approve(id) }));
        api.MapPost("/jobs/{id}/reject", (string id) => J(new { ok = ctx.Jobs.Reject(id) }));
        api.MapPost("/jobs/{id}/reprint", (string id) => { try { return J(new { job = ctx.Jobs.Reprint(id, "Desktop", null, false) }); } catch (PdfException e) { return Err(e.Message); } });
        api.MapPost("/reprint-last", () => { try { var j = ctx.Jobs.ReprintLast("Desktop"); return j == null ? Err("There is no previous label to reprint.") : J(new { job = j }); } catch (PdfException e) { return Err(e.Message); } });
        api.MapPost("/jobs/clear", () => { ctx.Jobs.ClearFinished(); return J(new { ok = true }); });
        api.MapPost("/test/{kind}", (string kind) => kind is "label" or "hello" or "pagemode" or "heal1" or "heal2" ? J(new { job = ctx.Jobs.SubmitTest(kind) }) : Err("Unknown test."));

        api.MapGet("/diagnostics", (bool? pnp) => J(DiagnosticsService.Collect(ctx.Settings.Get(), pnp == true)));
        api.MapGet("/diagnostics/report", () => Results.Text(DiagnosticsService.ToText(DiagnosticsService.Collect(ctx.Settings.Get(), true)), "text/plain; charset=utf-8"));
        api.MapPost("/diagnostics/move-port", async (HttpContext c) =>
        {
            var r = await Body<Dictionary<string, string>>(c.Request);
            if (r == null || !r.TryGetValue("printer", out var p) || !r.TryGetValue("port", out var po)) return Err("printer and port required");
            return J(new { message = DiagnosticsService.MoveQueueToPort(p, po) });
        });
        api.MapPost("/spool/{id:int}/cancel", (int id) => { var s = ctx.Settings.Get(); return J(new { ok = SpoolerApi.CancelJob(s.PrinterName, id) }); });

        api.MapPost("/remote/pairing", () =>
        {
            var st = ctx.Settings.Get();
            if (!st.RemoteEnabled) return Err("Turn on phone printing first.");
            var (code, exp) = ctx.Devices.NewPairingCode();
            var urls = RemoteUrls(ctx, st).Select(u => $"{u}/m#pair={code}").ToList();
            return J(new { code, expiresUtc = exp, urls });
        });
        api.MapPost("/remote/pairing/cancel", () => { ctx.Devices.CancelPairing(); return J(new { ok = true }); });
        api.MapDelete("/remote/devices/{id}", (string id) => J(new { ok = ctx.Devices.Revoke(id) }));

        api.MapPost("/stop", () => J(new { message = ctx.Jobs.StopAll() }));
        api.MapPost("/rescue", () => J(new { message = ctx.Jobs.Rescue() }));
        api.MapPost("/autoheal/{on}", (bool on) => { ctx.Settings.Update(s => s.AutoHealLineMode = on); return J(new { settings = ctx.Settings.Get() }); });
        api.MapPost("/verify/{ok}", (bool ok) => { ctx.Settings.Update(s => s.PrinterVerified = ok); return J(new { settings = ctx.Settings.Get() }); });
        api.MapPost("/show", () => { ctx.OnShow?.Invoke(); return J(new { ok = true }); });
        api.MapPost("/quit", () => { Task.Run(async () => { await Task.Delay(300); ctx.OnQuit?.Invoke(); }); return J(new { ok = true }); });
        return app;
    }

    static object RemoteStatus(AppContext ctx)
    {
        var s = ctx.Settings.Get();
        return new
        {
            enabled = s.RemoteEnabled, running = ctx.Remote.Running, error = ctx.Remote.LastError, port = s.RemotePort, https = s.RemoteHttps,
            urls = RemoteUrls(ctx, s), devices = ctx.Devices.List(),
        };
    }
    public static List<string> RemoteUrls(AppContext ctx, AppSettings s)
    {
        var scheme = s.RemoteHttps ? "https" : "http";
        return NetInfo.LanAddresses().Select(ip => $"{scheme}://{ip}:{s.RemotePort}").ToList();
    }

    static void ApplyStartup(AppSettings s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return;
            if (s.StartWithWindows) k.SetValue("RetroLabel", $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue("RetroLabel", false);
            k.DeleteValue("ZebraLabelPrinter", false);   // v1 name
        }
        catch (Exception ex) { Log.Warn("Could not update startup entry: " + ex.Message); }
    }

    // ------------------------------------------------------------------ shared handlers
    static IResult Preview(AppContext ctx, string id, int page, string? ownerId)
    {
        try
        {
            if (!Authorize(ctx, id, ownerId, out var err)) return err!;
            var r = ctx.Previews.Get(ctx, id, page);
            return Results.Bytes(PngWriter.Encode(r), "image/png");
        }
        catch (PdfException e) { return Err(e.Message, 400); }
    }
    static IResult PreviewInfo(AppContext ctx, string id, int page, string? ownerId = null)
    {
        try
        {
            if (!Authorize(ctx, id, ownerId, out var err)) return err!;
            var r = ctx.Previews.Get(ctx, id, page);
            var s = ctx.Settings.Get();
            var view = DocumentStore.View(ctx.Docs.Get(id)!, s.AutoCrop);
            var bc = ctx.Checks.Get(ctx, id, page, r);
            return J(new { width = r.Width, height = r.Height, printableWidth = s.PrintableWidthDots, summary = r.Summary, warnings = r.Warnings, cropped = r.Cropped, labelCount = view.PageCount, autoCropped = view.AutoCropped, note = view.Labels[Math.Clamp(page, 0, view.Labels.Count - 1)].Note,
                barcodes = new { available = bc.Available, ok = bc.Missing.Count == 0, found = bc.Printed.Count, missing = bc.Missing, summary = bc.Summary } });
        }
        catch (PdfException e) { return Err(e.Message, 400); }
    }
    record CropReq(int Page, double X, double Y, double W, double H, string Mode);

    /// <summary>Full PDF page as a grayscale PNG (for drawing a crop box). maxPx limits the long side.</summary>
    static IResult PageImage(AppContext ctx, string id, int page, string? ownerId)
    {
        try
        {
            if (!Authorize(ctx, id, ownerId, out var err)) return err!;
            var d = ctx.Docs.Get(id)!;
            if (page < 0 || page >= d.PageSizesIn.Count) return Err("Page out of range.");
            double pw = d.PageSizesIn[page][0], ph = d.PageSizesIn[page][1];
            double scale = 1400 / Math.Max(pw, ph);
            int w = Math.Max(1, (int)(pw * scale)), h = Math.Max(1, (int)(ph * scale));
            using var pdf = ctx.Docs.Open(id);
            return Results.Bytes(PngWriter.EncodeGray(pdf.RenderGray(page, w, h), w, h), "image/png");
        }
        catch (PdfException e) { return Err(e.Message); }
    }

    static async Task<IResult> SetCrop(AppContext ctx, HttpRequest req, string id, string? ownerId)
    {
        if (!Authorize(ctx, id, ownerId, out var err)) return err!;
        var r = await Body<CropReq>(req);
        if (r == null) return Err("Invalid crop.");
        try
        {
            var region = r.Mode == "reset" ? null : new LabelRegion { Page = r.Page, X = r.X, Y = r.Y, W = r.W, H = r.H };
            var d = ctx.Docs.SetManual(id, region, r.Mode != "add");
            return J(DocumentStore.View(d, ctx.Settings.Get().AutoCrop));
        }
        catch (PdfException e) { return Err(e.Message); }
    }

    static bool Authorize(AppContext ctx, string id, string? ownerId, out IResult? err)
    {
        err = null;
        DocInfo? d;
        try { d = ctx.Docs.Get(id); } catch { d = null; }
        if (d == null) { err = Err("That PDF is no longer stored. Upload it again.", 404); return false; }
        if (ownerId != null && d.OwnerId != ownerId) { err = Err("Not found", 404); return false; }
        return true;
    }

    static void Common(HttpContext c)
    {
        var h = c.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Frame-Options"] = "DENY";
        h["Cache-Control"] = "no-store";
        h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob: data:; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    }

    // ------------------------------------------------------------------ remote (phone) server
    public static WebApplication BuildRemote(AppContext ctx, AppSettings s)
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = DocumentStore.MaxBytes + 1024;
            o.Limits.MaxConcurrentConnections = 40;
            o.AddServerHeader = false;
            o.Listen(IPAddress.Any, s.RemotePort, lo => { if (s.RemoteHttps) lo.UseHttps(SelfSignedCert.GetOrCreate()); });
        });
        var app = b.Build();

        var hits = new System.Collections.Concurrent.ConcurrentDictionary<string, (long window, int n)>();
        app.Use(async (c, next) =>
        {
            Common(c);
            var ip = c.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?";
            var win = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var cur = hits.AddOrUpdate(ip, (win, 1), (_, o) => o.window == win ? (win, o.n + 1) : (win, 1));
            if (cur.n > 240) { c.Response.StatusCode = 429; return; }
            await next();
        });

        // static PWA shell (public: contains no data)
        app.MapGet("/", () => Results.Redirect("/m"));
        app.MapGet("/m", () => Assets.Serve("mobile.html"));
        app.MapGet("/m/m.js", () => Assets.Serve("mobile.js"));
        app.MapGet("/m/m.css", () => Assets.Serve("mobile.css"));
        app.MapGet("/m/manifest.webmanifest", () => Assets.Serve("manifest.webmanifest"));
        app.MapGet("/m/sw.js", (HttpContext c) => { c.Response.Headers["Service-Worker-Allowed"] = "/m"; return Assets.Serve("sw.js"); });
        app.MapGet("/m/icon.svg", () => Assets.Serve("icon.svg"));
        app.MapGet("/m/icon-192.png", () => Assets.Serve("icon-192.png"));
        app.MapGet("/m/icon-512.png", () => Assets.Serve("icon-512.png"));

        var api = app.MapGroup("/api/m");
        api.MapPost("/pair", async (HttpContext c) =>
        {
            var r = await Body<PairReq>(c.Request);
            if (r == null || string.IsNullOrWhiteSpace(r.Code)) return Err("Enter the pairing code.");
            var (dev, token, error) = ctx.Devices.Pair(r.Code, r.Name, c.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?");
            if (dev == null) return Err(error!, 403);
            Log.Info($"Phone paired: {dev.Name} ({dev.Id})");
            return J(new { token, deviceId = dev.Id, name = dev.Name });
        });

        // everything below needs a bearer token
        var auth = api.MapGroup("").AddEndpointFilter(async (inv, next) =>
        {
            var c = inv.HttpContext;
            if (!ctx.Settings.Get().RemoteEnabled) return Err("Phone printing is turned off on the PC.", 403);
            var h = c.Request.Headers.Authorization.ToString();
            var tok = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : null;
            var dev = ctx.Devices.Authenticate(tok, c.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "?");
            if (dev == null) return Err("This phone is not paired (or was revoked). Scan the QR code on the PC again.", 401);
            c.Items["dev"] = dev;
            return await next(inv);
        });
        static PairedDevice Dev(HttpContext c) => (PairedDevice)c.Items["dev"]!;

        auth.MapGet("/state", (HttpContext c) =>
        {
            var d = Dev(c); var s = ctx.Settings.Get();
            var pr = SpoolerApi.IsAvailable && !string.IsNullOrEmpty(s.PrinterName) ? SpoolerApi.GetPrinter(s.PrinterName) : null;
            if (s.Backend == BackendKind.DirectUsb)
            {
                UsbPrinterDevice? u = null;
                try { u = DirectUsb.Resolve(s.DirectDevicePath); } catch { }
                var jobsU = ctx.Jobs.Snapshot().Where(j => j.DeviceId == d.Id).Take(40).ToList();
                return J(new
                {
                    device = new { d.Id, d.Name },
                    printer = new { name = u?.Name ?? "", status = u != null ? "ready (USB)" : "not connected", offline = u == null },
                    requireApproval = s.RequireApproval,
                    label = new { widthIn = s.LabelWidthIn, heightIn = s.LabelHeightIn },
                    jobs = jobsU,
                    version = ctx.Jobs.Version,
                });
            }
            var jobs = ctx.Jobs.Snapshot().Where(j => j.DeviceId == d.Id).Take(40).ToList();
            return J(new
            {
                device = new { d.Id, d.Name },
                printer = new { name = s.PrinterName, status = pr?.StatusText ?? (SpoolerApi.IsAvailable ? "not found" : "n/a"), offline = pr?.Offline ?? false },
                requireApproval = s.RequireApproval,
                label = new { widthIn = s.LabelWidthIn, heightIn = s.LabelHeightIn },
                jobs,
                version = ctx.Jobs.Version,
            });
        });
        auth.MapPost("/docs", async (HttpContext c) =>
        {
            var d = Dev(c);
            try
            {
                var data = await ReadBody(c.Request, DocumentStore.MaxBytes);
                var name = Uri.UnescapeDataString(c.Request.Headers["X-File-Name"].ToString());
                return J(DocumentStore.View(ctx.Docs.Save(data, name, d.Name, d.Id), ctx.Settings.Get().AutoCrop));
            }
            catch (PdfException e) { return Err(e.Message); }
        });
        // iPhone Shortcut / share-sheet: upload + auto-crop + barcode check + print in one call. Plain-text reply for the notification.
        auth.MapPost("/quickprint", async (HttpContext c) =>
        {
            var d = Dev(c);
            try
            {
                var data = await ReadBody(c.Request, DocumentStore.MaxBytes);
                var name = Uri.UnescapeDataString(c.Request.Headers["X-File-Name"].ToString());
                if (string.IsNullOrWhiteSpace(name)) name = "shared-label.pdf";
                int copies = int.TryParse(c.Request.Query["copies"], out var cp) ? Math.Clamp(cp, 1, 20) : 1;
                var st = ctx.Settings.Get();
                var view = DocumentStore.View(ctx.Docs.Save(data, name, d.Name, d.Id), st.AutoCrop);
                if (view.PageCount > 10) return Results.Text($"That PDF has {view.PageCount} labels. Open the Label Printer page to pick which ones.", "text/plain", null, 400);
                for (int i = 0; i < view.PageCount; i++)
                {
                    var bc = ctx.Checks.Get(ctx, view.Id, i, ctx.Previews.Get(ctx, view.Id, i));
                    if (bc.Missing.Count > 0) return Results.Text($"NOT printed: a barcode on label {i + 1} would not scan. Open the Label Printer page to check.", "text/plain", null, 422);
                }
                var jobs = ctx.Jobs.Submit(view.Id, null, copies, d.Name, d.Id, st.RequireApproval);
                var msg = st.RequireApproval ? $"Sent {jobs.Count} label(s). Waiting for approval on the PC." : $"Printing {jobs.Count} label(s) on the LP 2443.";
                return Results.Text(msg, "text/plain");
            }
            catch (PdfException e) { return Results.Text("Not printed: " + e.Message, "text/plain", null, 400); }
        });
        auth.MapGet("/docs/{id}/preview/{page:int}.png", (HttpContext c, string id, int page) => Preview(ctx, id, page, Dev(c).Id));
        auth.MapGet("/docs/{id}/info/{page:int}", (HttpContext c, string id, int page) => PreviewInfo(ctx, id, page, Dev(c).Id));
        auth.MapGet("/docs/{id}/page/{page:int}.png", (HttpContext c, string id, int page) => PageImage(ctx, id, page, Dev(c).Id));
        auth.MapPost("/docs/{id}/crop", (HttpContext c, string id) => SetCrop(ctx, c.Request, id, Dev(c).Id));
        auth.MapPost("/print", async (HttpContext c) =>
        {
            var d = Dev(c);
            var r = await Body<PrintReq>(c.Request);
            if (r == null || string.IsNullOrEmpty(r.DocId)) return Err("Nothing to print.");
            if (!Authorize(ctx, r.DocId, d.Id, out var err)) return err!;
            try { return J(new { jobs = ctx.Jobs.Submit(r.DocId, r.Pages, r.Copies, d.Name, d.Id, ctx.Settings.Get().RequireApproval) }); }
            catch (PdfException e) { return Err(e.Message); }
        });
        auth.MapPost("/jobs/{id}/cancel", (HttpContext c, string id) => { var (ok, m) = ctx.Jobs.Cancel(id, Dev(c).Id); return J(new { ok, message = m }); });
        auth.MapPost("/jobs/{id}/reprint", (HttpContext c, string id) =>
        {
            var d = Dev(c);
            var j = ctx.Jobs.Get(id);
            if (j == null || j.DeviceId != d.Id) return Err("Job not found.", 404);
            try { return J(new { job = ctx.Jobs.Reprint(id, d.Name, d.Id, ctx.Settings.Get().RequireApproval) }); }
            catch (PdfException e) { return Err(e.Message); }
        });
        return app;
    }
}

/// <summary>Starts/stops the phone-facing listener. It exposes ONLY the paired-phone API, never the desktop API.</summary>
public class RemoteServer
{
    readonly AppContext _ctx;
    WebApplication? _app;
    readonly object _l = new();
    public bool Running => _app != null;
    public string? LastError { get; private set; }
    public RemoteServer(AppContext ctx) { _ctx = ctx; }

    public void Restart() { Stop(); Start(); }
    public void Start()
    {
        lock (_l)
        {
            if (_app != null) return;
            try
            {
                var s = _ctx.Settings.Get();
                _app = WebHost.BuildRemote(_ctx, s);
                _app.StartAsync().GetAwaiter().GetResult();
                LastError = null;
                Log.Info($"Phone printing listening on port {s.RemotePort} ({(s.RemoteHttps ? "HTTPS" : "HTTP")})");
            }
            catch (Exception ex)
            {
                _app = null;
                LastError = ex.InnerException?.Message ?? ex.Message;
                Log.Error("Could not start phone printing", ex);
            }
        }
    }
    public void Stop()
    {
        lock (_l)
        {
            if (_app == null) return;
            try { _app.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _app = null; Log.Info("Phone printing stopped");
        }
    }
}

using LabelPrinter.Core.Epl;
using LabelPrinter.Core.Imaging;
using LabelPrinter.Core.Pdf;
using LabelPrinter.Core.Printing;

namespace LabelPrinter.Core.Jobs;

public class JobManager : IDisposable
{
    readonly SettingsStore _settings;
    readonly DocumentStore _docs;
    readonly object _l = new();
    readonly List<JobRecord> _jobs;
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _cts = new();
    readonly Thread _worker;
    readonly Timer _monitor;
    long _version;
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Test hook: replaces the real backend selection.</summary>
    public Func<AppSettings, IPrintBackend>? BackendOverride { get; set; }

    public JobManager(SettingsStore settings, DocumentStore docs)
    {
        _settings = settings; _docs = docs;
        _jobs = JsonFile.Load(DataPaths.JobsFile, () => new List<JobRecord>());
        foreach (var j in _jobs)
        {
            if (j.Status == JobStatus.Printing) { j.Status = JobStatus.Failed; j.Message = "The app was closed while this label was being sent. Reprint it."; }
        }
        _worker = new Thread(Run) { IsBackground = true, Name = "print-worker" };
        _worker.Start();
        foreach (var _ in _jobs.Where(j => j.Status == JobStatus.Queued)) _signal.Release();
        _monitor = new Timer(_ => MonitorTick(), null, 2000, 2000);
    }

    // ---------- queries ----------
    public List<JobRecord> Snapshot() { lock (_l) return _jobs.OrderByDescending(j => j.CreatedUtc).Take(200).Select(Copy).ToList(); }
    public JobRecord? Get(string id) { lock (_l) { var j = _jobs.FirstOrDefault(x => x.Id == id); return j == null ? null : Copy(j); } }
    static JobRecord Copy(JobRecord j) => (JobRecord)System.Text.Json.JsonSerializer.Deserialize<JobRecord>(System.Text.Json.JsonSerializer.Serialize(j, Json.Options), Json.Options)!;
    public int PendingApprovalCount() { lock (_l) return _jobs.Count(j => j.Status == JobStatus.AwaitingApproval); }
    public ISet<string> DocsInUse() { lock (_l) return _jobs.Where(j => !j.IsFinal).Select(j => j.DocId).ToHashSet(); }

    // ---------- commands ----------
    public List<JobRecord> Submit(string docId, IEnumerable<int>? pages, int copies, string source, string? deviceId, bool needsApproval)
    {
        var raw = _docs.Get(docId) ?? throw new PdfException("That PDF is no longer stored. Upload it again.");
        var doc = DocumentStore.View(raw, _settings.Get().AutoCrop);
        var pl = (pages == null || !pages.Any() ? Enumerable.Range(0, doc.PageCount) : pages).Distinct().OrderBy(p => p).ToList();
        if (pl.Any(p => p < 0 || p >= doc.PageCount)) throw new PdfException("Page number out of range.");
        copies = Math.Clamp(copies, 1, 99);
        var printer = _settings.Get().PrinterName;
        var made = new List<JobRecord>();
        lock (_l)
        {
            foreach (var p in pl)
            {
                var j = new JobRecord
                {
                    DocId = docId, DocName = doc.Name, Page = p, PageCount = doc.PageCount, Copies = copies, Source = source, DeviceId = deviceId,
                    PrinterName = printer,
                    Status = needsApproval ? JobStatus.AwaitingApproval : JobStatus.Queued,
                    Message = needsApproval ? "Waiting for approval on the PC" : "Waiting to print"
                };
                _jobs.Add(j); made.Add(Copy(j));
            }
            Save();
        }
        if (!needsApproval) foreach (var _ in made) _signal.Release();
        return made;
    }

    public JobRecord SubmitTest(string kind)
    {
        var j = new JobRecord { Kind = kind is "hello" or "pagemode" or "heal1" or "heal2" ? kind : "test", DocName = kind switch { "hello" => "Raw EPL2 text test", "pagemode" => "Switch printer to label mode (ESC EPL2)", "heal1" => "Protection test 1/2", "heal2" => "Protection test 2/2", _ => "Test label" }, Copies = 1, PrinterName = _settings.Get().PrinterName, Message = "Waiting to print" };
        lock (_l) { _jobs.Add(j); Save(); }
        _signal.Release();
        return Copy(j);
    }

    public bool Approve(string id)
    {
        lock (_l)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id && x.Status == JobStatus.AwaitingApproval);
            if (j == null) return false;
            Set(j, JobStatus.Queued, "Approved, waiting to print"); Save();
        }
        _signal.Release(); return true;
    }
    public bool Reject(string id)
    {
        lock (_l)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id && x.Status == JobStatus.AwaitingApproval);
            if (j == null) return false;
            Set(j, JobStatus.Rejected, "Rejected on the PC"); Save(); return true;
        }
    }

    public (bool ok, string message) Cancel(string id, string? onlyDeviceId = null)
    {
        int spool = 0; string printer = "";
        lock (_l)
        {
            var j = _jobs.FirstOrDefault(x => x.Id == id);
            if (j == null) return (false, "Job not found.");
            if (onlyDeviceId != null && j.DeviceId != onlyDeviceId) return (false, "Job not found.");
            switch (j.Status)
            {
                case JobStatus.Queued:
                case JobStatus.AwaitingApproval:
                    Set(j, JobStatus.Cancelled, "Cancelled before printing"); Save(); return (true, "Cancelled.");
                case JobStatus.Printing:
                    return (false, "This label is being sent to the printer right now and can no longer be cancelled here. Check the Windows print queue.");
                case JobStatus.Spooled:
                    spool = j.SpoolerJobId; printer = j.PrinterName; break;
                default: return (false, "This job already finished.");
            }
        }
        if (spool > 0 && SpoolerApi.CancelJob(printer, spool))
        {
            lock (_l) { var j = _jobs.First(x => x.Id == id); if (!j.IsFinal) { Set(j, JobStatus.Cancelled, "Removed from the Windows print queue"); Save(); } }
            return (true, "Removed from the Windows print queue.");
        }
        return (false, "Windows has already passed this job to the printer (or the queue entry is gone).");
    }

    public JobRecord Reprint(string id, string source, string? deviceId, bool needsApproval)
    {
        JobRecord src;
        lock (_l) src = Copy(_jobs.FirstOrDefault(x => x.Id == id) ?? throw new PdfException("Job not found."));
        if (src.Kind != "pdf") return SubmitTest(src.Kind);
        return Submit(src.DocId, new[] { src.Page }, src.Copies, source, deviceId, needsApproval).First();
    }
    public JobRecord? ReprintLast(string source)
    {
        JobRecord? last;
        lock (_l) last = _jobs.Where(j => j.Kind == "pdf" && j.Status is JobStatus.Completed or JobStatus.Spooled or JobStatus.Failed).OrderByDescending(j => j.CreatedUtc).FirstOrDefault();
        return last == null ? null : Reprint(last.Id, source, null, false);
    }

    /// <summary>STOP: cancel everything not yet sent, abort the transfer in progress, and pull our jobs out of the Windows queue.</summary>
    public string StopAll()
    {
        int cancelled = 0;
        var spooled = new List<JobRecord>();
        lock (_l)
        {
            foreach (var j in _jobs.Where(j => j.Status is JobStatus.Queued or JobStatus.AwaitingApproval)) { Set(j, JobStatus.Cancelled, "Stopped"); cancelled++; }
            spooled = _jobs.Where(j => j.Status == JobStatus.Spooled).Select(Copy).ToList();
            Save();
        }
        bool aborted = DirectUsb.AbortCurrent();
        foreach (var j in spooled)
            if (SpoolerApi.CancelJob(j.PrinterName, j.SpoolerJobId)) lock (_l) { var t = _jobs.First(x => x.Id == j.Id); Set(t, JobStatus.Cancelled, "Removed from the Windows print queue"); cancelled++; }
        lock (_l) Save();
        Log.Info($"STOP pressed: {cancelled} job(s) cancelled, transfer aborted: {aborted}");
        return $"Stopped. {cancelled} waiting job(s) cancelled{(aborted ? " and the transfer in progress was cut off" : "")}. If the printer is still feeding, turn it off and on: that clears data it already received.";
    }

    /// <summary>"Printing garbage?" button: stop everything, then send the switch-back-to-label-mode command.</summary>
    public string Rescue()
    {
        var m = StopAll();
        Thread.Sleep(800);
        SubmitTest("pagemode");
        return "Stopped and sent the 'switch to label mode' command. Now turn the printer OFF and ON, then press Diagnostics → 1. Raw text test.";
    }

    public void ClearFinished()
    {
        lock (_l) { _jobs.RemoveAll(j => j.IsFinal); Save(); }
    }

    // ---------- worker ----------
    void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { _signal.Wait(_cts.Token); } catch (OperationCanceledException) { return; }
            JobRecord? job;
            lock (_l)
            {
                var j = _jobs.Where(x => x.Status == JobStatus.Queued).OrderBy(x => x.CreatedUtc).FirstOrDefault();
                if (j != null) Set(j, JobStatus.Printing, "Rendering…");
                job = j == null ? null : Copy(j);
            }
            if (job == null) continue;
            try { Process(job); }
            catch (Exception ex)
            {
                var msg = ex switch { PrintException pe => pe.Full, PdfException pd => pd.Message, _ => "Unexpected error: " + ex.Message + " (see logs)" };
                if (ex is not (PrintException or PdfException)) Log.Error("Job failed", ex); else Log.Warn($"Job {job.Id} failed: {msg}");
                lock (_l) { var j = _jobs.First(x => x.Id == job.Id); Set(j, JobStatus.Failed, msg); Save(); }
            }
        }
    }

    void Process(JobRecord job)
    {
        var st = _settings.Get();
        bool queueBased = st.Backend is BackendKind.RawEpl2 or BackendKind.WindowsDriverGdi;
        if (queueBased && string.IsNullOrWhiteSpace(st.PrinterName))
            throw new PrintException("No printer is selected.", "Open Settings and choose your Zebra printer queue.");
        string note;
        if (queueBased && !st.PrinterName.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase) && SpoolerApi.IsAvailable)
        {
            var p = SpoolerApi.GetPrinter(st.PrinterName);
            if (p == null) throw new PrintException($"Printer '{st.PrinterName}' is not installed in Windows.", "Open Diagnostics, check the queue list, then reselect it in Settings.");
        }
        bool toFile = st.Backend == BackendKind.SaveToFile || st.PrinterName.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase);
        if (!st.PrinterVerified && !toFile && job.Kind is not ("hello" or "pagemode" or "heal1" or "heal2") && BackendOverride == null)
            throw new PrintException("Paper-safe mode: nothing was sent.", "First go to Diagnostics → '1. Raw text test'. When it prints one clean label saying RAW EPL2 PATH OK, click 'Yes, it printed correctly'.");
        var backend = BackendOverride?.Invoke(st) ?? BackendFactory.For(st);
        SendResult res;
        if (job.Kind == "test") res = backend.SendRawEpl(st, "Test label", Epl2Builder.BuildTestLabel(st));
        else if (job.Kind == "pagemode") res = backend.SendRawEpl(st, "Exit line mode", Epl2Builder.BuildExitLineMode());
        else if (job.Kind is "heal1" or "heal2")
        {
            // Protection test: label 1 = ESC EPL2 prefix sent while in label mode (must be harmless);
            // label 2 = put the printer INTO line mode, then the same prefixed job (must recover by itself).
            var t = st.Clone(); t.AutoHealLineMode = true;
            if (job.Kind == "heal2") { backend.SendRawEpl(st, "Enter line mode", Epl2Builder.BuildEnterLineMode()); Thread.Sleep(2500); }
            res = backend.SendRawEpl(t, job.DocName, Epl2Builder.BuildHelloJob(t, job.Kind == "heal1" ? "PROTECTION TEST 1 OK" : "PROTECTION TEST 2 OK"));
        }
        else if (job.Kind == "hello") res = backend.SendRawEpl(st, "Raw EPL2 test", Epl2Builder.BuildHelloJob(st));
        else
        {
            LabelRaster raster;
            var view = DocumentStore.View(_docs.Get(job.DocId) ?? throw new PdfException("The PDF is no longer stored (it was deleted after the retention period). Upload it again."), st.AutoCrop);
            if (job.Page >= view.Labels.Count) throw new PdfException("That label no longer exists in this PDF (auto-crop setting changed?). Re-open the PDF.");
            using (var pdf = _docs.Open(job.DocId)) raster = LabelComposer.Compose(pdf, view.Labels[job.Page], st);
            lock (_l) { var j = _jobs.First(x => x.Id == job.Id); if (j.Status == JobStatus.Cancelled) return; Set(j, JobStatus.Printing, "Sending to printer…"); }
            res = backend.SendLabel(st, $"{job.DocName} label {job.Page + 1}", raster, job.Copies);
        }
        note = res.Note;
        lock (_l)
        {
            var j = _jobs.First(x => x.Id == job.Id);
            j.SpoolerJobId = res.SpoolerJobId; j.PrinterName = st.PrinterName;
            if (res.SpoolerJobId > 0) Set(j, JobStatus.Spooled, "In the Windows print queue: " + note);
            else Set(j, JobStatus.Completed, note);
            Save();
        }
    }

    void MonitorTick()
    {
        try
        {
            List<JobRecord> spooled;
            lock (_l) spooled = _jobs.Where(j => j.Status == JobStatus.Spooled).Select(Copy).ToList();
            if (spooled.Count > 0 && SpoolerApi.IsAvailable)
            {
                foreach (var g in spooled.GroupBy(j => j.PrinterName))
                {
                    var q = SpoolerApi.GetJobs(g.Key).ToDictionary(x => x.Id);
                    var pr = SpoolerApi.GetPrinter(g.Key);
                    foreach (var j in g)
                    {
                        lock (_l)
                        {
                            var t = _jobs.FirstOrDefault(x => x.Id == j.Id);
                            if (t == null || t.Status != JobStatus.Spooled) continue;
                            if (!q.TryGetValue(j.SpoolerJobId, out var sj))
                                Set(t, JobStatus.Completed, "Windows delivered the data to the printer port. (Windows cannot confirm the label physically printed.)");
                            else
                            {
                                var extra = pr is { Offline: true } ? " Printer is OFFLINE: check power and the USB adapter." : "";
                                var m = $"In the Windows queue ({sj.StatusText}).{extra}";
                                if (t.Message != m) Set(t, JobStatus.Spooled, m);
                            }
                            Save();
                        }
                    }
                }
            }
            // housekeeping
            var hours = _settings.Get().UploadRetentionHours;
            if ((DateTime.UtcNow.Minute % 5 == 0 || _lastPurge == default) && DateTime.UtcNow - _lastPurge > TimeSpan.FromMinutes(4))
            {
                _lastPurge = DateTime.UtcNow;
                _docs.Purge(TimeSpan.FromHours(hours), DocsInUse());
            }
        }
        catch (Exception ex) { Log.Error("Monitor tick failed", ex); }
    }
    DateTime _lastPurge;

    void Set(JobRecord j, JobStatus s, string msg) { j.Status = s; j.Message = msg; j.UpdatedUtc = DateTime.UtcNow; Interlocked.Increment(ref _version); }
    void Save()
    {
        if (_jobs.Count > 300) _jobs.RemoveAll(x => x.IsFinal && _jobs.Count(y => y.CreatedUtc > x.CreatedUtc) >= 300);
        try { JsonFile.Save(DataPaths.JobsFile, _jobs); } catch (Exception ex) { Log.Error("Could not save jobs", ex); }
        Interlocked.Increment(ref _version);
    }

    public void Dispose() { _cts.Cancel(); _monitor.Dispose(); }
}

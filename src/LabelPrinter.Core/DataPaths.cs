namespace LabelPrinter.Core;

public static class DataPaths
{
    static string? _root;
    public static string Root
    {
        get
        {
            if (_root != null) return _root;
            var env = Environment.GetEnvironmentVariable("ZLP_DATA_DIR");
            _root = !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RetroLabel");
            if (string.IsNullOrWhiteSpace(env)) MigrateFromOldName(_root);
            Directory.CreateDirectory(_root);
            return _root;
        }
        set { _root = value; Directory.CreateDirectory(value); }
    }
    /// <summary>v1 was called "Zebra Label Printer" and kept its data in %LOCALAPPDATA%\ZebraLabelPrinter. Copy it once.</summary>
    static void MigrateFromOldName(string newRoot)
    {
        try
        {
            var old = Path.Combine(Path.GetDirectoryName(newRoot)!, "ZebraLabelPrinter");
            if (Directory.Exists(newRoot) || !Directory.Exists(old)) return;
            foreach (var f in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(old, f);
                if (rel.StartsWith("webview", StringComparison.OrdinalIgnoreCase)) continue;   // browser cache: not needed
                var dest = Path.Combine(newRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, false);
            }
        }
        catch { /* best effort: worst case the user re-picks settings */ }
    }

    public static string Uploads { get { var p = Path.Combine(Root, "uploads"); Directory.CreateDirectory(p); return p; } }
    public static string Logs { get { var p = Path.Combine(Root, "logs"); Directory.CreateDirectory(p); return p; } }
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string JobsFile => Path.Combine(Root, "jobs.json");
    public static string DevicesFile => Path.Combine(Root, "devices.json");
}

public static class Log
{
    static readonly object _l = new();
    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Error(string m, Exception? ex = null) => Write("ERROR", ex == null ? m : $"{m} :: {ex}");
    static void Write(string lvl, string m)
    {
        try
        {
            lock (_l)
            {
                var f = Path.Combine(DataPaths.Logs, "app.log");
                if (File.Exists(f) && new FileInfo(f).Length > 1_000_000) File.Move(f, f + ".old", true);
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {lvl} {m}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}

/// <summary>Atomic JSON persistence.</summary>
public static class JsonFile
{
    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (File.Exists(path))
                return System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options) ?? fallback();
        }
        catch (Exception ex) { Log.Error($"Could not read {path}; using defaults", ex); }
        return fallback();
    }
    public static T Clone<T>(T v) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(v, Json.Options), Json.Options)!;
    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(value, Json.Options));
        File.Move(tmp, path, true);
    }
}

public class SettingsStore
{
    readonly object _l = new();
    AppSettings _cur;
    public SettingsStore()
    {
        _cur = JsonFile.Load(DataPaths.SettingsFile, () => new AppSettings());
        _cur.Sanitize();
    }
    public AppSettings Get() { lock (_l) return _cur.Clone(); }
    public void Set(AppSettings s) { s.Sanitize(); lock (_l) { _cur = s; JsonFile.Save(DataPaths.SettingsFile, _cur); } }
    public void Update(Action<AppSettings> mut) { lock (_l) { var c = _cur.Clone(); mut(c); c.Sanitize(); _cur = c; JsonFile.Save(DataPaths.SettingsFile, _cur); } }
    public void Reset() => Set(new AppSettings());
}

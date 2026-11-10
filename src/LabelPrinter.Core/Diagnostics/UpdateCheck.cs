using System.Text.Json;

namespace LabelPrinter.Core.Diagnostics;

/// <summary>Checks the public GitHub repo's latest release (at start-up and every 12 h). Only tells the user; never installs anything.</summary>
public class UpdateCheck : IDisposable
{
    readonly SettingsStore _settings;
    readonly Timer _timer;
    public string? LatestVersion { get; private set; }
    public string? PageUrl { get; private set; }
    public string? DownloadUrl { get; private set; }
    public bool Available => LatestVersion != null && IsNewer(LatestVersion, AppInfo.Version);

    public UpdateCheck(SettingsStore settings)
    {
        _settings = settings;
        _timer = new Timer(_ => _ = RunAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(12));
    }

    public static bool IsNewer(string remote, string local)
    {
        static Version Parse(string v) { v = v.TrimStart('v', 'V'); var core = v.Split('-', '+')[0]; return Version.TryParse(core, out var x) ? x : new Version(0, 0); }
        return Parse(remote) > Parse(local);
    }

    public async Task RunAsync()
    {
        if (!_settings.Get().CheckForUpdates) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RetroLabel/" + AppInfo.Version);
            var json = await http.GetStringAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            LatestVersion = root.GetProperty("tag_name").GetString();
            PageUrl = root.GetProperty("html_url").GetString();
            DownloadUrl = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) DownloadUrl = a.GetProperty("browser_download_url").GetString();
            }
        }
        catch (Exception ex) { Log.Info("Update check skipped: " + ex.Message); }
    }

    public void Dispose() => _timer.Dispose();
}

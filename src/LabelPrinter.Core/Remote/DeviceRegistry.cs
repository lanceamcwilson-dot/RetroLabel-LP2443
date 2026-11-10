using System.Security.Cryptography;

namespace LabelPrinter.Core.Remote;

/// <summary>Pairing codes + long-lived device tokens. Only SHA-256 hashes of tokens are stored.</summary>
public class DeviceRegistry
{
    readonly object _l = new();
    readonly List<PairedDevice> _devices;
    string? _code; DateTime _codeExpires;
    readonly Dictionary<string, (int fails, DateTime until)> _fails = new();
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public DeviceRegistry() { _devices = JsonFile.Load(DataPaths.DevicesFile, () => new List<PairedDevice>()); }

    public (string code, DateTime expires) NewPairingCode()
    {
        lock (_l)
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var sb = new char[10];
            for (int i = 0; i < sb.Length; i++) sb[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            _code = new string(sb); _codeExpires = DateTime.UtcNow + CodeLifetime;
            return (_code, _codeExpires);
        }
    }
    public void CancelPairing() { lock (_l) _code = null; }

    /// <summary>Returns the new device + its plaintext token (shown once), or an error string.</summary>
    public (PairedDevice? dev, string? token, string? error) Pair(string code, string name, string ip)
    {
        lock (_l)
        {
            if (_fails.TryGetValue(ip, out var f) && f.until > DateTime.UtcNow) return (null, null, "Too many wrong codes. Wait a minute and try again.");
            bool ok = _code != null && DateTime.UtcNow < _codeExpires &&
                CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(code.Trim().ToUpperInvariant().PadRight(32)), System.Text.Encoding.ASCII.GetBytes(_code.PadRight(32)));
            if (!ok)
            {
                var n = (_fails.TryGetValue(ip, out var ff) ? ff.fails : 0) + 1;
                _fails[ip] = (n, n >= 5 ? DateTime.UtcNow.AddMinutes(1) : DateTime.MinValue);
                return (null, null, "That pairing code is wrong or expired. Generate a new QR code on the PC.");
            }
            _code = null; _fails.Remove(ip);
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            name = new string((name ?? "Phone").Where(c => !char.IsControl(c)).Take(40).ToArray());
            var dev = new PairedDevice { Name = string.IsNullOrWhiteSpace(name) ? "Phone" : name, TokenHash = Hash(token), LastIp = ip };
            _devices.Add(dev); Save();
            return (Clone(dev), token, null);
        }
    }

    public PairedDevice? Authenticate(string? token, string ip)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        var h = Hash(token);
        lock (_l)
        {
            foreach (var d in _devices)
                if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(d.TokenHash), System.Text.Encoding.ASCII.GetBytes(h)))
                {
                    if (DateTime.UtcNow - d.LastSeenUtc > TimeSpan.FromMinutes(1) || d.LastIp != ip) { d.LastSeenUtc = DateTime.UtcNow; d.LastIp = ip; Save(); }
                    return Clone(d);
                }
        }
        return null;
    }

    public List<PairedDevice> List() { lock (_l) return _devices.Select(Clone).ToList(); }
    public bool Revoke(string id) { lock (_l) { var n = _devices.RemoveAll(d => d.Id == id); if (n > 0) Save(); return n > 0; } }
    public void RevokeAll() { lock (_l) { _devices.Clear(); Save(); } }
    static string Hash(string t) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(t)));
    static PairedDevice Clone(PairedDevice d) => new() { Id = d.Id, Name = d.Name, CreatedUtc = d.CreatedUtc, LastSeenUtc = d.LastSeenUtc, LastIp = d.LastIp, TokenHash = "" };
    void Save() { try { JsonFile.Save(DataPaths.DevicesFile, _devices); } catch (Exception ex) { Log.Error("Could not save devices", ex); } }
}

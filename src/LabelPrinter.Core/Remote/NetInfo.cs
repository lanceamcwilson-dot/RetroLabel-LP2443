using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LabelPrinter.Core.Remote;

public static class NetInfo
{
    /// <summary>IPv4 addresses of this PC: private ranges first, interfaces with a gateway first (VPN/virtual adapters rank last).</summary>
    public static List<string> LanAddresses()
    {
        var res = new List<(string ip, int rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                bool virt = ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || ni.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase) || ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || ni.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase);
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    bool priv = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
                    bool linkLocal = b[0] == 169 && b[1] == 254;
                    if (linkLocal) continue;
                    // private ranges first; any other routable IPv4 is kept as a last resort (unusual subnets)
                    res.Add((ua.Address.ToString(), (priv ? 0 : 10) + (gw ? 0 : 2) + (virt ? 1 : 0)));
                }
            }
        }
        catch (Exception ex) { Log.Warn("LAN address lookup failed: " + ex.Message); }
        return res.OrderBy(r => r.rank).Select(r => r.ip).Distinct().ToList();
    }
}

public static class SelfSignedCert
{
    public static X509Certificate2 GetOrCreate()
    {
        var path = Path.Combine(DataPaths.Root, "remote-cert.pfx");
        try
        {
            if (File.Exists(path))
            {
                var c = new X509Certificate2(File.ReadAllBytes(path), (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
                if (c.NotAfter > DateTime.Now.AddDays(30)) return c;
            }
        }
        catch (Exception ex) { Log.Warn("Existing remote certificate unusable, regenerating: " + ex.Message); }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=RetroLabel LP2443 (local)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName); san.AddDnsName("localhost");
        foreach (var ip in NetInfo.LanAddresses()) san.AddIpAddress(IPAddress.Parse(ip));
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(800));
        var pfx = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, pfx);
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }
}

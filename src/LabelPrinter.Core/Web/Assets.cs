using Microsoft.AspNetCore.Http;
using System.Reflection;

namespace LabelPrinter.Core.Web;

public static class Assets
{
    static readonly Assembly Asm = typeof(Assets).Assembly;
    static readonly Dictionary<string, string> Types = new()
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".webmanifest"] = "application/manifest+json", [".json"] = "application/json",
    };
    public static byte[]? Get(string name, out string contentType)
    {
        contentType = "application/octet-stream";
        using var s = Asm.GetManifestResourceStream("assets/" + name);
        if (s == null) return null;
        using var ms = new MemoryStream(); s.CopyTo(ms);
        contentType = Types.GetValueOrDefault(Path.GetExtension(name).ToLowerInvariant(), contentType);
        return ms.ToArray();
    }
    public static IResult Serve(string name, string? asName = null)
    {
        var b = Get(name, out var ct);
        return b == null ? Results.NotFound() : Results.Bytes(b, ct);
    }
}

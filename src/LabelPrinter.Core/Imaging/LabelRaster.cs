namespace LabelPrinter.Core.Imaging;

/// <summary>1-bit label bitmap. Bits[y*Stride + x/8], MSB first; 1 = BLACK (burned) dot.</summary>
public sealed class LabelRaster
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Bits { get; }
    public List<string> Warnings { get; } = new();
    public string Summary { get; set; } = "";
    public bool Cropped { get; set; }

    public LabelRaster(int w, int h)
    {
        Width = w; Height = h; Stride = (w + 7) / 8; Bits = new byte[Stride * h];
    }
    public bool Get(int x, int y) => (Bits[y * Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
    public void Set(int x, int y) => Bits[y * Stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
    public int BlackCount() { int n = 0; foreach (var b in Bits) n += System.Numerics.BitOperations.PopCount(b); return n; }
}

/// <summary>Minimal PNG encoder (1-bit grayscale) used for previews; no external imaging library needed.</summary>
public static class PngWriter
{
    static readonly uint[] Crc = BuildTable();
    static uint[] BuildTable() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
    static uint Crc32(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in type) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        Span<byte> be = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, (uint)data.Length); s.Write(be);
        s.Write(t); s.Write(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, Crc32(t, data)); s.Write(be);
    }

    /// <summary>8-bit grayscale PNG (used for the full-page crop view).</summary>
    public static byte[] EncodeGray(byte[] gray, int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 0;
        Chunk(ms, "IHDR", ihdr);
        var raw = new byte[(w + 1) * h];
        for (int y = 0; y < h; y++) Buffer.BlockCopy(gray, y * w, raw, y * (w + 1) + 1, w);
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true)) zs.Write(raw);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>Encode: black dots -> black pixels, white background.</summary>
    public static byte[] Encode(LabelRaster r)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)r.Width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)r.Height);
        ihdr[8] = 1; ihdr[9] = 0; // 1-bit grayscale (0 = black, 1 = white)
        Chunk(ms, "IHDR", ihdr);
        var raw = new byte[(r.Stride + 1) * r.Height];
        for (int y = 0; y < r.Height; y++)
        {
            int o = y * (r.Stride + 1);
            raw[o] = 0;
            for (int i = 0; i < r.Stride; i++) raw[o + 1 + i] = (byte)~r.Bits[y * r.Stride + i];
            // padding bits beyond Width in the last byte: set to white (1) so they never show
            int pad = r.Stride * 8 - r.Width;
            if (pad > 0) raw[o + r.Stride] |= (byte)((1 << pad) - 1);
        }
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) zs.Write(raw);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
}

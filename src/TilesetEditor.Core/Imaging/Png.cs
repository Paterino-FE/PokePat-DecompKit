using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TilesetEditor.Core.Imaging;

/// <summary>Decoded PNG. For indexed images <see cref="Indices"/> and <see cref="Palette"/> are set.</summary>
public sealed class PngImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int BitDepth { get; init; }
    public required PngColorType ColorType { get; init; }
    /// <summary>RGBA pixels (always populated).</summary>
    public required RgbaImage Rgba { get; init; }
    /// <summary>Raw palette indices per pixel for indexed images, else null.</summary>
    public byte[]? Indices { get; init; }
    /// <summary>PLTE entries (with tRNS alpha applied) for indexed images, else null.</summary>
    public Rgba32[]? Palette { get; init; }

    public bool IsIndexed => ColorType == PngColorType.Indexed;
}

public enum PngColorType : byte
{
    Grayscale = 0,
    Rgb = 2,
    Indexed = 3,
    GrayscaleAlpha = 4,
    Rgba = 6,
}

public sealed class PngFormatException(string message) : Exception(message);

/// <summary>
/// Minimal, dependency-free PNG reader/writer. Supports all non-interlaced color types and bit depths
/// that the PNG spec allows, which covers what image editors and the decomp tools produce.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static PngImage Load(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PngFormatException($"Cannot read file '{path}': {ex.Message}");
        }
        try
        {
            return Decode(data);
        }
        catch (PngFormatException ex)
        {
            throw new PngFormatException($"'{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    public static PngImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual(Signature))
            throw new PngFormatException("Not a valid PNG file (signature missing).");

        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, interlace = 0;
        PngColorType colorType = 0;
        bool haveHeader = false;
        Rgba32[]? palette = null;
        byte[]? trns = null;
        using var idat = new MemoryStream();

        while (pos + 8 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
            string type = Encoding.ASCII.GetString(data.Slice(pos + 4, 4));
            if (len < 0 || pos + 12 + len > data.Length) throw new PngFormatException($"Corrupt chunk '{type}'.");
            var body = data.Slice(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(body.Slice(4, 4));
                    bitDepth = body[8];
                    colorType = (PngColorType)body[9];
                    if (body[10] != 0 || body[11] != 0) throw new PngFormatException("Unknown compression/filter method.");
                    interlace = body[12];
                    haveHeader = true;
                    break;
                case "PLTE":
                    palette = new Rgba32[len / 3];
                    for (int i = 0; i < palette.Length; i++)
                        palette[i] = new Rgba32(body[i * 3], body[i * 3 + 1], body[i * 3 + 2]);
                    break;
                case "tRNS":
                    trns = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                case "IEND":
                    pos = data.Length;
                    continue;
            }
            pos += 12 + len;
        }

        if (!haveHeader) throw new PngFormatException("IHDR chunk missing.");
        if (width <= 0 || height <= 0) throw new PngFormatException("Invalid image dimensions.");
        if (interlace != 0)
            throw new PngFormatException("Interlaced PNGs are not supported. Save the image without interlacing.");

        int channels = colorType switch
        {
            PngColorType.Grayscale => 1,
            PngColorType.Rgb => 3,
            PngColorType.Indexed => 1,
            PngColorType.GrayscaleAlpha => 2,
            PngColorType.Rgba => 4,
            _ => throw new PngFormatException($"Unknown PNG color type {(int)colorType}."),
        };
        bool validDepth = colorType switch
        {
            PngColorType.Grayscale => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngColorType.Indexed => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!validDepth) throw new PngFormatException($"Invalid bit depth {bitDepth} for color type {colorType}.");
        if (colorType == PngColorType.Indexed && palette == null) throw new PngFormatException("Indexed PNG without a palette.");

        int bitsPerPixel = channels * bitDepth;
        int stride = (width * bitsPerPixel + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);

        byte[] raw;
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        using (var outMs = new MemoryStream())
        {
            try
            {
                z.CopyTo(outMs);
            }
            catch (InvalidDataException)
            {
                throw new PngFormatException("Image data is corrupt (zlib).");
            }
            raw = outMs.ToArray();
        }
        if (raw.Length < (long)(stride + 1) * height) throw new PngFormatException("Image data is incomplete.");

        var scan = new byte[stride * height];
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
            Unfilter(filter, cur, prev, bpp);
            Buffer.BlockCopy(cur, 0, scan, y * stride, stride);
            (prev, cur) = (cur, prev);
        }

        if (palette != null && trns != null && colorType == PngColorType.Indexed)
        {
            for (int i = 0; i < palette.Length && i < trns.Length; i++)
                palette[i] = palette[i] with { A = trns[i] };
        }

        var img = new RgbaImage(width, height);
        byte[]? indices = colorType == PngColorType.Indexed ? new byte[width * height] : null;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                Rgba32 c;
                switch (colorType)
                {
                    case PngColorType.Indexed:
                    {
                        int idx = ReadSample(scan, row, x, bitDepth);
                        indices![y * width + x] = (byte)idx;
                        c = idx < palette!.Length ? palette[idx] : new Rgba32(0, 0, 0);
                        break;
                    }
                    case PngColorType.Grayscale:
                    {
                        int v = ReadSample(scan, row, x, bitDepth);
                        byte g = ScaleTo8(v, bitDepth);
                        c = new Rgba32(g, g, g);
                        if (trns is { Length: >= 2 } && BinaryPrimitives.ReadUInt16BigEndian(trns) == v) c = c with { A = 0 };
                        break;
                    }
                    case PngColorType.GrayscaleAlpha:
                    {
                        byte g = Read8(scan, row, x * 2, bitDepth);
                        byte a = Read8(scan, row, x * 2 + 1, bitDepth);
                        c = new Rgba32(g, g, g, a);
                        break;
                    }
                    case PngColorType.Rgb:
                    {
                        c = new Rgba32(Read8(scan, row, x * 3, bitDepth), Read8(scan, row, x * 3 + 1, bitDepth), Read8(scan, row, x * 3 + 2, bitDepth));
                        if (trns is { Length: >= 6 } && bitDepth == 8 &&
                            trns[1] == c.R && trns[3] == c.G && trns[5] == c.B) c = c with { A = 0 };
                        break;
                    }
                    default:
                        c = new Rgba32(Read8(scan, row, x * 4, bitDepth), Read8(scan, row, x * 4 + 1, bitDepth),
                            Read8(scan, row, x * 4 + 2, bitDepth), Read8(scan, row, x * 4 + 3, bitDepth));
                        break;
                }
                img.Pixels[y * width + x] = c;
            }
        }

        return new PngImage
        {
            Width = width,
            Height = height,
            BitDepth = bitDepth,
            ColorType = colorType,
            Rgba = img,
            Indices = indices,
            Palette = palette,
        };
    }

    private static int ReadSample(byte[] scan, int row, int x, int bitDepth)
    {
        switch (bitDepth)
        {
            case 8: return scan[row + x];
            case 16: return (scan[row + x * 2] << 8) | scan[row + x * 2 + 1];
            default:
            {
                int perByte = 8 / bitDepth;
                int b = scan[row + x / perByte];
                int shift = 8 - bitDepth * (x % perByte + 1);
                return (b >> shift) & ((1 << bitDepth) - 1);
            }
        }
    }

    /// <summary>Reads the channel sample with index <paramref name="sample"/> and scales it to 8 bits.</summary>
    private static byte Read8(byte[] scan, int row, int sample, int bitDepth) =>
        bitDepth == 16 ? scan[row + sample * 2] : scan[row + sample];

    private static byte ScaleTo8(int v, int bitDepth) => bitDepth switch
    {
        16 => (byte)(v >> 8),
        8 => (byte)v,
        _ => (byte)(v * 255 / ((1 << bitDepth) - 1)),
    };

    private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1:
                for (int i = bpp; i < cur.Length; i++) cur[i] += cur[i - bpp];
                break;
            case 2:
                for (int i = 0; i < cur.Length; i++) cur[i] += prev[i];
                break;
            case 3:
                for (int i = 0; i < cur.Length; i++)
                {
                    int left = i >= bpp ? cur[i - bpp] : 0;
                    cur[i] += (byte)((left + prev[i]) / 2);
                }
                break;
            case 4:
                for (int i = 0; i < cur.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    cur[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }
                break;
            default:
                throw new PngFormatException($"Unknown row filter {filter}.");
        }
    }

    /// <summary>Encodes an 8-bit RGBA PNG.</summary>
    public static byte[] EncodeRgba(RgbaImage image)
    {
        int stride = image.Width * 4;
        var raw = new byte[(stride + 1) * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            int o = y * (stride + 1);
            raw[o] = 0;
            for (int x = 0; x < image.Width; x++)
            {
                var c = image.Pixels[y * image.Width + x];
                int p = o + 1 + x * 4;
                raw[p] = c.R; raw[p + 1] = c.G; raw[p + 2] = c.B; raw[p + 3] = c.A;
            }
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), image.Height);
        ihdr[8] = 8;
        ihdr[9] = (byte)PngColorType.Rgba;
        return Assemble(ihdr, raw, null);
    }

    /// <summary>Encodes an indexed PNG with the given bit depth (4 or 8).</summary>
    public static byte[] EncodeIndexed(int width, int height, byte[] indices, IReadOnlyList<Rgba32> palette, int bitDepth)
    {
        if (bitDepth is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(bitDepth));
        int stride = (width * bitDepth + 7) / 8;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * (stride + 1) + 1;
            for (int x = 0; x < width; x++)
            {
                byte v = indices[y * width + x];
                if (bitDepth == 8) raw[o + x] = v;
                else raw[o + x / 2] |= (byte)((x % 2 == 0 ? v << 4 : v) & (x % 2 == 0 ? 0xF0 : 0x0F));
            }
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)PngColorType.Indexed;
        var plte = new byte[palette.Count * 3];
        for (int i = 0; i < palette.Count; i++)
        {
            plte[i * 3] = palette[i].R; plte[i * 3 + 1] = palette[i].G; plte[i * 3 + 2] = palette[i].B;
        }
        return Assemble(ihdr, raw, plte);
    }

    private static byte[] Assemble(byte[] ihdr, byte[] raw, byte[]? plte)
    {
        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", ihdr);
        if (plte != null) WriteChunk(ms, "PLTE", plte);
        using (var comp = new MemoryStream())
        {
            using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            WriteChunk(ms, "IDAT", comp.ToArray());
        }
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(body);
        uint crc = Crc32.Compute(typeBytes, body);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Compute(byte[] a, byte[] b)
        {
            uint c = 0xFFFFFFFFu;
            foreach (var x in a) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}

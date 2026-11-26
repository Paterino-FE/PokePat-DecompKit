using System.Buffers.Binary;
using System.Text;
using TilesetEditor.Core.Imaging;

namespace TilesetEditor.Core.Platinum;

/// <summary>
/// Readers for the Nintendo DS 3D formats Platinum's maps use: NSBTX/TEX0 (textures and palettes) and NSBMD/MDL0 (models:
/// materials, shapes as GX display lists, render commands). Only what a top-down preview needs is read.
/// </summary>
public static class Nitro
{
    internal static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
    internal static short S16(byte[] d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(o));
    internal static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    internal static int S32(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));

    /// <summary>Offsets of the blocks of a Nitro file (BMD0/BTX0) by their stamp ("MDL0", "TEX0").</summary>
    public static Dictionary<string, int> Blocks(byte[] d, int start = 0)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (d.Length < start + 16) return result;
        int n = U16(d, start + 14);
        for (int i = 0; i < n && start + 16 + i * 4 + 4 <= d.Length; i++)
        {
            int off = start + (int)U32(d, start + 16 + i * 4);
            if (off + 4 <= d.Length) result[Encoding.ASCII.GetString(d, off, 4)] = off;
        }
        return result;
    }

    /// <summary>A Nitro dictionary ("info block"): names with fixed-size data entries.</summary>
    public static List<(string Name, int DataOffset)> Dict(byte[] d, int at)
    {
        var list = new List<(string, int)>();
        int count = d[at + 1];
        // Header (4), Patricia tree header (8) and one 4-byte tree node per entry, then the data part.
        int data = at + 12 + 4 * count;
        int unit = U16(d, data);
        int entries = data + 4;
        int names = entries + count * unit;
        for (int i = 0; i < count; i++)
        {
            var raw = Encoding.ASCII.GetString(d, names + i * 16, 16);
            int z = raw.IndexOf('\0');
            list.Add((z >= 0 ? raw[..z] : raw, entries + i * unit));
        }
        return list;
    }

    public static Rgba32 Bgr555(ushort c) =>
        new((byte)((c & 31) * 255 / 31), (byte)(((c >> 5) & 31) * 255 / 31), (byte)(((c >> 10) & 31) * 255 / 31), 255);
}

/// <summary>A texture of a TEX0 block, decoded on demand with a palette.</summary>
public sealed class NitroTexture
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Format { get; init; }
    public required bool Color0Transparent { get; init; }
    public required int DataOffset { get; init; }
}

/// <summary>Textures and palettes of a TEX0 block (an .nsbtx file or the TEX0 inside an .nsbmd).</summary>
public sealed class NitroTextureSet
{
    private readonly byte[] _d;
    private readonly int _texData, _cmpData, _cmpExtra, _palData;
    private readonly Dictionary<string, NitroTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _palettes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string?), RgbaImage> _cache = new();

    public IReadOnlyCollection<string> TextureNames => _textures.Keys;
    public IReadOnlyCollection<string> PaletteNames => _palettes.Keys;

    private NitroTextureSet(byte[] d, int tex0)
    {
        _d = d;
        int texDict = tex0 + Nitro.U16(d, tex0 + 0x0E);
        _texData = tex0 + (int)Nitro.U32(d, tex0 + 0x14);
        _cmpData = tex0 + (int)Nitro.U32(d, tex0 + 0x24);
        _cmpExtra = tex0 + (int)Nitro.U32(d, tex0 + 0x28);
        int palDict = tex0 + (int)Nitro.U32(d, tex0 + 0x34);
        _palData = tex0 + (int)Nitro.U32(d, tex0 + 0x38);
        foreach (var (name, at) in Nitro.Dict(d, texDict))
        {
            uint p = Nitro.U32(d, at);
            int format = (int)((p >> 26) & 7);
            _textures[name] = new NitroTexture
            {
                Name = name,
                Width = 8 << (int)((p >> 20) & 7),
                Height = 8 << (int)((p >> 23) & 7),
                Format = format,
                Color0Transparent = ((p >> 29) & 1) != 0,
                DataOffset = (int)(p & 0xFFFF) << 3,
            };
        }
        foreach (var (name, at) in Nitro.Dict(d, palDict))
            _palettes[name] = Nitro.U16(d, at) << 3;
    }

    /// <summary>The TEX0 of an .nsbtx or .nsbmd file; null if it has none.</summary>
    public static NitroTextureSet? Load(byte[] file)
    {
        var blocks = Nitro.Blocks(file);
        return blocks.TryGetValue("TEX0", out var tex0) ? new NitroTextureSet(file, tex0) : null;
    }

    public bool Has(string texture) => _textures.ContainsKey(texture);

    /// <summary>The texture in RGBA (alpha 0 = transparent); palette by name, or the texture's name + "_pl", or the first one.</summary>
    public RgbaImage? Decode(string texture, string? palette)
    {
        if (!_textures.TryGetValue(texture, out var t)) return null;
        if (_cache.TryGetValue((texture, palette), out var cached)) return cached;
        int pal = palette != null && _palettes.TryGetValue(palette, out var po) ? po
            : _palettes.TryGetValue(texture + "_pl", out var p2) ? p2
            : _palettes.Count > 0 ? _palettes.Values.First() : 0;
        var img = new RgbaImage(t.Width, t.Height);
        try { DecodeInto(t, _palData + pal, img); }
        catch (IndexOutOfRangeException) { }
        catch (ArgumentOutOfRangeException) { }
        return _cache[(texture, palette)] = img;
    }

    private Rgba32 Pal(int palBase, int index) =>
        palBase + index * 2 + 1 < _d.Length ? Nitro.Bgr555(Nitro.U16(_d, palBase + index * 2)) : new Rgba32(255, 0, 255);

    private void DecodeInto(NitroTexture t, int palBase, RgbaImage img)
    {
        int w = t.Width, h = t.Height;
        int src = _texData + t.DataOffset;
        switch (t.Format)
        {
            case 1: // A3I5
                for (int i = 0; i < w * h; i++)
                {
                    byte b = _d[src + i];
                    int a = (b >> 5) * 255 / 7;
                    img.Pixels[i] = Pal(palBase, b & 31) with { A = (byte)a };
                }
                break;
            case 2: // 4 colours
                for (int i = 0; i < w * h; i++)
                {
                    int idx = (_d[src + i / 4] >> ((i % 4) * 2)) & 3;
                    img.Pixels[i] = idx == 0 && t.Color0Transparent ? Rgba32.Transparent : Pal(palBase, idx);
                }
                break;
            case 3: // 16 colours
                for (int i = 0; i < w * h; i++)
                {
                    int idx = (_d[src + i / 2] >> ((i % 2) * 4)) & 15;
                    img.Pixels[i] = idx == 0 && t.Color0Transparent ? Rgba32.Transparent : Pal(palBase, idx);
                }
                break;
            case 4: // 256 colours
                for (int i = 0; i < w * h; i++)
                {
                    int idx = _d[src + i];
                    img.Pixels[i] = idx == 0 && t.Color0Transparent ? Rgba32.Transparent : Pal(palBase, idx);
                }
                break;
            case 5: // 4×4 compressed
                Decode4x4(t, palBase, img);
                break;
            case 6: // A5I3
                for (int i = 0; i < w * h; i++)
                {
                    byte b = _d[src + i];
                    int a = (b >> 3) * 255 / 31;
                    img.Pixels[i] = Pal(palBase, b & 7) with { A = (byte)a };
                }
                break;
            case 7: // direct colour
                for (int i = 0; i < w * h; i++)
                {
                    ushort c = Nitro.U16(_d, src + i * 2);
                    img.Pixels[i] = (c & 0x8000) == 0 ? Rgba32.Transparent : Nitro.Bgr555(c);
                }
                break;
        }
    }

    private void Decode4x4(NitroTexture t, int palBase, RgbaImage img)
    {
        int bw = t.Width / 4, bh = t.Height / 4;
        int texels = _cmpData + t.DataOffset;
        int extra = _cmpExtra + t.DataOffset / 2;
        var c = new Rgba32[4];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int block = by * bw + bx;
                uint bits = Nitro.U32(_d, texels + block * 4);
                ushort info = Nitro.U16(_d, extra + block * 2);
                int pal = palBase + (info & 0x3FFF) * 4;
                int mode = info >> 14;
                Rgba32 P(int i) => pal + i * 2 + 1 < _d.Length ? Nitro.Bgr555(Nitro.U16(_d, pal + i * 2)) : new Rgba32(255, 0, 255);
                c[0] = P(0);
                c[1] = P(1);
                switch (mode)
                {
                    case 0: c[2] = P(2); c[3] = Rgba32.Transparent; break;
                    case 1: c[2] = Mix(c[0], c[1], 1, 1); c[3] = Rgba32.Transparent; break;
                    case 2: c[2] = P(2); c[3] = P(3); break;
                    default: c[2] = Mix(c[0], c[1], 5, 3); c[3] = Mix(c[0], c[1], 3, 5); break;
                }
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        img[bx * 4 + x, by * 4 + y] = c[(bits >> ((y * 4 + x) * 2)) & 3];
            }
    }

    private static Rgba32 Mix(Rgba32 a, Rgba32 b, int wa, int wb) =>
        new((byte)((a.R * wa + b.R * wb) / (wa + wb)), (byte)((a.G * wa + b.G * wb) / (wa + wb)), (byte)((a.B * wa + b.B * wb) / (wa + wb)), 255);
}

public sealed record NitroMaterial(string Name, string? Texture, string? Palette, int Width, int Height, bool RepeatS, bool RepeatT, bool FlipS, bool FlipT, int Alpha, Rgba32 Diffuse, bool DiffuseAsVertexColor);

/// <summary>A triangle of a model in model units with texture coordinates (texels) and vertex colours.</summary>
public readonly record struct NitroTriangle(int Material,
    float X0, float Y0, float Z0, float S0, float T0, Rgba32 C0,
    float X1, float Y1, float Z1, float S1, float T1, Rgba32 C1,
    float X2, float Y2, float Z2, float S2, float T2, Rgba32 C2);

/// <summary>The first model of an MDL0 block, flattened to triangles.</summary>
public sealed class NitroModel
{
    public required string Name { get; init; }
    public required List<NitroMaterial> Materials { get; init; }
    public required List<NitroTriangle> Triangles { get; init; }
    /// <summary>The model's own textures (TEX0 in the .nsbmd), if any.</summary>
    public NitroTextureSet? Textures { get; init; }

    public static NitroModel? Load(byte[] file)
    {
        var blocks = Nitro.Blocks(file);
        if (!blocks.TryGetValue("MDL0", out var mdl0)) return null;
        var models = Nitro.Dict(file, mdl0 + 8);
        if (models.Count == 0) return null;
        int m = mdl0 + (int)Nitro.U32(file, models[0].DataOffset);
        return Parse(file, m, models[0].Name, NitroTextureSet.Load(file));
    }

    private static NitroModel Parse(byte[] d, int m, string name, NitroTextureSet? textures)
    {
        int sbc = m + (int)Nitro.U32(d, m + 4);
        int mats = m + (int)Nitro.U32(d, m + 8);
        int shapes = m + (int)Nitro.U32(d, m + 12);
        float posScale = Nitro.S32(d, m + 0x1C) / 4096f;

        // ---- materials: texture/palette bindings, then the material records
        var materialDict = Nitro.Dict(d, mats + 4);
        var materials = new List<NitroMaterial>();
        var texOf = new string?[materialDict.Count];
        var palOf = new string?[materialDict.Count];
        void Bind(int dictOffset, string?[] target)
        {
            if (dictOffset == 0) return;
            foreach (var (n, at) in Nitro.Dict(d, mats + dictOffset))
            {
                int list = mats + Nitro.U16(d, at);
                int count = d[at + 2];
                for (int i = 0; i < count; i++)
                {
                    int mi = d[list + i];
                    if (mi < target.Length) target[mi] = n;
                }
            }
        }
        Bind(Nitro.U16(d, mats), texOf);
        Bind(Nitro.U16(d, mats + 2), palOf);
        for (int i = 0; i < materialDict.Count; i++)
        {
            int at = mats + (int)Nitro.U32(d, materialDict[i].DataOffset);
            uint diffAmb = Nitro.U32(d, at + 4);
            uint polyAttr = Nitro.U32(d, at + 12);
            uint texParam = Nitro.U32(d, at + 20);
            int w = Nitro.U16(d, at + 32), h = Nitro.U16(d, at + 34);
            materials.Add(new NitroMaterial(materialDict[i].Name, texOf[i], palOf[i], w, h,
                (texParam & (1 << 16)) != 0, (texParam & (1 << 17)) != 0, (texParam & (1 << 18)) != 0, (texParam & (1 << 19)) != 0,
                (int)((polyAttr >> 16) & 31), Nitro.Bgr555((ushort)(diffAmb & 0x7FFF)), (diffAmb & 0x8000) != 0));
        }

        // ---- shapes
        var shapeDict = Nitro.Dict(d, shapes);
        var shapeAt = shapeDict.Select(s => shapes + (int)Nitro.U32(d, s.DataOffset)).ToList();

        // ---- render commands: which material each shape is drawn with
        var triangles = new List<NitroTriangle>();
        int material = 0;
        var drawn = new HashSet<int>();
        int p = sbc;
        bool end = false;
        while (!end && p < d.Length && p < sbc + 0x10000)
        {
            int op = d[p];
            switch (op & 0x1F)
            {
                case 0x00: p += 1; break;
                case 0x01: end = true; break;
                case 0x02: p += 3; break;
                case 0x03: p += 2; break;
                case 0x04: material = d[p + 1]; p += 2; break;
                case 0x05:
                {
                    int shape = d[p + 1];
                    if (shape < shapeAt.Count) { DrawShape(d, shapeAt[shape], material, posScale, materials, triangles); drawn.Add(shape); }
                    p += 2;
                    break;
                }
                case 0x06: p += op switch { 0x06 => 4, 0x26 or 0x46 => 5, _ => 6 }; break;
                case 0x07: case 0x08: p += op >= 0x40 ? 3 : 2; break;
                case 0x09: p += 3 + d[p + 2] * 3; break;
                case 0x0A: p += 9; break;
                case 0x0B: p += 1; break;
                case 0x0C: case 0x0D: p += 3; break;
                default: end = true; break;
            }
        }
        // Shapes no render command reached are drawn with their index as material (rare).
        for (int i = 0; i < shapeAt.Count; i++)
            if (!drawn.Contains(i)) DrawShape(d, shapeAt[i], Math.Min(i, Math.Max(0, materials.Count - 1)), posScale, materials, triangles);

        return new NitroModel { Name = name, Materials = materials, Triangles = triangles, Textures = textures };
    }

    private static readonly int[] ParamCount = BuildParamCounts();

    private static int[] BuildParamCounts()
    {
        var p = new int[256];
        p[0x10] = 1; p[0x12] = 1; p[0x13] = 1; p[0x14] = 1; p[0x16] = 16; p[0x17] = 12; p[0x18] = 16; p[0x19] = 12; p[0x1A] = 9; p[0x1B] = 3; p[0x1C] = 3;
        p[0x20] = 1; p[0x21] = 1; p[0x22] = 1; p[0x23] = 2; p[0x24] = 1; p[0x25] = 1; p[0x26] = 1; p[0x27] = 1; p[0x28] = 1; p[0x29] = 1; p[0x2A] = 1; p[0x2B] = 1;
        p[0x30] = 1; p[0x31] = 1; p[0x32] = 1; p[0x33] = 1; p[0x34] = 32; p[0x40] = 1; p[0x50] = 1; p[0x60] = 1; p[0x70] = 3; p[0x71] = 2; p[0x72] = 1;
        return p;
    }

    private struct Vtx { public float X, Y, Z, S, T; public Rgba32 C; }

    private static void DrawShape(byte[] d, int shape, int material, float scale, List<NitroMaterial> materials, List<NitroTriangle> output)
    {
        int dl = shape + (int)Nitro.U32(d, shape + 8);
        int size = (int)Nitro.U32(d, shape + 12);
        int end = Math.Min(d.Length, dl + size);
        var mat = material < materials.Count ? materials[material] : null;
        var color = mat is { DiffuseAsVertexColor: true } ? mat.Diffuse : new Rgba32(255, 255, 255);
        float x = 0, y = 0, z = 0, s = 0, t = 0;
        int prim = 0;
        var strip = new List<Vtx>();

        void Emit(Vtx a, Vtx b, Vtx c) => output.Add(new NitroTriangle(material,
            a.X * scale, a.Y * scale, a.Z * scale, a.S, a.T, a.C,
            b.X * scale, b.Y * scale, b.Z * scale, b.S, b.T, b.C,
            c.X * scale, c.Y * scale, c.Z * scale, c.S, c.T, c.C));

        void AddVertex()
        {
            strip.Add(new Vtx { X = x, Y = y, Z = z, S = s, T = t, C = color });
            int n = strip.Count;
            switch (prim)
            {
                case 0 when n == 3: Emit(strip[0], strip[1], strip[2]); strip.Clear(); break;
                case 1 when n == 4: Emit(strip[0], strip[1], strip[2]); Emit(strip[0], strip[2], strip[3]); strip.Clear(); break;
                case 2 when n >= 3: Emit(strip[n - 3], strip[n - 2], strip[n - 1]); break;
                case 3 when n >= 4 && n % 2 == 0: Emit(strip[n - 4], strip[n - 3], strip[n - 1]); Emit(strip[n - 4], strip[n - 1], strip[n - 2]); break;
            }
        }

        static float S10(uint v, int shift) => (short)(((v >> shift) & 0x3FF) << 6) >> 6;

        int pos = dl;
        while (pos + 4 <= end)
        {
            uint packed = Nitro.U32(d, pos);
            pos += 4;
            for (int k = 0; k < 4; k++)
            {
                int cmd = (int)((packed >> (k * 8)) & 0xFF);
                if (cmd == 0) continue;
                int n = ParamCount[cmd];
                if (pos + n * 4 > end) return;
                uint p0 = n > 0 ? Nitro.U32(d, pos) : 0;
                switch (cmd)
                {
                    case 0x20: color = Nitro.Bgr555((ushort)(p0 & 0x7FFF)); break;
                    case 0x22: s = (short)(p0 & 0xFFFF) / 16f; t = (short)(p0 >> 16) / 16f; break;
                    case 0x23:
                        x = (short)(p0 & 0xFFFF) / 4096f; y = (short)(p0 >> 16) / 4096f; z = (short)(Nitro.U32(d, pos + 4) & 0xFFFF) / 4096f;
                        AddVertex(); break;
                    case 0x24: x = S10(p0, 0) / 64f; y = S10(p0, 10) / 64f; z = S10(p0, 20) / 64f; AddVertex(); break;
                    case 0x25: x = (short)(p0 & 0xFFFF) / 4096f; y = (short)(p0 >> 16) / 4096f; AddVertex(); break;
                    case 0x26: x = (short)(p0 & 0xFFFF) / 4096f; z = (short)(p0 >> 16) / 4096f; AddVertex(); break;
                    case 0x27: y = (short)(p0 & 0xFFFF) / 4096f; z = (short)(p0 >> 16) / 4096f; AddVertex(); break;
                    case 0x28: x += S10(p0, 0) / 4096f; y += S10(p0, 10) / 4096f; z += S10(p0, 20) / 4096f; AddVertex(); break;
                    case 0x40: prim = (int)(p0 & 3); strip.Clear(); break;
                    case 0x41: strip.Clear(); break;
                }
                pos += n * 4;
            }
        }
    }
}

/// <summary>
/// Software rasterizer for a top-down orthographic view of models: X → right, Z → down, the highest surface wins
/// (a height buffer), textures sampled nearest with the material's repeat/flip, vertex colours multiplied.
/// </summary>
public sealed class TopDownRasterizer
{
    private readonly RgbaImage _img;
    private readonly float[] _height;
    private readonly float _scale, _originX, _originZ;

    /// <param name="pixelsPerUnit">Pixels per model unit (16 units = one tile).</param>
    public TopDownRasterizer(RgbaImage target, float pixelsPerUnit, float originX, float originZ)
    {
        _img = target;
        _height = new float[target.Width * target.Height];
        Array.Fill(_height, float.NegativeInfinity);
        _scale = pixelsPerUnit;
        _originX = originX;
        _originZ = originZ;
    }

    /// <summary>Draws a model placed at (dx, dy, dz) model units, optionally turned around Y and scaled.</summary>
    public void Draw(NitroModel model, Func<NitroMaterial, RgbaImage?> texture, float dx, float dy, float dz, float rotY = 0, float scale = 1, bool translucentPass = false)
    {
        float cos = MathF.Cos(rotY), sin = MathF.Sin(rotY);
        var tex = new RgbaImage?[model.Materials.Count];
        for (int i = 0; i < tex.Length; i++) tex[i] = texture(model.Materials[i]);
        foreach (var tri in model.Triangles)
        {
            var mat = tri.Material < model.Materials.Count ? model.Materials[tri.Material] : null;
            bool translucent = mat != null && mat.Alpha is > 0 and < 31;
            if (translucent != translucentPass) continue;
            if (mat is { Alpha: 0 }) continue; // wireframe/invisible
            var t = tri.Material < tex.Length ? tex[tri.Material] : null;
            (float, float, float) P(float x, float y, float z)
            {
                x *= scale; y *= scale; z *= scale;
                return (x * cos + z * sin + dx, y + dy, -x * sin + z * cos + dz);
            }
            var (x0, y0, z0) = P(tri.X0, tri.Y0, tri.Z0);
            var (x1, y1, z1) = P(tri.X1, tri.Y1, tri.Z1);
            var (x2, y2, z2) = P(tri.X2, tri.Y2, tri.Z2);
            Fill(mat, t,
                ((x0 - _originX) * _scale, (z0 - _originZ) * _scale, y0, tri.S0, tri.T0, tri.C0),
                ((x1 - _originX) * _scale, (z1 - _originZ) * _scale, y1, tri.S1, tri.T1, tri.C1),
                ((x2 - _originX) * _scale, (z2 - _originZ) * _scale, y2, tri.S2, tri.T2, tri.C2));
        }
    }

    private void Fill(NitroMaterial? mat, RgbaImage? tex,
        (float X, float Y, float H, float S, float T, Rgba32 C) a,
        (float X, float Y, float H, float S, float T, Rgba32 C) b,
        (float X, float Y, float H, float S, float T, Rgba32 C) c)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (MathF.Abs(area) < 1e-6f) return; // walls seen exactly from above
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(_img.Width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(_img.Height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        float alpha = mat == null ? 1f : mat.Alpha / 31f;
        for (int py = minY; py <= maxY; py++)
            for (int px = minX; px <= maxX; px++)
            {
                float fx = px + 0.5f, fy = py + 0.5f;
                float w0 = ((b.X - fx) * (c.Y - fy) - (c.X - fx) * (b.Y - fy)) / area;
                float w1 = ((c.X - fx) * (a.Y - fy) - (a.X - fx) * (c.Y - fy)) / area;
                float w2 = 1 - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                float h = w0 * a.H + w1 * b.H + w2 * c.H;
                int i = py * _img.Width + px;
                if (h < _height[i] - 0.01f) continue;
                Rgba32 col;
                if (tex != null && mat != null)
                {
                    float s = w0 * a.S + w1 * b.S + w2 * c.S, t = w0 * a.T + w1 * b.T + w2 * c.T;
                    col = tex[Wrap((int)MathF.Floor(s), tex.Width, mat.RepeatS, mat.FlipS), Wrap((int)MathF.Floor(t), tex.Height, mat.RepeatT, mat.FlipT)];
                    if (col.A < 16) continue;
                }
                else col = new Rgba32(200, 200, 200);
                var vc = new Rgba32(
                    (byte)(w0 * a.C.R + w1 * b.C.R + w2 * c.C.R), (byte)(w0 * a.C.G + w1 * b.C.G + w2 * c.C.G), (byte)(w0 * a.C.B + w1 * b.C.B + w2 * c.C.B));
                col = new Rgba32((byte)(col.R * (vc.R + 1) >> 8), (byte)(col.G * (vc.G + 1) >> 8), (byte)(col.B * (vc.B + 1) >> 8), col.A);
                float a2 = alpha * col.A / 255f;
                if (a2 >= 0.99f)
                {
                    _img.Pixels[i] = col with { A = 255 };
                    _height[i] = h;
                }
                else
                {
                    var dst = _img.Pixels[i];
                    _img.Pixels[i] = new Rgba32((byte)(dst.R + (col.R - dst.R) * a2), (byte)(dst.G + (col.G - dst.G) * a2), (byte)(dst.B + (col.B - dst.B) * a2), 255);
                }
            }
    }

    private static int Wrap(int v, int size, bool repeat, bool flip)
    {
        if (!repeat) return Math.Clamp(v, 0, size - 1);
        if (!flip) return ((v % size) + size) % size;
        int period = size * 2;
        int m = ((v % period) + period) % period;
        return m < size ? m : period - 1 - m;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

// A plot: polylines in sheet millimetres (origin top-left, y down), grouped into pen layers.
public class Plot
{
    public double Width = 210, Height = 297;            // A4 portrait
    public double PenWidth = 0.3;
    public readonly Dictionary<string, List<List<V2>>> Layers = new Dictionary<string, List<List<V2>>>();
    public readonly List<string> LayerOrder = new List<string>();

    public List<List<V2>> Layer(string name)
    {
        List<List<V2>> l;
        if (!Layers.TryGetValue(name, out l)) { l = new List<List<V2>>(); Layers[name] = l; LayerOrder.Add(name); }
        return l;
    }

    public double PenDownLength()
    {
        double s = 0;
        foreach (var l in Layers.Values) foreach (var p in l) for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length;
        return s;
    }
    public int Strokes() { return Layers.Values.Sum(l => l.Count); }
}

public struct V2
{
    public double X, Y;
    public V2(double x, double y) { X = x; Y = y; }
    public static V2 operator +(V2 a, V2 b) { return new V2(a.X + b.X, a.Y + b.Y); }
    public static V2 operator -(V2 a, V2 b) { return new V2(a.X - b.X, a.Y - b.Y); }
    public static V2 operator *(V2 a, double s) { return new V2(a.X * s, a.Y * s); }
    public double Length { get { return Math.Sqrt(X * X + Y * Y); } }
    public V2 Normalized() { double l = Length; return l > 1e-12 ? new V2(X / l, Y / l) : new V2(0, 0); }
}

public static class SheetIO
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    // Greedy pen-travel ordering per layer: next stroke = nearest end (either direction).
    public static void Optimize(Plot p)
    {
        foreach (var name in p.LayerOrder)
        {
            var src = p.Layers[name];
            var done = new List<List<V2>>(src.Count);
            var left = new List<List<V2>>(src.Where(s => s.Count >= 2));
            var cur = new V2(0, 0);
            // Spatial bins of stroke ends for speed.
            while (left.Count > 0)
            {
                int best = 0; bool rev = false; double bd = double.MaxValue;
                for (int i = 0; i < left.Count; i++)
                {
                    double d0 = (left[i][0] - cur).Length, d1 = (left[i][left[i].Count - 1] - cur).Length;
                    if (d0 < bd) { bd = d0; best = i; rev = false; }
                    if (d1 < bd) { bd = d1; best = i; rev = true; }
                }
                var s = left[best];
                left[best] = left[left.Count - 1]; left.RemoveAt(left.Count - 1);
                if (rev) s.Reverse();
                done.Add(s);
                cur = s[s.Count - 1];
            }
            src.Clear(); src.AddRange(done);
        }
    }

    public static void WriteSvg(Plot p, string path, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.AppendFormat(CI, "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" width=\"{0}mm\" height=\"{1}mm\" viewBox=\"0 0 {0} {1}\">\n", p.Width, p.Height);
        sb.Append("<title>").Append(System.Security.SecurityElement.Escape(title)).Append("</title>\n");
        int li = 1;
        foreach (var name in p.LayerOrder)
        {
            sb.AppendFormat(CI, "<g inkscape:groupmode=\"layer\" inkscape:label=\"{0} {1}\" fill=\"none\" stroke=\"black\" stroke-width=\"{2}\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n", li++, name, p.PenWidth);
            foreach (var s in p.Layers[name])
            {
                if (s.Count < 2) continue;
                sb.Append("<polyline points=\"");
                for (int i = 0; i < s.Count; i++) sb.AppendFormat(CI, "{0:0.###},{1:0.###} ", s[i].X, s[i].Y);
                sb.Append("\"/>\n");
            }
            sb.Append("</g>\n");
        }
        sb.Append("</svg>\n");
        File.WriteAllText(path, sb.ToString());
    }

    // Preview: cream paper, ink lines drawn anti-aliased at the pen width.
    public static void WritePng(Plot p, string path, double pxPerMm, Dictionary<string, double> layerInk = null)
    {
        int w = (int)Math.Ceiling(p.Width * pxPerMm), h = (int)Math.Ceiling(p.Height * pxPerMm);
        var ink = new float[w * h];   // coverage 0..1
        foreach (var name in p.LayerOrder)
        {
            double strength = layerInk != null && layerInk.ContainsKey(name) ? layerInk[name] : 1.0;
            foreach (var s in p.Layers[name])
                for (int i = 1; i < s.Count; i++)
                    DrawLine(ink, w, h, s[i - 1] * pxPerMm, s[i] * pxPerMm, Math.Max(0.6, p.PenWidth * pxPerMm), (float)strength);
        }
        // Paper (243, 238, 226), ink (28, 26, 24).
        var raw = new byte[h * (1 + w * 3)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                double a = Math.Min(1, ink[y * w + x]);
                raw[o++] = (byte)(243 + (28 - 243) * a); raw[o++] = (byte)(238 + (26 - 238) * a); raw[o++] = (byte)(226 + (24 - 226) * a);
            }
        }
        using (var fs = File.Create(path))
        {
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            var ihdr = new byte[13];
            BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            Chunk(fs, "IHDR", ihdr);
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
                Chunk(fs, "IDAT", ms.ToArray());
            }
            Chunk(fs, "IEND", new byte[0]);
        }
    }

    // Thick anti-aliased segment: coverage from the distance to the segment.
    static void DrawLine(float[] ink, int w, int h, V2 a, V2 b, double width, float strength)
    {
        double r = width / 2;
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X) - r - 1)), x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(a.X, b.X) + r + 1));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y) - r - 1)), y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + r + 1));
        V2 ab = b - a; double l2 = ab.X * ab.X + ab.Y * ab.Y;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                V2 p = new V2(x + 0.5, y + 0.5);
                double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / l2)) : 0;
                double d = (p - (a + ab * t)).Length;
                double cov = Math.Max(0, Math.Min(1, r + 0.5 - d));
                if (cov <= 0) continue;
                int i = y * w + x;
                ink[i] = Math.Max(ink[i], (float)(cov * strength));
            }
    }

    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len, 0, 4);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t, 0, 4); s.Write(data, 0, data.Length);
        uint crc = Crc(t, 0xffffffffu); crc = Crc(data, crc) ^ 0xffffffffu;
        var c = new byte[4]; BE(c, 0, (int)crc); s.Write(c, 0, 4);
    }

    static uint[] crcTable;
    static uint Crc(byte[] d, uint c)
    {
        if (crcTable == null)
        {
            crcTable = new uint[256];
            for (uint n = 0; n < 256; n++) { uint k = n; for (int i = 0; i < 8; i++) k = (k & 1) != 0 ? 0xedb88320u ^ (k >> 1) : k >> 1; crcTable[n] = k; }
        }
        foreach (byte b in d) c = crcTable[(c ^ b) & 0xff] ^ (c >> 8);
        return c;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

// Foam bed (Alex, 2026-10-19): the A3 sheet IS the plane the bubbles sit on. Bubbles are dropped onto it one at
// a time, each settling into the lowest hollow of the pile, so the foam builds up several layers deep; we look
// up at it from below, through the plane. The foam runs off the edges of the sheet (the pile is deposited over a
// larger area than the window). Many bubbles, wide size range.
//   mist     transparent stipple: walls and skin in dots, every Plateau border as a run of dots, the borders on
//            the plane (the footprint pattern, nearest to us) densest, borders higher up the pile sparser
//   lattice  borders only, thickened: every border drawn with parallel passes, thickest on the plane and thinning
//            with height, so the pile reads in depth
public class BedSettings
{
    public int Count = 120;
    public double RMin = 0.12, RMax = 1.2, SizeSkew = 2.2;   // ~10:1 sizes, mostly small
    public double Layers = 3.0;          // footprint area of all bubbles / deposition area: how deep the pile gets
    public double Overlap = 0.8;         // centre distance between touching bubbles, as a fraction of r1 + r2
    public int Candidates = 8;           // drop positions tried per bubble; the lowest wins
    public int TriangleBudget = 200000;
    public double Margin = 10;           // mm of paper around the drawing (plotter safety)
    // Filled in by Generate: the window (world units, on the plane z = 0) the sheet shows.
    public double WinW, WinH;
}

public static partial class Edition
{
    public static FoamSolver GenerateBed(EditionSettings e, BedSettings b, Action<string> log)
    {
        var rng = new Random(e.Seed);
        int n = b.Count;
        var r = new List<double>();
        for (int i = 0; i < n; i++) r.Add(b.RMin + (b.RMax - b.RMin) * Math.Pow(rng.NextDouble(), b.SizeSkew));
        // Window with the sheet's aspect; deposition area = window + a rim, sized so the footprints cover it
        // b.Layers times over.
        double aspect = (e.SheetH - 2 * b.Margin) / (e.SheetW - 2 * b.Margin);
        double rim = 0.6 * b.RMax, A = r.Sum(x => Math.PI * x * x) / b.Layers;
        double qa = aspect, qb = 2 * rim * (1 + aspect), qc = 4 * rim * rim - A;
        double w = (-qb + Math.Sqrt(qb * qb - 4 * qa * qc)) / (2 * qa);
        b.WinW = w; b.WinH = w * aspect;
        double dx = w / 2 + rim, dy = b.WinH / 2 + rim;
        // Deposition: each bubble tries a few drop positions and lands as low as it can, resting on the plane
        // (centre 0.3 r above it: a dome) or on the bubbles below it.
        var c = new List<Vec3>();
        for (int i = 0; i < n; i++)
        {
            Vec3 best = Vec3.Zero; double bz = double.MaxValue;
            for (int t = 0; t < b.Candidates; t++)
            {
                double x = (rng.NextDouble() * 2 - 1) * dx, y = (rng.NextDouble() * 2 - 1) * dy, z = 0.3 * r[i];
                for (int j = 0; j < c.Count; j++)
                {
                    double reach = b.Overlap * (r[i] + r[j]);
                    double hx = x - c[j].X, hy = y - c[j].Y, d2 = hx * hx + hy * hy;
                    if (d2 < reach * reach) z = Math.Max(z, c[j].Z + Math.Sqrt(reach * reach - d2));
                }
                if (z < bz) { bz = z; best = new Vec3(x, y, z); }
            }
            c.Add(best);
        }
        var plane = new PlaneHost(Vec3.Zero, new Vec3(0, 0, 1));
        double area = r.Sum(x => 4 * Math.PI * x * x);
        double h = Math.Max(r.Min() / 5, Math.Sqrt(area / (0.433 * b.TriangleBudget)));
        string info;
        var mesh = MobiusMesher.Build(c, r, h, out info, plane);
        var s = new FoamSolver(mesh, new SolverSettings { Rearrange = true }) { Host = plane };
        s.SetTargetRadii(r);
        log(string.Format(CI, "foam bed (seed {0}): {1} bubbles, r {2:F2}..{3:F2}, pile height {4:F2}, window {5:F1} x {6:F1}, cell {7:F3}, {8} triangles",
            e.Seed, n, r.Min(), r.Max(), c.Max(p => p.Z + 0), b.WinW, b.WinH, h, mesh.T.Count));
        Relax(s, log, 1200);
        Settle(s, log);
        return s;
    }

    public static Plot BedSheet(FoamSolver s, BedSettings b, string kind, HatchStyle baseSt, EditionSettings e, int seed)
    {
        var p = NewPlot(e);
        var rng = new Random(seed * 7 + 3);
        // Looking straight up through the plane. Window corners fit the frame exactly (same aspect).
        var v = new View { Forward = new Vec3(0, 0, 1), Seed = seed };
        double[] fr = { b.Margin / p.Width, b.Margin / p.Height, 1 - 2 * b.Margin / p.Width, 1 - 2 * b.Margin / p.Height };
        var fit = new List<Vec3> { new Vec3(-b.WinW / 2, -b.WinH / 2, 0), new Vec3(b.WinW / 2, b.WinH / 2, 0) };
        double x0 = b.Margin, y0 = b.Margin, x1 = p.Width - b.Margin, y1 = p.Height - b.Margin;

        // Every Plateau border, the footprint outlines on the plane included, in 3D (the foam is transparent:
        // no hidden-line removal), with its height above the plane.
        var skin = Frame(s, v, 0, fr, p, e, baseSt, fit);
        double zTop = s.Mesh.V.Max(q => q.Z);
        var borders = BorderClean.Clean(s.Mesh, s.TargetEdge, new BorderCleanSettings { Spacing = s.TargetEdge * 0.5, IncludeHost = true });
        var lines = new List<KeyValuePair<double, List<V2>>>();   // (height 0..1, sheet polyline)
        foreach (var bd in borders)
        {
            var pts = new List<Vec3>(bd.Points);
            if (bd.Closed && pts.Count > 0) pts.Add(pts[0]);
            if (pts.Count < 2) continue;
            double hgt = bd.OnHost ? 0 : Math.Max(0, Math.Min(1, pts.Average(q => q.Z) / zTop));
            foreach (var run in ClipRect(new List<List<V2>> { Smooth2(pts.Select(skin.Project).ToList()) }, x0, y0, x1, y1))
                lines.Add(new KeyValuePair<double, List<V2>>(hgt, run));
        }

        if (kind == "mist")
        {
            // Round 1 (0.30 mm dots, tone 0.9/0.75) filled the sheet an even grey (229k dots): seen from below the
            // whole sheet is foam, so the dots must be much sparser and keep the paper in the lights.
            var ms = new MarkSettings { DotMin = 0.36, DotGamma = 1.8 };
            // Interior walls (the nearest one at each point), sparser dots, behind.
            var classic = Clone(e); classic.Light = "classic";
            var inner = Frame(s, v, -1, fr, p, classic, Clone(baseSt), fit);
            var inMs = new MarkSettings { DotMin = ms.DotMin * 1.4, DotMax = ms.DotMax, DotGamma = ms.DotGamma, DotLength = ms.DotLength };
            p.Layer("inner").AddRange(ClipRect(Stipple(inner, inMs, rng, 0.66), x0, y0, x1, y1));
            // Borders as runs of dots: the footprint pattern on the plane tight, higher borders sparser.
            foreach (var kv in lines)
            {
                double sp = 0.28 + 0.55 * kv.Key;
                p.Layer(kv.Key == 0 ? "hatch" : "innerborders").AddRange(DotLines(new List<List<V2>> { kv.Value }, sp, 0.05, ms, rng));
            }
            // The outer skin (where the pile's outside faces us), denser dots.
            p.Layer("hatch").AddRange(ClipRect(Stipple(skin, ms, rng, 0.8), x0, y0, x1, y1));
        }
        else
        {
            // Lattice: thickness from height: up to 4 parallel passes 0.17 mm apart on the plane, 1 at the top.
            foreach (var kv in lines)
            {
                int passes = Math.Max(1, (int)Math.Round(1 + 3 * Math.Pow(1 - kv.Key, 1.6)));
                p.Layer(passes >= 3 ? "outline" : "innerborders").AddRange(Thicken(kv.Value, passes, 0.17));
            }
        }
        AddLabel(p, MarkSettings.NameOf(kind) + "  (FOAM BED " + kind + ")  " + s.Mesh.BubbleCount + " BUBBLES  SEED " + seed);
        return p;
    }

    // Parallel copies of a polyline, `passes` of them `gap` mm apart, centred on it.
    static List<List<V2>> Thicken(List<V2> l, int passes, double gap)
    {
        var res = new List<List<V2>>();
        var pts = Densify(l, 0.3).ToList();
        if (pts.Count < 2) return res;
        for (int k = 0; k < passes; k++)
        {
            double off = (k - (passes - 1) / 2.0) * gap;
            var o = new List<V2>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                int j = Math.Min(pts.Count - 1, i + 1), m = Math.Max(0, i - 1);
                var d = (pts[j] - pts[m]).Normalized();
                o.Add(pts[i] + new V2(-d.Y, d.X) * off);
            }
            if (k % 2 == 1) o.Reverse();   // boustrophedon: the pen comes back along the next pass
            res.Add(Hatcher.Simplify(o, 0.01));
        }
        return res;
    }

    // Polylines cut to a rectangle (point sampling at 0.2 mm; fine for drawing).
    static List<List<V2>> ClipRect(List<List<V2>> lines, double x0, double y0, double x1, double y1)
    {
        var res = new List<List<V2>>();
        foreach (var l in lines)
        {
            var cur = new List<V2>();
            foreach (var q in Densify(l, 0.2))
            {
                if (q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1) cur.Add(q);
                else { if (cur.Count >= 2) res.Add(Hatcher.Simplify(cur, 0.01)); cur = new List<V2>(); }
            }
            if (cur.Count >= 2) res.Add(Hatcher.Simplify(cur, 0.01));
        }
        return res;
    }
}

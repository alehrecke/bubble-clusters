using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// One edition = one seed: a relaxed random cluster, a fixed view and light, then
//   overview        the whole cluster as an opaque solid
//   specimen/NN     bubble NN alone, centred on the sheet
//   context/NN      the whole cluster in outline, bubble NN hatched
// each as SVG (pen layers, travel-optimised) + PNG preview, and manifest.json with everything needed to
// regenerate any sheet.
public class EditionSettings
{
    public int Seed = 1;
    public int Count = 0;              // bubbles (0 = random 9..15)
    public double RMin = 0.5, RMax = 1.6;
    public int DetailBatches = 0;      // Border Detail batches after relaxing (0 = none)
    public double PreviewPxPerMm = 5;
    public double RasterPxPerMm = 10;
    public double Inkmm_s = 25;        // pen-down speed for the time estimate
}

public static class Edition
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    // ---------- cluster ----------
    public static FoamSolver Generate(EditionSettings e, string cacheDir, Action<string> log)
    {
        var rng = new Random(e.Seed);
        int n = e.Count > 0 ? e.Count : rng.Next(9, 16);
        var r = new List<double>(); var c = new List<Vec3>();
        for (int i = 0; i < n; i++) r.Add(e.RMin + (e.RMax - e.RMin) * Math.Pow(rng.NextDouble(), 1.6));
        double R0 = 0.9 * Math.Pow(n, 1.0 / 3) * r.Average();
        for (int i = 0; i < n; i++)
        {
            Vec3 p;
            do p = new Vec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1); while (p.LengthSq > 1);
            c.Add(p * R0);
        }
        ClusterPacker.Pack(c, r, 1000, 0.01);
        // Cell size: about a fifth of the smallest radius, coarser if the mesh would get too big.
        double area = r.Sum(x => 4 * Math.PI * x * x);
        double h = Math.Max(r.Min() / 5, Math.Sqrt(area / (0.433 * 60000)));
        string objPath = Path.Combine(cacheDir, "cluster.obj"), radPath = Path.Combine(cacheDir, "cluster.txt");
        string info;
        var mesh = MobiusMesher.Build(c, r, h, out info);
        var s = new FoamSolver(mesh, new SolverSettings { Rearrange = true });
        s.SetTargetRadii(r);
        log(string.Format(CI, "cluster: {0} bubbles, r {1:F2}..{2:F2}, cell {3:F3}, {4} triangles", n, r.Min(), r.Max(), h, mesh.T.Count));
        Relax(s, log);
        if (e.DetailBatches > 0)
        {
            var drng = new Random(e.Seed * 31 + 7);
            for (int b = 0; b < e.DetailBatches; b++)
            {
                var picks = BorderDetail.Choose(s, new BorderDetailSettings { PerBatch = 6, Where = 1 }, drng);
                if (picks.Count == 0) break;
                s.RequestAddBatch(picks.Select(p => p.At).ToList(), picks.Select(p => p.Radius).ToList(), "border detail");
                for (int i = 0; i < 300; i++) s.Step();
                log("  detail batch " + b + ": " + picks.Count + " bubbles");
            }
            Relax(s, log);
        }
        return s;
    }

    static void Relax(FoamSolver s, Action<string> log)
    {
        double last = double.MaxValue;
        for (int block = 0; block < 40; block++)
        {
            for (int i = 0; i < 200; i++) s.Step();
            s.RearrangeLog.Clear();
            if (s.Steps > 600 && Math.Abs(last - s.Area) < 1e-4 * s.Area && !s.RebuildPending) break;
            last = s.Area;
        }
        log(string.Format(CI, "  relaxed: {0} steps, {1} rearrangements; {2}", s.Steps, s.Rearrangements, FoamAnalysis.Report(s).Split('\n')[2]));
    }

    // ---------- view ----------
    public class View { public Vec3 Forward, Light; public double Azimuth, Elevation; public int Seed; }

    public static View MakeView(int seed)
    {
        var rng = new Random(seed * 17 + 3);
        double az = rng.NextDouble() * 2 * Math.PI, el = (18 + 22 * rng.NextDouble()) * Math.PI / 180;
        var f = new Vec3(-Math.Cos(el) * Math.Cos(az), -Math.Cos(el) * Math.Sin(az), -Math.Sin(el));
        return new View { Forward = f, Azimuth = az, Elevation = el, Seed = seed };
    }

    static void Light(Scene sc) { sc.Light = (sc.R * -0.45 + sc.U * 0.7 + sc.F * -0.55).Normalized(); }

    // Per bubble: the axis its strokes ring around (the light direction, nudged a little per bubble).
    static Func<int, Vec3> Axes(Scene sc, int seed)
    {
        var cache = new Dictionary<int, Vec3>();
        return k =>
        {
            Vec3 a;
            if (cache.TryGetValue(k, out a)) return a;
            var r = new Random(seed * 101 + k);
            // The light direction, nudged a little per bubble so neighbouring bubbles' rings don't line up.
            a = (sc.Light + sc.R * ((r.NextDouble() - 0.5) * 0.25) + sc.U * ((r.NextDouble() - 0.5) * 0.25)).Normalized();
            cache[k] = a; return a;
        };
    }

    // ---------- sheets ----------
    public static Plot Overview(FoamSolver s, View v, HatchStyle st, EditionSettings e, out Scene scene)
    {
        var p = new Plot();
        var sc = Scene.Surface(s.Mesh, 0);
        sc.SetCamera(v.Forward, 22, 28, 166, 214);
        Light(sc);
        sc.Rasterize(e.RasterPxPerMm, p.Width, p.Height);
        p.Layer("hatch").AddRange(Hatcher.Hatch(sc, st, Axes(sc, v.Seed), new Random(v.Seed * 7 + 1)));
        p.Layer("outline").AddRange(Outlines(s, sc, 0));
        scene = sc;
        return p;
    }

    public static Plot Specimen(FoamSolver s, View v, int k, HatchStyle st, EditionSettings e)
    {
        var p = new Plot();
        var sc = Scene.Surface(s.Mesh, k);
        sc.SetCamera(v.Forward, 38, 52, 134, 170);
        Light(sc);
        sc.Rasterize(e.RasterPxPerMm, p.Width, p.Height);
        p.Layer("hatch").AddRange(Hatcher.Hatch(sc, st, Axes(sc, v.Seed), new Random(v.Seed * 7 + k)));
        p.Layer("outline").AddRange(Outlines(s, sc, k));
        return p;
    }

    public static Plot Context(FoamSolver s, View v, int k, HatchStyle st, EditionSettings e, Scene overview)
    {
        var p = new Plot();
        var sc = Scene.Surface(s.Mesh, k);
        // Same framing as the overview, so the bubble sits where it is in the cluster.
        var all = Scene.Surface(s.Mesh, 0);
        sc.SetCamera(v.Forward, 22, 28, 166, 214, all.Tris.SelectMany(i => new[] { s.Mesh.V[s.Mesh.T[i].A] }));
        Light(sc);
        sc.Rasterize(e.RasterPxPerMm, p.Width, p.Height);
        p.Layer("hatch").AddRange(Hatcher.Hatch(sc, st, Axes(sc, v.Seed), new Random(v.Seed * 7 + k)));
        p.Layer("outline").AddRange(Outlines(s, sc, k));
        // The rest of the cluster in outline (its own hidden lines removed), not drawn over the featured bubble.
        var ctx = Outlines(s, overview, 0);
        var keep = new List<List<V2>>();
        foreach (var line in ctx)
        {
            var run = new List<V2>();
            foreach (var q in Densify(line, 0.2))
            {
                if (!sc.Covered(q)) run.Add(q);
                else { if (run.Count >= 2) keep.Add(run); run = new List<V2>(); }
            }
            if (run.Count >= 2) keep.Add(run);
        }
        p.Layer("context").AddRange(keep.Select(l => Hatcher.Simplify(l, 0.02)));
        return p;
    }

    static IEnumerable<V2> Densify(List<V2> l, double step)
    {
        for (int i = 0; i + 1 < l.Count; i++)
        {
            int n = Math.Max(1, (int)Math.Ceiling((l[i + 1] - l[i]).Length / step));
            for (int j = 0; j < n; j++) yield return l[i] + (l[i + 1] - l[i]) * ((double)j / n);
        }
        if (l.Count > 0) yield return l[l.Count - 1];
    }

    // Plateau borders (cleaned: exact arcs) of the drawn surface + its silhouette, hidden parts removed.
    static List<List<V2>> Outlines(FoamSolver s, Scene sc, int bubble)
    {
        var borders = BorderClean.Clean(s.Mesh, s.TargetEdge, new BorderCleanSettings { Spacing = s.TargetEdge * 0.5, IncludeHost = false });
        var lines = new List<List<Vec3>>();
        foreach (var b in borders)
        {
            if (bubble > 0 ? !b.Bubbles.Contains(bubble) : !b.Bubbles.Contains(0)) continue;
            var pts = new List<Vec3>(b.Points);
            if (b.Closed && pts.Count > 0) pts.Add(pts[0]);
            lines.Add(pts);
        }
        double eps = 4 / (sc.S * sc.Res);
        var res = Hatcher.VisibleRuns(sc, lines, eps);
        // Silhouettes: visibility on the raw mesh outline (smoothing first pulls it inside the surface, where
        // the depth test hides it), with a looser tolerance (the surface beside an outline is edge-on), then
        // smoothed on the sheet to lose the mesh facets.
        foreach (var run in Hatcher.VisibleRuns(sc, sc.Silhouettes(), 1.0 / sc.S, 0.6)) res.Add(Smooth2(run));
        return res;
    }

    static List<V2> Smooth2(List<V2> p)
    {
        var cur = Densify(p, 0.25).ToList();
        if (cur.Count < 4) return cur;
        for (int it = 0; it < 6; it++)
        {
            var nx = new List<V2>(cur);
            for (int i = 1; i < cur.Count - 1; i++) nx[i] = (cur[i - 1] + cur[i] * 2 + cur[i + 1]) * 0.25;
            cur = nx;
        }
        return Hatcher.Simplify(cur, 0.02);
    }

    // ---------- output ----------
    public static Dictionary<string, object> Write(Plot p, string dir, string name, string title, EditionSettings e)
    {
        SheetIO.Optimize(p);
        Directory.CreateDirectory(dir);
        string svg = Path.Combine(dir, name + ".svg"), png = Path.Combine(dir, name + ".png");
        SheetIO.WriteSvg(p, svg, title);
        SheetIO.WritePng(p, png, e.PreviewPxPerMm, new Dictionary<string, double> { { "context", 0.45 } });
        double len = p.PenDownLength();
        int strokes = p.Strokes();
        return new Dictionary<string, object>
        {
            { "svg", svg }, { "png", png }, { "strokes", strokes },
            { "penDownMm", Math.Round(len) }, { "estMinutes", Math.Round((len / e.Inkmm_s + strokes * 0.25) / 60, 1) },
        };
    }

    public static string Json(object o)
    {
        if (o == null) return "null";
        if (o is string) return "\"" + ((string)o).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        if (o is bool) return (bool)o ? "true" : "false";
        if (o is int || o is long) return Convert.ToString(o, CI);
        if (o is double) return ((double)o).ToString("R", CI);
        var d = o as Dictionary<string, object>;
        if (d != null) return "{" + string.Join(",", d.Select(kv => Json(kv.Key) + ":" + Json(kv.Value))) + "}";
        var l = o as System.Collections.IEnumerable;
        if (l != null) return "[" + string.Join(",", l.Cast<object>().Select(Json)) + "]";
        return Json(o.ToString());
    }
}

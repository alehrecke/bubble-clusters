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
    public int Count = 0;              // bubbles (0 = random 16..26)
    // Radii: RMin..RMax, skewed toward small by SizeSkew (>1 = more small bubbles, a few big ones).
    // (Before 2026-10-18 evening: 9..15 bubbles, 0.5..1.6, skew 1.6, 60k triangles; old seeds look different now.)
    public double RMin = 0.3, RMax = 1.9, SizeSkew = 2.2;
    public string Shape = "ball";      // cluster silhouette: ball | chain | ring | raft | branch | buds (Shapes.cs)
    public int Satellites = 0;         // free-floating extras around the cluster: single bubbles and small clusters (Shapes.cs)
    public int TriangleBudget = 90000;
    public bool PlotterCopy = true;    // also write *_plotter-landscape.svg for portrait sheets
    public int DetailBatches = 0;      // Border Detail batches after relaxing (0 = none)
    public double PreviewPxPerMm = 5;
    public double RasterPxPerMm = 10;
    public double Inkmm_s = 25;        // pen-down speed for the time estimate
    public double SheetW = 210, SheetH = 297;
    public string Light = "classic";   // classic (soft Lambert) | dramatic (raking key light, cast shadows, ambient occlusion)
    // Context sheets: "visible" = the rest of the cluster with hidden lines removed (front borders + silhouette);
    // "all" = its whole Plateau-border network, back borders too (an X-ray of the foam); "dash" = back borders dashed.
    public string ContextLines = "visible";
    public int Variant = 0;            // which variant of the seed settled (0 = the seed itself; see Edition.Generate)
    public bool Outline = true;        // Plateau-border arcs + silhouettes   // one sheet (A4); A5 tiles for 4-up A3 pages
}

public static partial class Edition
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    // ---------- cluster ----------
    // A cluster is only used once it has settled: relaxed AND no pinched films (two films of a border folded
    // together, which plot as thorns). Seed 8150 on 2026-10-19 relaxed with 0 rearrangements and three thorns.
    // If forced rearrangements can't clear the pinches, the next variant of the seed is tried (seed*1000+1, ...);
    // the variant used is in e.Variant / the manifest, so the edition still regenerates exactly.
    public static FoamSolver Generate(EditionSettings e, string cacheDir, Action<string> log)
    {
        FoamSolver best = null; Stress bestStress = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int seed = attempt == 0 ? e.Seed : e.Seed * 1000 + attempt;
            var s = GenerateOne(e, seed, log);
            var st = Settle(s, log);
            if (st.Settled) { e.Variant = attempt; return s; }
            if (best == null || st.PinchedBorders < bestStress.PinchedBorders) { best = s; bestStress = st; e.Variant = attempt; }
            log("  not settled (" + st + "); trying variant " + (attempt + 1) + " of seed " + e.Seed);
        }
        log("  WARNING: no variant settled cleanly; using variant " + e.Variant + " (" + bestStress + ")");
        return best;
    }

    static FoamSolver GenerateOne(EditionSettings e, int seed, Action<string> log)
    {
        var rng = new Random(seed);
        int n = e.Count > 0 ? e.Count : rng.Next(16, 27);
        var r = new List<double>(); var c = new List<Vec3>();
        for (int i = 0; i < n; i++) r.Add(e.RMin + (e.RMax - e.RMin) * Math.Pow(rng.NextDouble(), e.SizeSkew));
        if (e.Shape != "ball") ShapePack(e.Shape, c, r, rng);
        else
        {
            double R0 = 0.9 * Math.Pow(n, 1.0 / 3) * r.Average();
            for (int i = 0; i < n; i++)
            {
                Vec3 p;
                do p = new Vec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1); while (p.LengthSq > 1);
                c.Add(p * R0);
            }
            ClusterPacker.Pack(c, r, 1000, 0.01);
        }
        if (e.Satellites > 0) AddSatellites(c, r, e.Satellites, rng);
        // Cell size: about a fifth of the smallest radius, coarser if the mesh would get too big.
        double area = r.Sum(x => 4 * Math.PI * x * x);
        double h = Math.Max(r.Min() / 5, Math.Sqrt(area / (0.433 * e.TriangleBudget)));
        string info;
        var mesh = MobiusMesher.Build(c, r, h, out info);
        var s = new FoamSolver(mesh, new SolverSettings { Rearrange = true });
        s.SetTargetRadii(r);
        log(string.Format(CI, "cluster (seed {5}): {0} bubbles, r {1:F2}..{2:F2}, cell {3:F3}, {4} triangles", n, r.Min(), r.Max(), h, mesh.T.Count, seed));
        Relax(s, log);
        if (e.DetailBatches > 0)
        {
            var drng = new Random(seed * 31 + 7);
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

    static void Relax(FoamSolver s, Action<string> log, int minSteps = 600)
    {
        double last = double.MaxValue;
        int start = s.Steps;
        for (int block = 0; block < 40; block++)
        {
            for (int i = 0; i < 200; i++) s.Step();
            s.RearrangeLog.Clear();
            if (s.Steps - start > minSteps && Math.Abs(last - s.Area) < 1e-4 * s.Area && !s.RebuildPending) break;
            last = s.Area;
        }
        log(string.Format(CI, "  relaxed: {0} steps, {1} rearrangements; {2}", s.Steps, s.Rearrangements, FoamAnalysis.Report(s).Split('\n')[2]));
    }

    public class Stress
    {
        public int PinchedEdges, PinchedBorders;
        public double WorstAngle = 180;    // smallest angle between two films at any border edge (ideal 120)
        public bool Settled { get { return PinchedBorders == 0; } }
        public override string ToString()
        {
            return string.Format(CI, "{0} pinched borders ({1} edges), smallest film angle {2:F0} deg", PinchedBorders, PinchedEdges, WorstAngle);
        }
    }

    // Pinched film = at a border edge, two of the three films meet at under PinchDeg (ideal 120). A border
    // counts as pinched when at least two of its edges are (one alone is usually a single bad triangle).
    public static Stress MeasureStress(FoamSolver s, double pinchDeg = 30)
    {
        var m = s.Mesh;
        var st = new Stress();
        var perBorder = new Dictionary<string, int>();
        foreach (var kv in m.EdgeTris())
        {
            if (kv.Value.Count != 3) continue;
            // Edges touching a host (the water plane, the paper): a contact line, not a Plateau border; no 120 deg rule.
            if (kv.Value.Any(ti => FoamMesh.IsSolid(m.T[ti]))) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            Vec3 ed = (m.V[b] - m.V[a]).Normalized(), mid = (m.V[a] + m.V[b]) * 0.5;
            var dirs = new Vec3[3]; var regions = new SortedSet<int>();
            for (int k = 0; k < 3; k++)
            {
                var t = m.T[kv.Value[k]];
                regions.Add(t.Neg); regions.Add(t.Pos);
                int o = t.A != a && t.A != b ? t.A : (t.B != a && t.B != b ? t.B : t.C);
                Vec3 d = m.V[o] - mid;
                dirs[k] = (d - ed * Vec3.Dot(d, ed)).Normalized();
            }
            double mn = 180;
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    mn = Math.Min(mn, Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(dirs[i], dirs[j])))) * 180 / Math.PI);
            st.WorstAngle = Math.Min(st.WorstAngle, mn);
            if (mn >= pinchDeg) continue;
            st.PinchedEdges++;
            string key = string.Join("-", regions);
            perBorder[key] = perBorder.ContainsKey(key) ? perBorder[key] + 1 : 1;
        }
        st.PinchedBorders = perBorder.Count(kv => kv.Value >= 2);
        return st;
    }

    // Relaxed -> check for pinched films -> forced rearrangement + relax, up to 6 rounds.
    static Stress Settle(FoamSolver s, Action<string> log)
    {
        var st = MeasureStress(s);
        for (int round = 0; round < 6 && !st.Settled; round++)
        {
            log("  settle round " + (round + 1) + ": " + st + " -> forced rearrangement");
            s.ForceRearrange("settle: " + st.PinchedBorders + " pinched borders");
            s.RearrangeLog.Clear();
            Relax(s, log, 400);
            st = MeasureStress(s);
        }
        log("  settle: " + (st.Settled ? "clean (" + st + ")" : "still pinched (" + st + ")"));
        return st;
    }

    // ---------- view ----------
    public class View { public Vec3 Forward, Light; public double Azimuth, Elevation; public int Seed; }

    // azDeg / elDeg (NaN = from the seed): camera direction, azimuth around Z from +X, elevation above the XY plane.
    public static View MakeView(int seed, double azDeg = double.NaN, double elDeg = double.NaN)
    {
        var rng = new Random(seed * 17 + 3);
        double az = rng.NextDouble() * 2 * Math.PI, el = (18 + 22 * rng.NextDouble()) * Math.PI / 180;
        if (!double.IsNaN(azDeg)) az = azDeg * Math.PI / 180;
        if (!double.IsNaN(elDeg)) el = elDeg * Math.PI / 180;
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
    // Drawing frames as fractions of the sheet (x, y, w, h), so the same layout works on any sheet size.
    static readonly double[] OverviewFrame = { 0.105, 0.094, 0.79, 0.72 };
    static readonly double[] SpecimenFrame = { 0.181, 0.175, 0.638, 0.572 };

    static Plot NewPlot(EditionSettings e) { return new Plot { Width = e.SheetW, Height = e.SheetH }; }

    static Scene Frame(FoamSolver s, View v, int bubble, double[] fr, Plot p, EditionSettings e, HatchStyle st, IEnumerable<Vec3> fit = null)
    {
        var sc = Scene.Surface(s.Mesh, bubble);
        // Reframe (Reframe.cs) can override the overview frame and what the camera fits to.
        if (FrameOverride != null && fr == OverviewFrame) fr = FrameOverride;
        if (FitOverride != null) fit = FitOverride;
        sc.SetCamera(v.Forward, fr[0] * p.Width, fr[1] * p.Height, fr[2] * p.Width, fr[3] * p.Height, fit);
        Light(sc);
        sc.Rasterize(e.RasterPxPerMm, p.Width, p.Height);
        if (st != null && st.Field == "border") sc.BuildBorderField();
        if (st != null && e.Light == "dramatic")
            Lighting.Setup(sc, (sc.R * -0.72 + sc.U * 0.5 + sc.F * -0.48).Normalized(), new LightSettings());
        if (st != null && st.Swirl > 0) sc.BuildSwirl(v.Seed, st.Swirl, 8 * s.TargetEdge);
        return sc;
    }

    // Debug: the tone (darkness) the hatcher sees over the overview, as an image.
    public static void ToneImage(FoamSolver s, View v, EditionSettings e, string path, double pxPerMm = 4)
    {
        var p = NewPlot(e);
        var sc = Frame(s, v, 0, OverviewFrame, p, e, new HatchStyle());
        int w = (int)(p.Width * pxPerMm), h = (int)(p.Height * pxPerMm);
        var ink = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var q = new V2((x + 0.5) / pxPerMm, (y + 0.5) / pxPerMm);
                int si; Vec3 pos, n;
                if (!sc.Query(q, out si, out pos, out n)) continue;
                ink[y * w + x] = (float)(sc.Lit != null ? 1 - sc.Lit(si, q, pos, n) : 1 - Math.Max(0, Vec3.Dot(n, sc.Light)));
            }
        SheetIO.WriteInk(ink, w, h, path);
    }

    public static Plot Overview(FoamSolver s, View v, HatchStyle st, EditionSettings e)
    {
        var p = NewPlot(e);
        var sc = Frame(s, v, 0, OverviewFrame, p, e, st);
        p.Layer("hatch").AddRange(Hatcher.Hatch(sc, st, Axes(sc, v.Seed), new Random(v.Seed * 7 + 1)));
        if (e.Outline) p.Layer("outline").AddRange(Outlines(s, sc, 0));
        if (st.Hidden != null) p.Layer("hidden").AddRange(Hidden(s, sc, 0, st.Hidden == "dash"));
        return p;
    }

    public static Plot Specimen(FoamSolver s, View v, int k, HatchStyle st, EditionSettings e)
    {
        var p = NewPlot(e);
        var sc = Frame(s, v, k, SpecimenFrame, p, e, st);
        p.Layer("hatch").AddRange(Hatcher.Hatch(sc, st, Axes(sc, v.Seed), new Random(v.Seed * 7 + k)));
        if (e.Outline) p.Layer("outline").AddRange(Outlines(s, sc, k));
        if (st.Hidden != null) p.Layer("hidden").AddRange(Hidden(s, sc, k, st.Hidden == "dash"));
        return p;
    }

    public static Plot Context(FoamSolver s, View v, int k, HatchStyle st, EditionSettings e)
    {
        var p = NewPlot(e);
        // Same framing as the overview, so the bubble sits where it is in the cluster.
        var all = Frame(s, v, 0, OverviewFrame, p, e, null);
        var fit = all.Tris.Select(i => s.Mesh.V[s.Mesh.T[i].A]);
        var sc = Frame(s, v, k, OverviewFrame, p, e, st, fit);
        p.Layer("hatch").AddRange(Hatcher.Hatch(sc, st, Axes(sc, v.Seed), new Random(v.Seed * 7 + k)));
        if (e.Outline) p.Layer("outline").AddRange(Outlines(s, sc, k));
        if (st.Hidden != null) p.Layer("hidden").AddRange(Hidden(s, sc, k, st.Hidden == "dash"));
        // The rest of the cluster in outline, not drawn over the featured bubble: by default its own hidden lines
        // removed; with ContextLines all/dash also every border behind the front surface (solid or dashed).
        var context = Outlines(s, all, 0);
        if (e.ContextLines == "all" || e.ContextLines == "dash") context.AddRange(Hidden(s, all, 0, e.ContextLines == "dash"));
        var keep = new List<List<V2>>();
        foreach (var line in context)
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

    // Several sheets tiled cols x rows onto one page (each tile keeps its own size and line spacing), plus
    // short cut marks between tiles on their own layer ("marks": plot it, or don't).
    public static Plot Tile(IList<Plot> tiles, double pageW, double pageH, int cols, int rows)
    {
        var page = new Plot { Width = pageW, Height = pageH };
        double tw = pageW / cols, th = pageH / rows;
        foreach (var name in new[] { "hatch", "outline", "context" }) page.Layer(name);
        for (int i = 0; i < tiles.Count && i < cols * rows; i++)
        {
            var off = new V2((i % cols) * tw, (i / cols) * th);
            foreach (var name in tiles[i].LayerOrder)
                page.Layer(name).AddRange(tiles[i].Layers[name].Select(l => l.Select(q => q + off).ToList()));
        }
        var marks = page.Layer("marks");
        const double m = 4;
        for (int c = 1; c < cols; c++)
            for (int r = 0; r <= rows; r++)
            {
                double x = c * tw, y = r * th;
                if (r > 0) marks.Add(new List<V2> { new V2(x, y - m), new V2(x, y) });
                if (r < rows) marks.Add(new List<V2> { new V2(x, y), new V2(x, y + m) });
            }
        for (int r = 1; r < rows; r++)
            for (int c = 0; c <= cols; c++)
            {
                double x = c * tw, y = r * th;
                if (c > 0) marks.Add(new List<V2> { new V2(x - m, y), new V2(x, y) });
                if (c < cols) marks.Add(new List<V2> { new V2(x, y), new V2(x + m, y) });
            }
        return page;
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
    // Plateau borders BEHIND the drawn surface: for the overview the cluster's whole internal border network,
    // for one bubble its back-side borders. A looser tolerance than the visible test leaves a small halo gap
    // where a hidden line passes behind a visible one.
    static List<List<V2>> Hidden(FoamSolver s, Scene sc, int bubble, bool dashed)
    {
        var borders = BorderClean.Clean(s.Mesh, s.TargetEdge, new BorderCleanSettings { Spacing = s.TargetEdge * 0.5, IncludeHost = false });
        var lines = new List<List<Vec3>>();
        foreach (var b in borders)
        {
            if (bubble > 0 && !b.Bubbles.Contains(bubble)) continue;
            var pts = new List<Vec3>(b.Points);
            if (b.Closed && pts.Count > 0) pts.Add(pts[0]);
            lines.Add(pts);
        }
        var runs = Hatcher.VisibleRuns(sc, lines, 1.2 / sc.S, 0.6, true).Select(Smooth2).ToList();
        if (!dashed) return runs;
        var dashes = new List<List<V2>>();
        foreach (var r in runs)
        {
            var pts = Densify(r, 0.1).ToList();
            double along = 0; var cur = new List<V2>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) along += (pts[i] - pts[i - 1]).Length;
                bool on = along % 1.4 < 0.8;            // 0.8 mm dash, 0.6 mm gap
                if (on) cur.Add(pts[i]);
                else if (cur.Count > 0) { if (cur.Count >= 2) dashes.Add(cur); cur = new List<V2>(); }
            }
            if (cur.Count >= 2) dashes.Add(cur);
        }
        return dashes;
    }

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
        // One SVG per sheet, the plotter-ready one (Alex, 2026-10-19): portrait sheets are written turned 90 degrees
        // for the AxiDraw A3 (load the paper landscape); the PNG preview stays upright.
        string png = Path.Combine(dir, name + ".png"), svg;
        if (e.PlotterCopy && p.Height > p.Width)
        {
            svg = Path.Combine(dir, name + "_plotter-landscape.svg");
            SheetIO.WriteSvg(SheetIO.Rotated(p), svg, title + " [rotated for AxiDraw A3, load paper landscape]");
        }
        else
        {
            svg = Path.Combine(dir, name + ".svg");
            SheetIO.WriteSvg(p, svg, title);
        }
        SheetIO.WritePng(p, png, e.PreviewPxPerMm, new Dictionary<string, double> { { "context", 0.45 }, { "hidden", 0.5 }, { "inner", 0.55 }, { "innerborders", 0.6 }, { "background", 0.8 }, { "_label", 0.9 }, { "fine", 0.5 } });
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

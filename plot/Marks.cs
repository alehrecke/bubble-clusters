using System;
using System.Collections.Generic;
using System.Linq;

// Experimental mark making (Alex, 2026-10-19: "more natural, some noise in the linework", stippling,
// cross-hatching, transparency that shows the cells behind the outer wall). Kinds:
//   stipple        blue-noise dots, spacing DotMin / tone^DotGamma (steep: solid darks, sparse lights), wavering density
//   cross          engraving cross-hatch: the surface-following pass, then crossing passes over the darker tones only;
//                  each film's crossing angle jittered, spacing wandering with noise, pass angles drawn per seed
//   natural        the house style with hand noise: sideways wander, small breaks
//   wild           natural with the wobble cranked up (0.5 mm, longer wavelength)
//   inkdrift       strokes pushed by a slow swirling flow, strongest at the cluster's edge, sliding further the
//                  longer they run; dark strokes get a pooled second pass
//   glass          see-through skin: interior walls hatched underneath (own layer), the internal Plateau borders,
//                  the skin lighter on top
//   glass-stipple  the same with the interior walls in stipple (walls in dots, skin in lines)
// --wobble a (mm) overrides any kind's hand noise.
public class MarkSettings
{
    public string Kind = "natural";
    public double Wobble = -1;             // perpendicular wander, mm (-1 = the kind's default)
    public double WobbleWavelength = 3.5;  // mm along the stroke
    public double Breaks = 0.04;           // chance per mm of a small gap in a line
    // stipple (round 1 used 0.42 / tone^0.75: too even, no density shift)
    public double DotMin = 0.30;           // mm between dots in the darkest tone (the pen width: darks close up)
    public double DotMax = 4.5;            // mm in the lightest tone that still gets dots
    public double DotGamma = 1.6;
    public double DotLength = 0.22;        // each dot is a tiny tick so the pen leaves a mark
    // ink drift
    public double Drift = 2.2;             // flow displacement at the cluster's edge, mm
    public string Tag = "";                // extra text for the preview label (e.g. the round)

    // Names for the settings (Alex, 2026-10-19: "give each of these settings a name"), shown on the PNG previews
    // and accepted on the command line in place of the kind.
    public static readonly Dictionary<string, string> Names = new Dictionary<string, string>
    {
        { "stipple", "Sand" }, { "cross", "Engrave" }, { "natural", "Hand" }, { "wild", "Tremor" },
        { "wild-bg", "Tremor Sky" }, { "inkdrift", "Drift" }, { "glass", "Lens" }, { "glass-stipple", "Ghost" },
        { "glass-dots", "Frost" }, { "glass-pure", "Mist" }, { "meniscus", "Meniscus" }, { "wet-lattice", "Wet Lattice" }, { "deep-stipple", "Deep Stipple" }, { "depth-hatch", "Depth Hatch" }, { "windows", "Windows" }, { "contour", "Contour" }, { "thread", "Thread" }, { "froth", "Froth" }, { "wave", "Wave" }, { "deep-windows", "Deep Windows" }, { "lattice", "Lattice" },
    };

    public static string Resolve(string kindOrName)
    {
        string k = kindOrName.Trim();
        foreach (var kv in Names) if (string.Equals(kv.Value, k, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        return k.ToLowerInvariant();
    }

    public static string NameOf(string kind) { string n; return Names.TryGetValue(kind, out n) ? n : kind; }
}

public static partial class Edition
{
    public static Plot MarkOverview(FoamSolver s, View v, HatchStyle baseSt, EditionSettings e, MarkSettings ms)
    {
        var p = MarkOverviewRaw(s, v, baseSt, e, ms);
        AddLabel(p, MarkSettings.NameOf(ms.Kind) + "  (" + ms.Kind + ")" + (ms.Tag != "" ? "  " + ms.Tag : "") + "  SEED " + v.Seed);
        return p;
    }

    // Preview-only label along the bottom-left of the sheet.
    public static void AddLabel(Plot p, string text, double h = 3.2)
    {
        p.Layer("_label").AddRange(Label.Text(text, 6, p.Height - 6 - h, h));
    }

    static Plot MarkOverviewRaw(FoamSolver s, View v, HatchStyle baseSt, EditionSettings e, MarkSettings ms)
    {
        var p = NewPlot(e);
        var rng = new Random(v.Seed * 7 + 1);
        double wob = ms.Wobble >= 0 ? ms.Wobble
                   : ms.Kind == "natural" ? 0.12 : ms.Kind.StartsWith("wild") ? 0.5 : ms.Kind == "cross" ? 0.05
                   : ms.Kind == "inkdrift" ? 0.1 : ms.Kind.StartsWith("glass") ? 0.06 : 0;
        if (ms.Kind.StartsWith("wild")) { ms.WobbleWavelength = 6; ms.Breaks = 0.015; }
        // Round 3: ink drift calmer (2.2 mm was too much) and without breaks (they read as periodic white spots).
        if (ms.Kind == "inkdrift") { ms.Drift = 1.1; ms.Breaks = 0; }

        if (ms.Kind.StartsWith("glass"))
        {
            // Interior walls first (own pen layer, plotted first), framed exactly like the skin.
            var skin = Frame(s, v, 0, OverviewFrame, p, e, null);
            var fit = skin.Tris.Select(i => s.Mesh.V[s.Mesh.T[i].A]).ToList();
            var classic = Clone(e); classic.Light = "classic";
            var inSt = Clone(baseSt); inSt.Field = "facet"; inSt.Hyper = false; inSt.ToneScale = 0.8;
            inSt.SepMin *= 1.25; inSt.SepMax *= 1.1;
            var inner = Frame(s, v, -1, OverviewFrame, p, classic, inSt, fit);
            if (ms.Kind == "glass-pure")
            {
                // Dots only: the borders and silhouette are drawn as dense runs of dots instead of lines
                // (visible ones tight, the internal network behind sparser).
                var inMs = new MarkSettings { DotMin = ms.DotMin * 1.25, DotMax = ms.DotMax, DotGamma = ms.DotGamma, DotLength = ms.DotLength };
                p.Layer("inner").AddRange(Stipple(inner, inMs, rng, 0.8));
                p.Layer("innerborders").AddRange(DotLines(Hidden(s, skin, 0, false), 0.55, 0.06, ms, rng));
                var dskin = Frame(s, v, 0, OverviewFrame, p, e, Clone(baseSt), fit);
                p.Layer("hatch").AddRange(Stipple(dskin, ms, rng, 1.05));
                p.Layer("hatch").AddRange(DotLines(Outlines(s, dskin, 0), 0.32, 0.05, ms, rng));
                return p;
            }
            if (ms.Kind == "glass-dots")
            {
                // All stipple: the walls in slightly sparser dots than the skin, so the skin still reads in front.
                var inMs = new MarkSettings { DotMin = ms.DotMin * 1.25, DotMax = ms.DotMax, DotGamma = ms.DotGamma, DotLength = ms.DotLength };
                p.Layer("inner").AddRange(Stipple(inner, inMs, rng, 0.8));
                p.Layer("innerborders").AddRange(Hidden(s, skin, 0, false));
                var dskin = Frame(s, v, 0, OverviewFrame, p, e, Clone(baseSt), fit);
                p.Layer("hatch").AddRange(Stipple(dskin, ms, rng, 1.05));
                if (e.Outline) p.Layer("outline").AddRange(Outlines(s, dskin, 0));
                return p;
            }
            if (ms.Kind == "glass-stipple")
                p.Layer("inner").AddRange(Stipple(inner, ms, rng, 0.85));
            else
                p.Layer("inner").AddRange(Wobble(Hatcher.Hatch(inner, inSt, Axes(inner, v.Seed), new Random(v.Seed * 7 + 2)), wob, ms, rng));
            // The internal border network (behind the skin), with the walls.
            p.Layer("innerborders").AddRange(Hidden(s, skin, 0, false));
            // The skin on top, lighter, so the walls read through it.
            var outSt = Clone(baseSt); outSt.ToneScale = ms.Kind == "glass-stipple" ? 0.55 : 0.6;
            var outer = Frame(s, v, 0, OverviewFrame, p, e, outSt, fit);
            p.Layer("hatch").AddRange(Wobble(Hatcher.Hatch(outer, outSt, Axes(outer, v.Seed), new Random(v.Seed * 7 + 1)), wob, ms, rng));
            if (e.Outline) p.Layer("outline").AddRange(Outlines(s, outer, 0));
            return p;
        }

        var sc = Frame(s, v, 0, OverviewFrame, p, e, baseSt);
        if (IsMore(ms.Kind))
            p.Layer("hatch").AddRange(MoreMarks(ms.Kind, s, sc, rng));
        else if (ms.Kind == "stipple")
            p.Layer("hatch").AddRange(Stipple(sc, ms, rng, 1));
        else if (ms.Kind == "inkdrift")
        {
            var lines = Hatcher.Hatch(sc, baseSt, Axes(sc, v.Seed), new Random(v.Seed * 7 + 1));
            p.Layer("hatch").AddRange(InkDrift(Wobble(lines, wob, ms, rng, false), sc, ms, rng, true));
            // The outline stays smooth and in place (Alex, round 4): only the hatching drifts.
            if (e.Outline) p.Layer("outline").AddRange(Outlines(s, sc, 0));
            return p;
        }
        else if (ms.Kind == "cross")
        {
            // Each pass on its own spacing grid (passes cross freely). The first pass is opened up, since the
            // crossing passes add the darkness back. Randomness: per-film angle jitter, spacing noise, pass
            // angles and thresholds drawn per seed, more breaks.
            var prng = new Random(v.Seed * 13 + 5);
            var passes = new[]
            {
                // Round 3: denser and finer again (round 2 opened the passes 1.4-1.6x and lost the delicacy).
                new { Rot = 0.0, From = 0.0, Open = 1.1, Jit = 4.0 },
                new { Rot = 60 + 25 * prng.NextDouble(), From = 0.36 + 0.08 * prng.NextDouble(), Open = 1.2, Jit = 10.0 },
                new { Rot = -(25 + 25 * prng.NextDouble()), From = 0.66 + 0.08 * prng.NextDouble(), Open = 1.25, Jit = 14.0 },
            };
            int i = 0;
            foreach (var ps in passes)
            {
                var st = Clone(baseSt); st.RotateDeg = ps.Rot; st.ToneFrom = ps.From;
                st.RotateJitter = ps.Jit; st.SepJitter = 0.15; st.JitterSeed = v.Seed * 3 + i;
                st.SepMin *= ps.Open; st.SepMax *= ps.Open; st.HyperMin *= ps.Open; st.HyperMax *= ps.Open;
                p.Layer("hatch").AddRange(Wobble(Hatcher.Hatch(sc, st, Axes(sc, v.Seed), new Random(v.Seed * 7 + 11 * ++i)), wob, ms, rng));
            }
        }
        else
            p.Layer("hatch").AddRange(Wobble(Hatcher.Hatch(sc, baseSt, Axes(sc, v.Seed), new Random(v.Seed * 7 + 1)), wob, ms, rng));
        if (ms.Kind == "wild-bg")
        {
            // No outlines: the hatched ground defines the silhouette.
            p.Layer("background").AddRange(Wobble(Background(sc, p, v.Seed), wob * 0.6, ms, rng, false));
            return p;
        }
        if (e.Outline) p.Layer("outline").AddRange(Wobble(Outlines(s, sc, 0), wob * 0.5, ms, rng, false));
        return p;
    }

    // ---------- stipple ----------
    // Dart throwing with a tone-dependent radius (variable-radius Poisson disc): every dot keeps at least
    // r(tone) from the others, so dots pack tight in the darks and spread in the lights; a low-frequency
    // noise makes the density waver like hand stippling.
    static List<List<V2>> Stipple(Scene sc, MarkSettings ms, Random rng, double toneScale, Func<V2, double> weight = null)
    {
        var dots = new List<List<V2>>();
        double cell = ms.DotMin * 0.7;
        var grid = new Dictionary<long, List<V2>>(LongKey.Comparer);
        Func<long, long, long> key = (x, y) => unchecked((x * 73856093L) ^ (y * 19349663L));
        double w = sc.W / sc.Res, h = sc.H / sc.Res;
        int tries = (int)(w * h / (ms.DotMin * ms.DotMin) * 2.5);
        for (int t = 0; t < tries; t++)
        {
            var q = new V2(rng.NextDouble() * w, rng.NextDouble() * h);
            int si; Vec3 pos, n;
            if (!sc.Query(q, out si, out pos, out n)) continue;
            if (sc.TwoSided && Vec3.Dot(n, sc.F) > 0) n = n * -1;
            double tone = sc.Lit != null ? 1 - sc.Lit(si, q, pos, n) : 1 - Math.Max(0, Vec3.Dot(n, sc.Light));
            if (sc.Lit == null) tone = Math.Min(1, tone + 0.25 * Math.Pow(1 - Math.Abs(Vec3.Dot(n, sc.F)), 2));
            if (weight != null) tone *= weight(q);
            tone = Math.Max(0, (tone * toneScale - 0.08) / 0.92);
            if (tone < 0.02) continue;
            double r = Math.Min(ms.DotMax, ms.DotMin / Math.Pow(tone, ms.DotGamma));
            r *= 1 + 0.22 * Noise(q.X / 9, q.Y / 9, 3) + 0.08 * Noise(q.X / 2.2, q.Y / 2.2, 5);
            if (r >= ms.DotMax) continue;
            long cx = (long)Math.Floor(q.X / cell), cy = (long)Math.Floor(q.Y / cell);
            int rr = (int)Math.Ceiling(r / cell);
            bool ok = true;
            for (long gy = cy - rr; gy <= cy + rr && ok; gy++)
                for (long gx = cx - rr; gx <= cx + rr && ok; gx++)
                {
                    List<V2> l;
                    if (!grid.TryGetValue(key(gx, gy), out l)) continue;
                    foreach (var o in l) if ((o - q).Length < r) { ok = false; break; }
                }
            if (!ok) continue;
            List<V2> cl;
            long k = key(cx, cy);
            if (!grid.TryGetValue(k, out cl)) { cl = new List<V2>(2); grid[k] = cl; }
            cl.Add(q);
            double a = rng.NextDouble() * Math.PI, len = ms.DotLength * (0.7 + 0.6 * rng.NextDouble());
            var d = new V2(Math.Cos(a), Math.Sin(a)) * (len / 2);
            dots.Add(new List<V2> { q - d, q + d });
        }
        return dots;
    }

    // ---------- ink drift ----------
    // Strokes pushed by a slow swirling flow (angle from a large-scale noise), weak at the cluster centre and
    // up to ms.Drift mm at its edge; each stroke also slides further the longer it runs (the pen dragging ink),
    // and dark strokes get a second, slightly offset pass, as if the ink pooled there.
    static List<List<V2>> InkDrift(List<List<V2>> strokes, Scene sc, MarkSettings ms, Random rng, bool pool, double scale = 1)
    {
        double sx = 0, sy = 0; int cnt = 0;
        for (int y = 0; y < sc.H; y += 4)
            for (int x = 0; x < sc.W; x += 4)
            {
                var q = new V2((x + 0.5) / sc.Res, (y + 0.5) / sc.Res);
                if (sc.Covered(q)) { sx += q.X; sy += q.Y; cnt++; }
            }
        var c = new V2(sx / Math.Max(1, cnt), sy / Math.Max(1, cnt));
        double R = Math.Sqrt(cnt * 16 / (sc.Res * sc.Res) / Math.PI);
        Func<V2, V2> flow = q =>
        {
            double ang = Math.PI * 2 * Noise(q.X / 35, q.Y / 35, 11) + 0.6 * Noise(q.X / 9, q.Y / 9, 13);
            double r = (q - c).Length / Math.Max(R, 1e-6);
            double mag = scale * ms.Drift * Math.Min(1.6, r * r) * (0.65 + 0.35 * Noise(q.X / 14, q.Y / 14, 17));
            return new V2(Math.Cos(ang), Math.Sin(ang)) * mag;
        };
        var res = new List<List<V2>>(strokes.Count);
        foreach (var l in strokes)
        {
            var pts = Densify(l, 0.3).ToList();
            if (pts.Count < 2) { res.Add(l); continue; }
            double along = 0, slide = 0.012 * scale * (0.5 + rng.NextDouble());
            var o = new List<V2>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) along += (pts[i] - pts[i - 1]).Length;
                o.Add(pts[i] + flow(pts[i]) * (1 + slide * along));
            }
            res.Add(Hatcher.Simplify(o, 0.015));
            if (!pool) continue;
            var mid = pts[pts.Count / 2];
            int si; Vec3 pos, n;
            if (!sc.Query(mid, out si, out pos, out n)) continue;
            double tone = sc.Lit != null ? 1 - sc.Lit(si, mid, pos, n) : 1 - Math.Max(0, Vec3.Dot(n, sc.Light));
            if (tone < 0.62 || rng.NextDouble() > (tone - 0.62) * 2.2) continue;
            // The pooled second pass: ~0.18 mm sideways, a little shorter at both ends.
            var dbl = new List<V2>();
            int i0 = (int)(pts.Count * 0.15 * rng.NextDouble()), i1 = pts.Count - 1 - (int)(pts.Count * 0.2 * rng.NextDouble());
            for (int i = i0; i <= i1; i++)
            {
                int j = Math.Min(pts.Count - 1, i + 1), k = Math.Max(0, i - 1);
                var dir = (pts[j] - pts[k]).Normalized();
                dbl.Add(o[i] + new V2(-dir.Y, dir.X) * 0.18);
            }
            if (dbl.Count >= 2) res.Add(Hatcher.Simplify(dbl, 0.015));
        }
        return res;
    }

    // Lines redrawn as runs of dots, spacing mm apart with a little sideways scatter.
    static List<List<V2>> DotLines(List<List<V2>> lines, double spacing, double scatter, MarkSettings ms, Random rng)
    {
        var res = new List<List<V2>>();
        foreach (var l in lines)
        {
            double carry = spacing * rng.NextDouble();
            for (int i = 1; i < l.Count; i++)
            {
                var a = l[i - 1]; var d = l[i] - a; double len = d.Length;
                if (len < 1e-9) continue;
                var u = d * (1 / len); var nrm = new V2(-u.Y, u.X);
                double t = carry;
                for (; t < len; t += spacing * (0.8 + 0.4 * rng.NextDouble()))
                {
                    var q = a + u * t + nrm * (scatter * (rng.NextDouble() * 2 - 1));
                    double ang = rng.NextDouble() * Math.PI, h = ms.DotLength * (0.7 + 0.6 * rng.NextDouble()) / 2;
                    var o = new V2(Math.Cos(ang), Math.Sin(ang)) * h;
                    res.Add(new List<V2> { q - o, q + o });
                }
                carry = t - len;
            }
        }
        return res;
    }

    // ---------- hatched ground ----------
    // Near-horizontal lines behind the cluster, stopping short of it (a ~0.6 mm gap): a sky, not a wall
    // (round 4: no cast shadow). The tone darkens toward the top, with soft horizontally stretched cloud patches
    // and a slight darkening right at the silhouette so the lit edge reads without an outline. Lines come in nested families (every 8th, 4th, 2nd, all) switched on by tone, so the spacing goes
    // 4 / 2 / 1 / 0.5 mm. Ragged ends at the frame edge.
    static List<List<V2>> Background(Scene sc, Plot p, int seed)
    {
        var rng = new Random(seed * 31 + 7);
        double x0 = 0.06 * p.Width, y0 = 0.05 * p.Height, x1 = 0.94 * p.Width, y1 = 0.88 * p.Height;
        // Distance (mm) to the cluster on a 0.5 mm grid (chamfer transform).
        double g = 0.5;
        int gw = (int)Math.Ceiling((x1 - x0) / g) + 1, gh = (int)Math.Ceiling((y1 - y0) / g) + 1;
        var dist = new double[gw * gh];
        int cnt = 0;
        for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
            {
                bool c = sc.Covered(new V2(x0 + i * g, y0 + j * g));
                dist[j * gw + i] = c ? 0 : 1e9;
                if (c) cnt++;
            }
        Chamfer(dist, gw, gh, g);
        Func<V2, double> D = q =>
        {
            int i = (int)Math.Round((q.X - x0) / g), j = (int)Math.Round((q.Y - y0) / g);
            if (i < 0 || j < 0 || i >= gw || j >= gh) return 1e9;
            return dist[j * gw + i];
        };
        Func<V2, double> tone = q => 0.62 - 0.34 * (q.Y - y0) / (y1 - y0)
            + 0.2 * Noise(q.X / 40, q.Y / 12, seed + 41) + 0.07 * Noise(q.X / 9, q.Y / 4, seed + 43)
            + 0.12 * Math.Exp(-D(q) / 2.5);
        double ang = (-4 + 8 * rng.NextDouble()) * Math.PI / 180;
        var dir = new V2(Math.Cos(ang), Math.Sin(ang));
        var nrm = new V2(-dir.Y, dir.X);
        var mid = new V2((x0 + x1) / 2, (y0 + y1) / 2);
        double half = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / 2, s0 = 0.5, step = 0.25;
        var res = new List<List<V2>>();
        int kmax = (int)(half / s0);
        for (int k = -kmax; k <= kmax; k++)
        {
            int m = ((k % 8) + 8) % 8;
            double thr = m == 0 ? 0 : m == 4 ? 0.3 : m % 2 == 0 ? 0.48 : 0.66;
            double ins0 = 2.5 * rng.NextDouble(), ins1 = 2.5 * rng.NextDouble();
            var cur = new List<V2>();
            for (double t = -half; t <= half; t += step)
            {
                var q = mid + nrm * (k * s0) + dir * t;
                bool inside = q.X > x0 + ins0 && q.X < x1 - ins1 && q.Y > y0 + ins0 && q.Y < y1 - ins1;
                bool on = inside && D(q) > 0.6 && tone(q) + 0.06 * Noise(q.X / 3, q.Y / 3, seed + k) >= thr;
                if (on) cur.Add(q);
                else { if (cur.Count >= 4) res.Add(cur); cur = new List<V2>(); }
            }
            if (cur.Count >= 4) res.Add(cur);
        }
        return res.Select(l => Hatcher.Simplify(l, 0.01)).ToList();
    }

    static void Chamfer(double[] d, int w, int h, double g)
    {
        double dg = g * Math.Sqrt(2);
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                double v = d[j * w + i];
                if (i > 0) v = Math.Min(v, d[j * w + i - 1] + g);
                if (j > 0) v = Math.Min(v, d[(j - 1) * w + i] + g);
                if (i > 0 && j > 0) v = Math.Min(v, d[(j - 1) * w + i - 1] + dg);
                if (i < w - 1 && j > 0) v = Math.Min(v, d[(j - 1) * w + i + 1] + dg);
                d[j * w + i] = v;
            }
        for (int j = h - 1; j >= 0; j--)
            for (int i = w - 1; i >= 0; i--)
            {
                double v = d[j * w + i];
                if (i < w - 1) v = Math.Min(v, d[j * w + i + 1] + g);
                if (j < h - 1) v = Math.Min(v, d[(j + 1) * w + i] + g);
                if (i < w - 1 && j < h - 1) v = Math.Min(v, d[(j + 1) * w + i + 1] + dg);
                if (i > 0 && j < h - 1) v = Math.Min(v, d[(j + 1) * w + i - 1] + dg);
                d[j * w + i] = v;
            }
    }

    // ---------- hand noise ----------
    // Each stroke wanders sideways by a smooth noise (amplitude amp mm, wavelength ms.WobbleWavelength along the
    // stroke, its own phase per stroke), and lines break now and then (ms.Breaks per mm, 0.3-0.8 mm gaps).
    static List<List<V2>> Wobble(List<List<V2>> strokes, double amp, MarkSettings ms, Random rng, bool breaks = true)
    {
        if (amp <= 0) return strokes;
        var res = new List<List<V2>>(strokes.Count);
        foreach (var l in strokes)
        {
            var pts = Densify(l, 0.25).ToList();
            if (pts.Count < 2) { res.Add(l); continue; }
            double phase = rng.NextDouble() * 1000, along = 0;
            var cur = new List<V2>();
            double gapLeft = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) along += (pts[i] - pts[i - 1]).Length;
                int j = Math.Min(pts.Count - 1, i + 1), k = Math.Max(0, i - 1);
                var dir = (pts[j] - pts[k]).Normalized();
                var nrm = new V2(-dir.Y, dir.X);
                double off = amp * (Noise(along / ms.WobbleWavelength + phase, phase * 0.37, 7) + 0.35 * Noise(along / (ms.WobbleWavelength * 0.3) + phase, 1.3, 9));
                var q = pts[i] + nrm * off;
                double step = i > 0 ? (pts[i] - pts[i - 1]).Length : 0;
                if (gapLeft > 0) { gapLeft -= step; if (gapLeft <= 0) cur = new List<V2>(); continue; }
                if (breaks && ms.Breaks > 0 && along > 1 && rng.NextDouble() < ms.Breaks * step)
                {
                    if (cur.Count >= 2) res.Add(Hatcher.Simplify(cur, 0.015));
                    cur = new List<V2>();
                    gapLeft = 0.3 + 0.5 * rng.NextDouble();
                    continue;
                }
                cur.Add(q);
            }
            if (cur.Count >= 2) res.Add(Hatcher.Simplify(cur, 0.015));
        }
        return res;
    }

    static double Noise(double x, double y, int seed) { return Hatcher.Noise2(x, y, seed); }

    static HatchStyle Clone(HatchStyle s) { return (HatchStyle)s.GetType().GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(s, null); }
    static EditionSettings Clone(EditionSettings s) { return (EditionSettings)s.GetType().GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(s, null); }
}

using System;
using System.Collections.Generic;
using System.Linq;

// Experimental mark making (2026-10-19, Alex: "more natural, some noise in the linework", stippling,
// cross-hatching, and transparency that shows the cells behind the outer wall). One A3 overview per kind:
//   stipple  blue-noise dots, spacing from tone, density wavering with a low-frequency noise
//   cross    engraving cross-hatch: the surface-following pass, then crossing passes over the darker tones only
//   natural  the house style (mixed / border / dramatic) with hand noise: wobble, path drift, small breaks
//   glass    see-through skin: interior walls hatched underneath (own layer, other direction), outer skin light
//            on top, the internal Plateau borders drawn
// Any kind can take --wobble a (mm) to add the hand noise (natural = the house style + wobble).
public class MarkSettings
{
    public string Kind = "natural";
    public double Wobble = -1;           // perpendicular wander, mm (-1 = the kind's default)
    public double WobbleWavelength = 3.5;  // mm along the stroke
    public double Breaks = 0.04;         // chance per mm of a small gap in a line
    // stipple
    public double DotMin = 0.42;         // mm between dots in the darkest tone (a bit over the pen width)
    public double DotMax = 3.2;          // mm in the lightest tone that still gets dots
    public double DotLength = 0.22;      // each dot is a tiny tick so the pen leaves a mark
}

public static partial class Edition
{
    public static Plot MarkOverview(FoamSolver s, View v, HatchStyle baseSt, EditionSettings e, MarkSettings ms)
    {
        var p = NewPlot(e);
        var rng = new Random(v.Seed * 7 + 1);
        double wob = ms.Wobble >= 0 ? ms.Wobble : ms.Kind == "natural" ? 0.12 : ms.Kind == "cross" ? 0.07 : ms.Kind == "glass" ? 0.06 : 0;
        if (ms.Kind == "glass")
        {
            // Interior walls first (own pen layer, plotted first), framed exactly like the skin.
            var skin = Frame(s, v, 0, OverviewFrame, p, e, null);
            var fit = skin.Tris.Select(i => s.Mesh.V[s.Mesh.T[i].A]).ToList();
            var classic = Clone(e); classic.Light = "classic";
            var inSt = Clone(baseSt); inSt.Field = "facet"; inSt.Hyper = false; inSt.ToneScale = 0.8;
            inSt.SepMin *= 1.25; inSt.SepMax *= 1.1;
            var inner = Frame(s, v, -1, OverviewFrame, p, classic, inSt, fit);
            p.Layer("inner").AddRange(Wobble(Hatcher.Hatch(inner, inSt, Axes(inner, v.Seed), new Random(v.Seed * 7 + 2)), wob, ms, rng));
            // The internal border network (behind the skin), thin, with the walls.
            p.Layer("innerborders").AddRange(Hidden(s, skin, 0, false));
            // The skin on top, lighter, so the walls read through it.
            var outSt = Clone(baseSt); outSt.ToneScale = 0.6;
            var outer = Frame(s, v, 0, OverviewFrame, p, e, outSt, fit);
            p.Layer("hatch").AddRange(Wobble(Hatcher.Hatch(outer, outSt, Axes(outer, v.Seed), new Random(v.Seed * 7 + 1)), wob, ms, rng));
            if (e.Outline) p.Layer("outline").AddRange(Outlines(s, outer, 0));
            return p;
        }
        var sc = Frame(s, v, 0, OverviewFrame, p, e, baseSt);
        if (ms.Kind == "stipple")
            p.Layer("hatch").AddRange(Stipple(sc, baseSt, ms, rng));
        else if (ms.Kind == "cross")
        {
            // Each pass on its own spacing grid (passes cross freely). The first pass is opened up, since the
            // crossing passes add the darkness back.
            var passes = new[] { new { Rot = 0.0, From = 0.0, Open = 1.6 }, new { Rot = 72.0, From = 0.38, Open = 1.5 }, new { Rot = -38.0, From = 0.68, Open = 1.4 } };
            int i = 0;
            foreach (var ps in passes)
            {
                var st = Clone(baseSt); st.RotateDeg = ps.Rot; st.ToneFrom = ps.From;
                st.SepMin *= ps.Open; st.SepMax *= ps.Open; st.HyperMin *= ps.Open; st.HyperMax *= ps.Open;
                p.Layer("hatch").AddRange(Wobble(Hatcher.Hatch(sc, st, Axes(sc, v.Seed), new Random(v.Seed * 7 + 11 * ++i)), wob, ms, rng));
            }
        }
        else
            p.Layer("hatch").AddRange(Wobble(Hatcher.Hatch(sc, baseSt, Axes(sc, v.Seed), new Random(v.Seed * 7 + 1)), wob, ms, rng));
        if (e.Outline) p.Layer("outline").AddRange(Wobble(Outlines(s, sc, 0), wob * 0.5, ms, rng, false));
        return p;
    }

    // ---------- stipple ----------
    // Dart throwing with a tone-dependent radius (variable-radius Poisson disc): every dot keeps at least
    // r(tone) from the others, so dots pack tight in the darks and spread in the lights; a low-frequency
    // noise makes the density waver like hand stippling.
    static List<List<V2>> Stipple(Scene sc, HatchStyle st, MarkSettings ms, Random rng)
    {
        var dots = new List<List<V2>>();
        double cell = ms.DotMin * 0.7;
        var grid = new Dictionary<long, List<V2>>(LongKey.Comparer);
        Func<long, long, long> key = (x, y) => unchecked((x * 73856093L) ^ (y * 19349663L));
        double w = sc.W / sc.Res, h = sc.H / sc.Res;
        int tries = (int)(w * h / (ms.DotMin * ms.DotMin) * 3);
        for (int t = 0; t < tries; t++)
        {
            var q = new V2(rng.NextDouble() * w, rng.NextDouble() * h);
            int si; Vec3 pos, n;
            if (!sc.Query(q, out si, out pos, out n)) continue;
            double tone = sc.Lit != null ? 1 - sc.Lit(si, q, pos, n) : 1 - Math.Max(0, Vec3.Dot(n, sc.Light));
            tone = Math.Max(0, (tone - 0.1) / 0.9);
            if (tone < 0.03) continue;
            double r = Math.Min(ms.DotMax, ms.DotMin / Math.Pow(tone, 0.75));
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

    // Smooth 2D value noise in [-1, 1] (hashed lattice, smoothstep interpolation).
    static double Noise(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        Func<int, int, double> h = (a, b) =>
        {
            unchecked
            {
                uint u = (uint)(a * 374761393 + b * 668265263 + seed * 1442695041);
                u = (u ^ (u >> 13)) * 1274126177; u ^= u >> 16;
                return (u & 0xffffff) / (double)0xffffff * 2 - 1;
            }
        };
        double a0 = h(x0, y0) + (h(x0 + 1, y0) - h(x0, y0)) * fx;
        double a1 = h(x0, y0 + 1) + (h(x0 + 1, y0 + 1) - h(x0, y0 + 1)) * fx;
        return a0 + (a1 - a0) * fy;
    }

    static HatchStyle Clone(HatchStyle s) { return (HatchStyle)s.GetType().GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(s, null); }
    static EditionSettings Clone(EditionSettings s) { return (EditionSettings)s.GetType().GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(s, null); }
}

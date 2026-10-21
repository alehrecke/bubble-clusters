using System;
using System.Collections.Generic;
using System.Linq;

// Environment reflections (Alex, 2026-10-21: "reflections on the beads ... and on the main body as well"). A soap
// film or a droplet is a mirror: what it shows depends on the reflected view ray r = d - 2 (d.n) n. A tiny
// environment: one window (a rounded rectangle of directions around the light, with a vertical mullion and a
// transom) and a dark floor (rays pointing downward). On a sphere the window lands near the highlight, scaled with
// the radius (about 0.15 R across), so big bubbles and small beads show the same window at their own size.
//   window   clear paper (the stipple removed) inside, crisp outline, crisp mullion + transom on big surfaces
//   floor    darker stipple where r points down (the lower part of each bubble reflects the dark room)
public class Env
{
    readonly Vec3 W, A, B, F;
    public double Wx = 0.30, Wy = 0.42, Transom = 0.18, Floor = -0.12;
    static readonly Vec3 Up = new Vec3(0, 0, 1);

    public Env(Scene sc)
    {
        F = sc.F; W = sc.Light.Normalized();
        var a = Vec3.Cross(W, Up);
        A = a.LengthSq > 1e-6 ? a.Normalized() : sc.R;
        B = Vec3.Cross(A, W).Normalized();
        if (Vec3.Dot(B, Up) < 0) B = B * -1;
    }

    public Vec3 Reflect(Vec3 n)
    {
        if (Vec3.Dot(n, F) > 0) n = n * -1;                    // the side facing the viewer
        return F - n * (2 * Vec3.Dot(F, n));
    }

    // < 1 inside the window (rounded rectangle, superellipse p = 6); 10 when facing away from it.
    public double Window(Vec3 r)
    {
        double z = Vec3.Dot(r, W);
        if (z <= 0.05) return 10;
        double x = Vec3.Dot(r, A) / z / Wx, y = Vec3.Dot(r, B) / z / Wy;
        return Math.Pow(Math.Pow(Math.Abs(x), 6) + Math.Pow(Math.Abs(y), 6), 1 / 6.0);
    }
    public double MullionX(Vec3 r) { double z = Math.Max(0.05, Vec3.Dot(r, W)); return Vec3.Dot(r, A) / z / Wx; }
    public double MullionY(Vec3 r) { double z = Math.Max(0.05, Vec3.Dot(r, W)); return Vec3.Dot(r, B) / z / Wy - Transom; }
    // 0 above the horizon, up to 1 for rays pointing well down (the dark floor).
    public double Dark(Vec3 r) { return Math.Max(0, Math.Min(1, (Floor - r.Z) / 0.35)); }
}

public static partial class Edition
{
    // Window reflections (and optionally the dark floor) on the cluster skin. Clears the stipple inside each window.
    static void BodyReflections(Plot p, Scene sc, Env env, bool floor, Random rng, Func<V2, bool> beadFront)
    {
        double g = 0.15;
        double x0 = 0, y0 = 0;
        int gw = (int)Math.Ceiling(sc.W / sc.Res / g) + 1, gh = (int)Math.Ceiling(sc.H / sc.Res / g) + 1;
        var wf = new double[gw * gh]; var mx = new double[gw * gh]; var my = new double[gw * gh]; var own = new int[gw * gh];
        for (int j = 0; j < gh; j++)
            for (int i = 0; i < gw; i++)
            {
                int k = j * gw + i; int si; Vec3 pos, n;
                var q = new V2(x0 + i * g, y0 + j * g);
                if (!sc.Covered(q) || !sc.Query(q, out si, out pos, out n)) { wf[k] = 10; mx[k] = my[k] = 0; own[k] = -1; continue; }
                var r = env.Reflect(n);
                wf[k] = env.Window(r); mx[k] = env.MullionX(r); my[k] = env.MullionY(r); own[k] = sc.Owner[si];
            }
        // Only cells inside one bubble (normals jump across the borders) and not under a bead.
        Func<int, int, bool> same = (i, j) =>
        {
            int o = own[j * gw + i];
            return o >= 0 && own[j * gw + i + 1] == o && own[(j + 1) * gw + i] == o && own[(j + 1) * gw + i + 1] == o
                && !beadFront(new V2(x0 + (i + 0.5) * g, y0 + (j + 0.5) * g));
        };
        Func<int, int, bool> inside = (i, j) => same(i, j) && Math.Max(Math.Max(wf[j * gw + i], wf[j * gw + i + 1]), Math.Max(wf[(j + 1) * gw + i], wf[(j + 1) * gw + i + 1])) < 0.93;
        var lines = new List<List<V2>>();
        lines.AddRange(Contours(wf, gw, gh, g, 1.0, same));
        foreach (var l in Contours(mx, gw, gh, g, 0.0, inside).Concat(Contours(my, gw, gh, g, 0.0, inside)))
            if (PolyLen(l) > 1.2) lines.Add(l);
        p.Layer("glint").AddRange(lines.Where(l => PolyLen(l) > 0.6).Select(l => Hatcher.Simplify(l, 0.01)));

        Func<V2, Vec3> rayAt = q =>
        {
            int si; Vec3 pos, n;
            return sc.Covered(q) && sc.Query(q, out si, out pos, out n) ? env.Reflect(n) : new Vec3(0, 0, 1);
        };
        // Clear the stipple inside the windows.
        foreach (var name in new[] { "hatch", "inner", "innerborders" })
        {
            if (!p.Layers.ContainsKey(name)) continue;
            var kept = p.Layers[name].Where(d => d.Count != 2 || env.Window(rayAt((d[0] + d[1]) * 0.5)) >= 1).ToList();
            p.Layers[name].Clear(); p.Layers[name].AddRange(kept);
        }
        if (!floor) return;
        // The dark floor: an extra, darkening stipple where the skin reflects downward.
        var ms = new MarkSettings { DotMin = 0.42, DotMax = 3.5, DotGamma = 1.4, DotLength = 0.2 };
        p.Layer("hatch").AddRange(ToneStipple(q =>
        {
            if (!sc.Covered(q) || beadFront(q)) return 0;
            var r = rayAt(q);
            return env.Window(r) < 1.05 ? 0 : 0.55 * env.Dark(r);
        }, ms, rng, 0, 0, sc.W / sc.Res, sc.H / sc.Res));
    }

    static double PolyLen(List<V2> l) { double s = 0; for (int i = 1; i < l.Count; i++) s += (l[i] - l[i - 1]).Length; return s; }

    // Variable-radius Poisson stipple of a tone function (0..1) over a rectangle.
    static List<List<V2>> ToneStipple(Func<V2, double> toneFn, MarkSettings ms, Random rng, double x0, double y0, double x1, double y1)
    {
        var dots = new List<List<V2>>();
        double cell = ms.DotMin * 0.7;
        var grid = new Dictionary<long, List<V2>>(LongKey.Comparer);
        Func<long, long, long> key = (x, y) => unchecked((x * 73856093L) ^ (y * 19349663L));
        int tries = (int)((x1 - x0) * (y1 - y0) / (ms.DotMin * ms.DotMin) * 2.5);
        for (int t = 0; t < tries; t++)
        {
            var q = new V2(x0 + rng.NextDouble() * (x1 - x0), y0 + rng.NextDouble() * (y1 - y0));
            double tone = toneFn(q);
            if (tone < 0.03) continue;
            double r = Math.Min(ms.DotMax, ms.DotMin / Math.Pow(tone, ms.DotGamma));
            if (r >= ms.DotMax) continue;
            long cx = (long)Math.Floor(q.X / cell), cy = (long)Math.Floor(q.Y / cell);
            int rq = (int)Math.Ceiling(r / cell);
            bool ok = true;
            for (long gy = cy - rq; gy <= cy + rq && ok; gy++)
                for (long gx = cx - rq; gx <= cx + rq && ok; gx++)
                {
                    List<V2> l; if (!grid.TryGetValue(key(gx, gy), out l)) continue;
                    foreach (var o in l) if ((o - q).Length < r) { ok = false; break; }
                }
            if (!ok) continue;
            List<V2> cl; long kk = key(cx, cy);
            if (!grid.TryGetValue(kk, out cl)) grid[kk] = cl = new List<V2>(2);
            cl.Add(q);
            double a = rng.NextDouble() * Math.PI, len = ms.DotLength * (0.7 + 0.6 * rng.NextDouble());
            var o2 = new V2(Math.Cos(a), Math.Sin(a)) * (len / 2);
            dots.Add(new List<V2> { q - o2, q + o2 });
        }
        return dots;
    }

    // The window (and the floor's horizon on bigger beads) on one bead, as crisp curves in sheet mm.
    static List<List<V2>> BeadReflectionMarks(Scene sc, Env env, V2 c, double R)
    {
        int n = 28; double gs = 2 * R / n;
        var wf = new double[(n + 1) * (n + 1)]; var hz = new double[(n + 1) * (n + 1)];
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                double a = (i * gs - R) / R, b = -(j * gs - R) / R, w2 = 1 - a * a - b * b;
                int k = j * (n + 1) + i;
                if (w2 <= 0.02) { wf[k] = 10; hz[k] = 1; continue; }
                var nn = sc.R * a + sc.U * b + sc.F * -Math.Sqrt(w2);
                var r = env.Reflect(nn);
                wf[k] = env.Window(r); hz[k] = r.Z - env.Floor;
            }
        var res = new List<List<V2>>();
        var off = c - new V2(R, R);
        Func<int, int, bool> all = (i, j) => true;
        foreach (var l in Contours(wf, n + 1, n + 1, gs, 1.0, all)) res.Add(l.Select(q => q + off).ToList());
        if (R > 0.6)
            foreach (var l in Contours(hz, n + 1, n + 1, gs, 0.0, all))
                if (PolyLen(l) > 0.5 * R) res.Add(l.Select(q => q + off).ToList());
        return res;
    }
}

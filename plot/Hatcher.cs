using System;
using System.Collections.Generic;
using System.Linq;

// Surface-following hatching (evenly spaced streamlines, Jobard & Lefer 1997, with tone-dependent spacing).
// Strokes follow a tangent field on each film (rings around a per-bubble light axis, so they wrap
// the film like engraving on a globe), stay on one film (they stop at Plateau borders), and are packed
// tighter where the film turns away from the light.
public class HatchStyle
{
    public bool Short = true;          // short dashes in rows (true) or long flowing lines (false)
    public double SepMin = 0.45;       // mm between strokes in the darkest tone (a bit over the pen width)
    public double SepMax = 3.0;        // mm in the lightest tone that still gets strokes
    public double Highlight = 0.12;    // tone below this: no strokes (paper shows)
    public double Gamma = 1.3;         // tone curve
    public double DashMin = 0.9, DashMax = 2.2, DashGap = 0.55;   // short style (mm)
    public double MaxLength = 80;      // long style (mm)
    public double Step = 0.15;         // tracing step (mm)
    public double Ambient = 0.1;
    // Stroke direction field (the lighting stays global either way):
    //   light   rings around each bubble's light axis (along lines of equal brightness)
    //   facet   rings around each film's own axis (concentric on every facet)
    //   border  inset rings parallel to each film's Plateau borders (level sets of distance to the border)
    public string Field = "light";
    // Soap-film swirl: 0 = none, 1 = strong. Rotates the stroke direction within the film around a few
    // soft vortices per film plus a gentle noise, so lines eddy like the thinning flows on a real film.
    public double Swirl = 0;
    // Hidden lines: null = none, "solid" or "dash": the Plateau borders behind the surface, own pen layer.
    public string Hidden = null;
}

public static class Hatcher
{
    public static List<List<V2>> Hatch(Scene sc, HatchStyle st, Func<int, Vec3> axisOf, Random rng)
    {
        var strokes = new List<List<V2>>();
        double cell = st.SepMin * 0.5;
        var grid = new Dictionary<long, List<V2>>(LongKey.Comparer);
        Func<V2, long> key = p => unchecked(((long)Math.Floor(p.X / cell) * 73856093L) ^ ((long)Math.Floor(p.Y / cell) * 19349663L));

        // Tone (0 light .. 1 dark) and stroke direction at a sheet point; false if off the surface.
        Func<V2, double[]> sample = q =>
        {
            int s; Vec3 pos, n;
            if (!sc.Query(q, out s, out pos, out n)) return null;
            double lam = Math.Max(0, Vec3.Dot(n, sc.Light));
            double tone = 1 - (st.Ambient + (1 - st.Ambient) * lam);
            // Faces turned edge-on to the viewer read darker (gives the solids their rounded edges).
            double facing = Math.Abs(Vec3.Dot(n, sc.F));
            tone = Math.Min(1, tone + 0.25 * (1 - facing) * (1 - facing));
            // Stroke direction: rings around the bubble's light axis (t = n x A, A ~ the light direction), i.e.
            // along lines of equal brightness, as engravers hatch a sphere. Any such field has two singular
            // points (n = +A, -A): +A is the highlight, which gets no strokes, and -A faces away from the
            // viewer. (A vertical axis put a visible pole under every bubble; a sideways guide field fanned
            // the lines into points on the outlines.)
            Vec3 t; double ok;
            if (st.Field == "border")
            {
                Vec3 g = sc.FieldAt(s, q);
                ok = g.Length;                                   // < 1 where the rings meet (medial axis)
                t = Vec3.Cross(n, g);
            }
            else if (st.Field == "facet")
            {
                // Circles around the film's centroid, about its mean normal (works on flat films too, where
                // the normal barely changes and n x axis would vanish).
                int f = sc.FaceIdx[s];
                Vec3 A = sc.FilmAxis[f], r = pos - sc.FilmCentre[f];
                r = r - A * Vec3.Dot(r, A);
                t = Vec3.Cross(A, r); t = t - n * Vec3.Dot(t, n);
                ok = Math.Min(1, r.Length * sc.S / 0.6);         // no strokes within ~0.6 mm of the centre
            }
            else
            {
                Vec3 A = axisOf(sc.Owner[s]);
                t = Vec3.Cross(n, A);
                ok = Math.Min(1, t.Length * 4);                  // ~0 at the rings' centre
            }
            if (t.LengthSq < 1e-6) t = Vec3.Cross(n, sc.F);
            t = t.Normalized();
            if (sc.SwirlOn) t = sc.Swirl(s, pos, n, t);
            double dx = Vec3.Dot(t, sc.R), dy = -Vec3.Dot(t, sc.U);
            double dl = Math.Sqrt(dx * dx + dy * dy);
            if (dl < 1e-6) { dx = 1; dy = 0; dl = 1; }
            return new[] { tone, dx / dl, dy / dl, s, (double)sc.FaceIdx[s], ok };
        };
        Func<double, double> sepOf = tone =>
        {
            double d = Math.Max(0, Math.Min(1, (tone - st.Highlight) / (1 - st.Highlight)));
            d = Math.Pow(d, st.Gamma);
            return st.SepMax + (st.SepMin - st.SepMax) * d;
        };
        Func<V2, double, bool> free = (p, dist) =>
        {
            long cx = (long)Math.Floor(p.X / cell), cy = (long)Math.Floor(p.Y / cell);
            int r = (int)Math.Ceiling(dist / cell);
            double d2 = dist * dist;
            for (long gy = cy - r; gy <= cy + r; gy++)
                for (long gx = cx - r; gx <= cx + r; gx++)
                {
                    List<V2> l;
                    if (!grid.TryGetValue(unchecked((gx * 73856093L) ^ (gy * 19349663L)), out l)) continue;
                    foreach (var o in l) { double ex = o.X - p.X, ey = o.Y - p.Y; if (ex * ex + ey * ey < d2) return false; }
                }
            return true;
        };
        Action<List<V2>> commit = line =>
        {
            foreach (var p in line)
            {
                long k = key(p); List<V2> l;
                if (!grid.TryGetValue(k, out l)) { l = new List<V2>(4); grid[k] = l; }
                l.Add(p);
            }
            strokes.Add(line);
        };

        // Trace from a seed in one direction (sign), staying on the seed's film.
        Func<V2, double, double, double, List<V2>> trace = (seed, sign, maxLen, faceId) =>
        {
            var pts = new List<V2>();
            V2 p = seed; double len = 0;
            V2 prevDir = new V2(0, 0);
            while (len < maxLen)
            {
                var a = sample(p);
                if (a == null || a[4] != faceId || a[5] < 0.5) break;
                var d = new V2(a[1], a[2]) * sign;
                if (prevDir.Length > 0 && d.X * prevDir.X + d.Y * prevDir.Y < 0) d = d * -1;   // keep heading
                if (prevDir.Length > 0 && d.X * prevDir.X + d.Y * prevDir.Y < 0.75) break;      // too sharp a turn
                var mid = p + d * (st.Step * 0.5);
                var b = sample(mid);
                if (b == null || b[4] != faceId) break;
                var d2 = new V2(b[1], b[2]); if (d2.X * d.X + d2.Y * d.Y < 0) d2 = d2 * -1;
                var next = p + d2 * st.Step;
                double sep = sepOf(a[0]);
                if (sep > st.SepMax * 1.01 || !free(next, 0.5 * sep)) break;   // highlight, or too close to another stroke
                pts.Add(next); p = next; prevDir = d2; len += st.Step;
            }
            return pts;
        };

        // Seeds: a jittered grid over the covered area, shuffled; then (Jobard-Lefer) more seeds beside
        // each accepted stroke, and for dashes along the stroke after a gap.
        var seeds = new Queue<V2>();
        var init = new List<V2>();
        double gs = st.SepMin * 1.5;
        for (double y = 0; y < sc.H / sc.Res; y += gs)
            for (double x = 0; x < sc.W / sc.Res; x += gs)
            {
                var q = new V2(x + rng.NextDouble() * gs, y + rng.NextDouble() * gs);
                if (sc.Covered(q)) init.Add(q);
            }
        foreach (var q in init.OrderBy(_ => rng.Next())) seeds.Enqueue(q);
        int guard = 0;
        while (seeds.Count > 0 && guard++ < 4000000)
        {
            var q = seeds.Dequeue();
            var a = sample(q);
            if (a == null || a[5] < 0.5) continue;
            double sep = sepOf(a[0]);
            if (sep > st.SepMax * 1.01) continue;
            if (!free(q, sep)) continue;
            double faceId = a[4];
            double maxLen = st.Short ? st.DashMin + rng.NextDouble() * (st.DashMax - st.DashMin) : st.MaxLength;
            var fw = trace(q, 1, maxLen / 2, faceId);
            var bw = trace(q, -1, maxLen / 2, faceId);
            var line = new List<V2>(bw.Count + fw.Count + 1);
            for (int i = bw.Count - 1; i >= 0; i--) line.Add(bw[i]);
            line.Add(q);
            line.AddRange(fw);
            double L = 0; for (int i = 1; i < line.Count; i++) L += (line[i] - line[i - 1]).Length;
            if (L < (st.Short ? 0.4 : 1.5)) continue;
            commit(line);
            // New seeds beside the stroke (one separation away), and for dashes, beyond its ends.
            for (int i = 0; i < line.Count; i += Math.Max(1, (int)(sep / st.Step / 2)))
            {
                int j = Math.Min(line.Count - 1, i + 1), k = Math.Max(0, i - 1);
                var dir = (line[j] - line[k]).Normalized();
                var nrm = new V2(-dir.Y, dir.X);
                seeds.Enqueue(line[i] + nrm * sep); seeds.Enqueue(line[i] - nrm * sep);
            }
            if (st.Short && line.Count >= 2)
            {
                var e1 = (line[line.Count - 1] - line[line.Count - 2]).Normalized();
                var e0 = (line[0] - line[1]).Normalized();
                double gap = st.DashGap * (0.7 + 0.6 * rng.NextDouble());
                seeds.Enqueue(line[line.Count - 1] + e1 * (gap + st.DashMin * 0.5));
                seeds.Enqueue(line[0] + e0 * (gap + st.DashMin * 0.5));
            }
        }
        return strokes.Select(l => Simplify(l, 0.015)).ToList();
    }

    // 3D polylines → visible 2D runs (hidden parts removed by the scene's depth buffer).
    public static List<List<V2>> VisibleRuns(Scene sc, IEnumerable<List<Vec3>> lines, double epsWorld, double minLen = 0.3, bool hidden = false)
    {
        var res = new List<List<V2>>();
        foreach (var l in lines)
        {
            var run = new List<V2>();
            for (int i = 0; i + 1 < l.Count; i++)
            {
                Vec3 a = l[i], b = l[i + 1];
                double segMm = (sc.Project(b) - sc.Project(a)).Length;
                int n = Math.Max(1, (int)Math.Ceiling(segMm / 0.2));
                for (int k = (i == 0 ? 0 : 1); k <= n; k++)
                {
                    Vec3 p = a + (b - a) * ((double)k / n);
                    if (sc.Visible(p, epsWorld) != hidden) run.Add(sc.Project(p));
                    else { Flush(res, run, minLen); run = new List<V2>(); }
                }
            }
            Flush(res, run, minLen);
        }
        return res;
    }

    static void Flush(List<List<V2>> res, List<V2> run, double minLen)
    {
        if (run.Count < 2) return;
        double L = 0; for (int i = 1; i < run.Count; i++) L += (run[i] - run[i - 1]).Length;
        if (L >= minLen) res.Add(Simplify(run, 0.02));
    }

    // Drop points that lie within tol (mm) of the line through their neighbours (smaller SVGs, same drawing).
    public static List<V2> Simplify(List<V2> p, double tol)
    {
        if (p.Count < 3) return p;
        var keep = new List<V2> { p[0] };
        int anchor = 0;
        for (int i = 2; i < p.Count; i++)
        {
            V2 a = p[anchor], b = p[i];
            V2 ab = b - a; double l = ab.Length;
            bool ok = true;
            for (int j = anchor + 1; j < i && ok; j++)
            {
                V2 ap = p[j] - a;
                double dist = l > 1e-12 ? Math.Abs(ab.X * ap.Y - ab.Y * ap.X) / l : ap.Length;
                if (dist > tol) ok = false;
            }
            if (!ok) { keep.Add(p[i - 1]); anchor = i - 1; }
        }
        keep.Add(p[p.Count - 1]);
        return keep;
    }
}

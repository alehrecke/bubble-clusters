using System;
using System.Collections.Generic;
using System.Linq;

// Bubble rafts on water (Alex, 2026-10-21). The round-1 "raft" shape packed a flat sheet in free space, which read
// as a blob from most views. Here the raft floats: the bubbles sit on a plane (PlaneHost z = 0, as the foam bed),
// each a dome with its footprint on the water, a single layer packed edge to edge. Seen from 25-45 degrees above.
// Drawn in a mark style (Mist by default) plus:
//   waterline  the footprint outlines where the domes meet the water, crisp
//   water      ripple lines on the plane (parallel to the view horizontal), densest around the raft and thinning
//              outward in nested families, ragged at the far edge, hidden behind the raft, a little hand wobble
public static partial class Edition
{
    public static FoamSolver GenerateRaft(EditionSettings e, Action<string> log)
    {
        var rng = new Random(e.Seed);
        int n = e.Count > 0 ? e.Count : rng.Next(15, 46);
        var r = new List<double>();
        for (int i = 0; i < n; i++) r.Add(e.RMin + (e.RMax - e.RMin) * Math.Pow(rng.NextDouble(), e.SizeSkew));
        // Pack in the plane (disc), gathered to the middle, then lift each centre a little: domes on the water.
        double rd = Math.Sqrt(r.Sum(x => x * x) * 1.1);
        var c = new List<Vec3>();
        for (int i = 0; i < n; i++)
        {
            double a = rng.NextDouble() * 2 * Math.PI, d = rd * Math.Sqrt(rng.NextDouble());
            c.Add(new Vec3(d * Math.Cos(a), d * Math.Sin(a), 0));
        }
        for (int it = 0; it < 1500; it++)
        {
            for (int i = 0; i < n; i++) c[i] = new Vec3(c[i].X * 0.985, c[i].Y * 0.985, 0);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    Vec3 d = c[j] - c[i];
                    double l = d.Length, want = ClusterPacker.PairDistance(r[i], r[j]);
                    if (l >= want) continue;
                    if (l < 1e-9) { d = new Vec3(1e-3, 0, 0); l = 1e-3; }
                    Vec3 corr = d / l * (0.5 * (l - want));
                    double wi = r[j] / (r[i] + r[j]);
                    c[i] = c[i] + corr * wi; c[j] = c[j] - corr * (1 - wi);
                }
        }
        var mid = c.Aggregate(Vec3.Zero, (a, b) => a + b) / n;
        for (int i = 0; i < n; i++) c[i] = new Vec3(c[i].X - mid.X, c[i].Y - mid.Y, 0.25 * r[i]);
        var plane = new PlaneHost(Vec3.Zero, new Vec3(0, 0, 1));
        double area = r.Sum(x => 4 * Math.PI * x * x);
        double h = Math.Max(r.Min() / 5, Math.Sqrt(area / (0.433 * e.TriangleBudget)));
        string info;
        var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info, plane), new SolverSettings { Rearrange = true }) { Host = plane };
        s.SetTargetRadii(r);
        log(string.Format(CI, "raft (seed {0}): {1} bubbles, r {2:F2}..{3:F2}, cell {4:F3}, {5} triangles", e.Seed, n, r.Min(), r.Max(), h, s.Mesh.T.Count));
        Relax(s, log);
        Settle(s, log);
        return s;
    }

    public static View RaftView(int seed)
    {
        var rng = new Random(seed * 29 + 1);
        return MakeView(seed, rng.NextDouble() * 360, 25 + 20 * rng.NextDouble());
    }

    public static Plot RaftSheet(FoamSolver s, View v, HatchStyle baseSt, EditionSettings e, MarkSettings ms)
    {
        var p = MarkOverview(s, v, baseSt, e, ms);
        var sc = Frame(s, v, 0, OverviewFrame, p, e, null);
        var rng = new Random(v.Seed * 7 + 41);

        // Waterline: the footprint outlines, where visible.
        var borders = BorderClean.Clean(s.Mesh, s.TargetEdge, new BorderCleanSettings { Spacing = s.TargetEdge * 0.5, IncludeHost = true });
        var foot = new List<List<Vec3>>();
        foreach (var b in borders.Where(b => b.OnHost))
        {
            var pts = new List<Vec3>(b.Points);
            if (b.Closed && pts.Count > 0) pts.Add(pts[0]);
            foot.Add(pts);
        }
        p.Layer("outline").AddRange(Hatcher.VisibleRuns(sc, foot, 4 / (sc.S * sc.Res)).Select(Smooth2));

        // Water: lines on the plane parallel to the view horizontal.
        var rp = new Vec3(sc.R.X, sc.R.Y, 0).Normalized();
        var dp = new Vec3(sc.F.X, sc.F.Y, 0).Normalized();
        double sinEl = Math.Max(0.2, Math.Abs(sc.F.Z));
        double rr = s.Mesh.V.Max(q => Math.Sqrt(q.X * q.X + q.Y * q.Y));
        // Round 2: lighter (0.85 mm base spacing, a softer tone) and sized to fade out inside the tile instead of
        // being cut off by its frame.
        double dw = 0.85 / (sc.S * sinEl), step = 0.25 / sc.S;
        double fitW = 0.44 * p.Width / sc.S, fitH = 0.38 * p.Height / (sc.S * sinEl);
        double rw = Math.Min(1.9 * rr, Math.Min(fitW, fitH));
        double x0 = 0.04 * p.Width, x1 = 0.96 * p.Width, y0 = 0.04 * p.Height, y1 = 0.86 * p.Height;
        var wob = new MarkSettings { WobbleWavelength = 6, Breaks = 0 };
        var water = new List<List<V2>>();
        int kmax = (int)(rw / dw);
        for (int k = -kmax; k <= kmax; k++)
        {
            double o = k * dw, halfLen = Math.Sqrt(Math.Max(0, rw * rw - o * o));
            halfLen *= 0.85 + 0.15 * rng.NextDouble();                         // ragged far edge
            int m = ((k % 8) + 8) % 8;
            var cur = new List<V2>();
            for (double t = -halfLen; t <= halfLen; t += step)
            {
                var w3 = dp * o + rp * t;
                double dist = Math.Sqrt(w3.X * w3.X + w3.Y * w3.Y) / rr;      // 1 at the raft edge
                double fade = Math.Sqrt(w3.X * w3.X + w3.Y * w3.Y) / rw;      // 1 at the edge of the water
                double tone = 0.85 - 0.45 * (dist - 1) - 0.5 * Math.Max(0, fade - 0.6) + 0.12 * Noise(w3.X * 3, w3.Y * 3, v.Seed);
                double thr = m == 0 ? 0.15 : m == 4 ? 0.45 : m % 2 == 0 ? 0.7 : 0.9;
                var q = sc.Project(w3);
                bool on = tone >= thr && !sc.Covered(q) && q.X > x0 && q.X < x1 && q.Y > y0 && q.Y < y1;
                if (on) cur.Add(q);
                else { if (cur.Count >= 3) water.Add(cur); cur = new List<V2>(); }
            }
            if (cur.Count >= 3) water.Add(cur);
        }
        p.Layer("water").AddRange(Wobble(water.Select(l => Hatcher.Simplify(l, 0.01)).ToList(), 0.08, wob, rng, false));
        return p;
    }
}

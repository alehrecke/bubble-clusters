using System;
using System.Collections.Generic;
using System.Linq;

// Tubes (Alex, 2026-10-25: "just the thickened borders of the foam ... delicate shaded wires that have nice smooth
// junctions"). The Plateau borders as thin round wire, engraved: each wire's outline plus a few lines running along it
// on its shadow side (more toward the shadow edge), so it reads as a lit cylinder. Built on a 2D field over the sheet:
// f = smooth-min over the wire segments and node spheres of (distance - radius), so where wires meet the outlines blend
// into rounded fillets instead of crossing. Wires behind are cut where a nearer wire passes in front (with a small
// halo), so the network reads in depth.
//   tube-fine   thin wire (0.8 mm across), the whole network
//   tube        1.3 mm
//   tube-depth  1.5 mm in front thinning to 0.6 mm at the back
//   tube-front  1.3 mm, only the borders on the visible side of the cluster
public static partial class Edition
{
    static bool IsTube(string kind) { return kind.StartsWith("tube"); }

    struct TubeSeg { public V2 A, B; public double ZA, ZB, RA, RB; }

    static Plot TubeOverview(FoamSolver s, View v, EditionSettings e, string kind)
    {
        var p = NewPlot(e);
        var sc = Frame(s, v, 0, OverviewFrame, p, e, null);
        var borders = BorderClean.Clean(s.Mesh, s.TargetEdge, new BorderCleanSettings { Spacing = s.TargetEdge * 0.5, IncludeHost = false });
        double rFront = kind == "tube-fine" ? 0.4 : kind == "tube-depth" ? 0.75 : 0.65, rBack = kind == "tube-depth" ? 0.3 : rFront;
        var segs = new List<TubeSeg>();
        var nodes = new List<KeyValuePair<V2, double>>();   // (sheet point, depth)

        if (kind == "tube-front")
        {
            var lines = new List<List<Vec3>>();
            foreach (var b in borders)
            {
                if (!b.Bubbles.Contains(0)) continue;
                var pts = new List<Vec3>(b.Points);
                if (b.Closed && pts.Count > 0) pts.Add(pts[0]);
                lines.Add(pts);
            }
            foreach (var run in Hatcher.VisibleRuns(sc, lines, 4 / (sc.S * sc.Res)).Select(Smooth2))
                for (int i = 0; i + 1 < run.Count; i++)
                    segs.Add(new TubeSeg { A = run[i], B = run[i + 1], RA = rFront, RB = rFront });
        }
        else
        {
            double zmin = double.MaxValue, zmax = double.MinValue;
            foreach (var b in borders) foreach (var q in b.Points) { double z = sc.Depth(q); zmin = Math.Min(zmin, z); zmax = Math.Max(zmax, z); }
            Func<double, double> rad = z => rFront + (rBack - rFront) * (z - zmin) / Math.Max(1e-9, zmax - zmin);
            foreach (var b in borders)
            {
                var pts = new List<Vec3>(b.Points);
                if (b.Closed && pts.Count > 0) pts.Add(pts[0]);
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    double za = sc.Depth(pts[i]), zb = sc.Depth(pts[i + 1]);
                    segs.Add(new TubeSeg { A = sc.Project(pts[i]), B = sc.Project(pts[i + 1]), ZA = za * sc.S, ZB = zb * sc.S, RA = rad(za), RB = rad(zb) });
                }
                if (!b.Closed && pts.Count > 1)
                    foreach (var q in new[] { pts[0], pts[pts.Count - 1] }) nodes.Add(new KeyValuePair<V2, double>(sc.Project(q), sc.Depth(q)));
            }
            foreach (var n in nodes)
            {
                double r = rad(n.Value) * 1.12;   // a slightly fuller node: the fillet the smooth-min starts from
                segs.Add(new TubeSeg { A = n.Key, B = n.Key, ZA = n.Value * sc.S, ZB = n.Value * sc.S, RA = r, RB = r });
            }
        }
        if (segs.Count == 0) return p;

        // Field grid over the drawing.
        double g = 0.06, rMax = Math.Max(rFront, rBack) * 1.12, k = 0.22 * rFront, reach = rMax + 7 * k, halo = 0.22;
        double x0 = segs.Min(t => Math.Min(t.A.X, t.B.X)) - 2 * reach, y0 = segs.Min(t => Math.Min(t.A.Y, t.B.Y)) - 2 * reach;
        double x1 = segs.Max(t => Math.Max(t.A.X, t.B.X)) + 2 * reach, y1 = segs.Max(t => Math.Max(t.A.Y, t.B.Y)) + 2 * reach;
        int w = (int)Math.Ceiling((x1 - x0) / g) + 1, h = (int)Math.Ceiling((y1 - y0) / g) + 1;
        var zf = new float[w * h];
        for (int i = 0; i < zf.Length; i++) zf[i] = float.MaxValue;
        var S = new double[w * h];
        bool useDepth = kind != "tube-front";
        Action<TubeSeg, Action<int, double, double, double>> visit = (t, f) =>
        {
            double rr = Math.Max(t.RA, t.RB) + 7 * k;
            int i0 = Math.Max(0, (int)((Math.Min(t.A.X, t.B.X) - rr - x0) / g)), i1 = Math.Min(w - 1, (int)((Math.Max(t.A.X, t.B.X) + rr - x0) / g) + 1);
            int j0 = Math.Max(0, (int)((Math.Min(t.A.Y, t.B.Y) - rr - y0) / g)), j1 = Math.Min(h - 1, (int)((Math.Max(t.A.Y, t.B.Y) + rr - y0) / g) + 1);
            var ab = t.B - t.A; double ab2 = ab.X * ab.X + ab.Y * ab.Y;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var q = new V2(x0 + i * g, y0 + j * g);
                    double u = ab2 > 1e-18 ? Math.Max(0, Math.Min(1, ((q.X - t.A.X) * ab.X + (q.Y - t.A.Y) * ab.Y) / ab2)) : 0;
                    var c = t.A + ab * u;
                    double dist = (q - c).Length, r = t.RA + (t.RB - t.RA) * u, z = t.ZA + (t.ZB - t.ZA) * u;
                    f(j * w + i, dist - r, z, r);
                }
        };
        if (useDepth)
            foreach (var t in segs) visit(t, (q, d, z, r) => { if (d < halo && z < zf[q]) zf[q] = (float)z; });
        double tol = 2.5 * rMax;
        foreach (var t in segs)
            visit(t, (q, d, z, r) =>
            {
                if (useDepth && zf[q] < float.MaxValue && z > zf[q] + tol) return;   // behind a nearer wire
                S[q] += Math.Exp(-d / k);
            });
        var fld = new double[w * h];
        for (int q = 0; q < fld.Length; q++) fld[q] = S[q] > 1e-300 ? Math.Min(3 * rMax, -k * Math.Log(S[q])) : 3 * rMax;

        // Engraving: the outline (f = 0) everywhere, then lines along the wire on the shadow side.
        var l2 = new V2(Vec3.Dot(sc.Light, sc.R), -Vec3.Dot(sc.Light, sc.U)).Normalized();
        Func<int, int, double> shade = (i, j) =>
        {
            int a = Math.Max(1, Math.Min(w - 2, i)), b = Math.Max(1, Math.Min(h - 2, j));
            double gx = fld[b * w + a + 1] - fld[b * w + a - 1], gy = fld[(b + 1) * w + a] - fld[(b - 1) * w + a];
            double gl = Math.Sqrt(gx * gx + gy * gy);
            return gl < 1e-12 ? 0 : -(gx * l2.X + gy * l2.Y) / gl;   // 1 = facing away from the light
        };
        var res = new List<List<V2>>();
        Func<int, int, bool> all = (i, j) => true;
        res.AddRange(Contours(fld, w, h, g, 0.0, all));
        var levels = new[] { new { C = 0.17, Th = -0.15 }, new { C = 0.34, Th = 0.2 }, new { C = 0.51, Th = 0.48 }, new { C = 0.68, Th = 0.74 } };
        foreach (var lv in levels)
            res.AddRange(Contours(fld, w, h, g, -lv.C * rFront, (i, j) => shade(i, j) > lv.Th));
        var off = new V2(x0, y0);
        p.Layer("hatch").AddRange(res.Where(l => l.Count >= 3).Select(l => Hatcher.Simplify(l.Select(q => q + off).ToList(), 0.01)));
        return p;
    }
}

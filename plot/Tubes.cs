using System;
using System.Collections.Generic;
using System.Linq;

// Tubes (Alex, 2026-10-25: "just the thickened borders of the foam ... delicate shaded wires that have nice smooth
// junctions"). The Plateau borders as thin round wire, engraved: each wire's outline plus lines running along it on its
// shadow side, so it reads as a lit cylinder. Built on a 2D field over the sheet: f = smooth-min over the wire segments
// and node spheres of (distance - radius), so where wires meet the outlines blend into rounded fillets instead of
// crossing. Wires behind are cut where a nearer wire passes in front (with a small halo).
//   tube-fine   thin wire (0.8 mm across), the whole network        tube        1.3 mm
//   tube-depth  1.5 mm in front thinning to 0.6 mm at the back       tube-front  1.3 mm, visible side only
// Round 2 ("get closer with a wider angle ... the dramatic shading style"):
//   persp       a perspective camera close to the cluster (wires near the lens thicker, the far side small and thin)
//   close       closer still, wider angle, cropped by the sheet (the cluster runs off the edges)
//   drama       dramatic light: more shadow lines, a heavier shadow edge, and wires in shadow of nearer wires (a
//               screen-space cast shadow along the light direction) fully shaded
//   kinds: tube-persp, tube-persp-drama, tube-close-drama, tube-drama (orthographic)
public static partial class Edition
{
    static bool IsTube(string kind) { return kind.StartsWith("tube"); }

    struct TubeSeg { public V2 A, B; public double ZA, ZB, RA, RB; }

    static Plot TubeOverview(FoamSolver s, View v, EditionSettings e, string kind)
    {
        var p = NewPlot(e);
        var sc = Frame(s, v, 0, OverviewFrame, p, e, null);
        var borders = BorderClean.Clean(s.Mesh, s.TargetEdge, new BorderCleanSettings { Spacing = s.TargetEdge * 0.5, IncludeHost = false });
        bool persp = kind.Contains("persp") || kind.Contains("close"), close = kind.Contains("close"), drama = kind.Contains("drama");
        double rFront = kind == "tube-fine" ? 0.4 : kind == "tube-depth" ? 0.75 : persp ? 0.6 : 0.65, rBack = kind == "tube-depth" ? 0.3 : rFront;
        var segs = new List<TubeSeg>();
        var nodes = new List<Vec3>();
        double fx0 = 0.04 * p.Width, fy0 = 0.04 * p.Height, fx1 = 0.96 * p.Width, fy1 = 0.88 * p.Height;   // drawing area

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
            // Projection: orthographic (the scene's) or a pinhole camera at dist from the cluster centre along -F.
            var all = borders.SelectMany(b => b.Points).ToList();
            var cen = all.Aggregate(Vec3.Zero, (a, b) => a + b) / Math.Max(1, all.Count);
            double R = all.Max(q => (q - cen).Length);
            double dist = close ? 1.75 * R : 2.6 * R;
            var eye = cen - sc.F * dist;
            Func<Vec3, double> camZ = q => Vec3.Dot(q - eye, sc.F);
            Func<Vec3, V2> raw = q => persp ? new V2(Vec3.Dot(q - eye, sc.R) / camZ(q), -Vec3.Dot(q - eye, sc.U) / camZ(q)) : sc.Project(q);
            Func<Vec3, V2> proj = raw; double scale = 1, ox = 0, oy = 0;
            if (persp)
            {
                // Fit into the drawing area; close = 1.45x larger, so it runs off the sheet.
                var pr = all.Select(raw).ToList();
                double minx = pr.Min(q => q.X), maxx = pr.Max(q => q.X), miny = pr.Min(q => q.Y), maxy = pr.Max(q => q.Y);
                scale = Math.Min((fx1 - fx0) / (maxx - minx), (fy1 - fy0) / (maxy - miny)) * (close ? 1.45 : 0.95);
                ox = (fx0 + fx1) / 2 - scale * (minx + maxx) / 2; oy = (fy0 + fy1) / 2 - scale * (miny + maxy) / 2;
                proj = q => { var r2 = raw(q); return new V2(ox + scale * r2.X, oy + scale * r2.Y); };
            }
            double zc = persp ? dist : 0;
            Func<Vec3, double> depthMm = q => persp ? camZ(q) * scale / dist : sc.Depth(q) * sc.S;
            double zmin = all.Min(q => sc.Depth(q)), zmax = all.Max(q => sc.Depth(q));
            Func<Vec3, double> rad = q =>
                persp ? rFront * dist / camZ(q)                                             // thicker near the lens
                      : rFront + (rBack - rFront) * (sc.Depth(q) - zmin) / Math.Max(1e-9, zmax - zmin);
            foreach (var b in borders)
            {
                var pts = new List<Vec3>(b.Points);
                if (b.Closed && pts.Count > 0) pts.Add(pts[0]);
                for (int i = 0; i + 1 < pts.Count; i++)
                    segs.Add(new TubeSeg { A = proj(pts[i]), B = proj(pts[i + 1]), ZA = depthMm(pts[i]), ZB = depthMm(pts[i + 1]), RA = rad(pts[i]), RB = rad(pts[i + 1]) });
                if (!b.Closed && pts.Count > 1) { nodes.Add(pts[0]); nodes.Add(pts[pts.Count - 1]); }
            }
            foreach (var n in nodes)
            {
                double r = rad(n) * 1.12;
                var q = proj(n);
                segs.Add(new TubeSeg { A = q, B = q, ZA = depthMm(n), ZB = depthMm(n), RA = r, RB = r });
            }
            // Drop what lies well outside the sheet (close-ups run off it).
            segs = segs.Where(t => Math.Max(t.A.X, t.B.X) > fx0 - 5 && Math.Min(t.A.X, t.B.X) < fx1 + 5 && Math.Max(t.A.Y, t.B.Y) > fy0 - 5 && Math.Min(t.A.Y, t.B.Y) < fy1 + 5).ToList();
        }
        if (segs.Count == 0) return p;

        // Field grid over the drawing.
        double g = 0.06, rMax = segs.Max(t => Math.Max(t.RA, t.RB)), rTyp = rFront, k = 0.22 * rTyp, halo = 0.22;
        double reach = rMax + 7 * k;
        double x0 = Math.Max(fx0 - reach, segs.Min(t => Math.Min(t.A.X, t.B.X)) - 2 * reach), y0 = Math.Max(fy0 - reach, segs.Min(t => Math.Min(t.A.Y, t.B.Y)) - 2 * reach);
        double x1 = Math.Min(fx1 + reach, segs.Max(t => Math.Max(t.A.X, t.B.X)) + 2 * reach), y1 = Math.Min(fy1 + reach, segs.Max(t => Math.Max(t.A.Y, t.B.Y)) + 2 * reach);
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
        double tol = 2.5 * rTyp;
        foreach (var t in segs)
            visit(t, (q, d, z, r) =>
            {
                if (useDepth && zf[q] < float.MaxValue && z > zf[q] + tol) return;   // behind a nearer wire
                S[q] += Math.Exp(-d / k);
            });
        var fld = new double[w * h];
        for (int q = 0; q < fld.Length; q++) fld[q] = S[q] > 1e-300 ? Math.Min(3 * rMax, -k * Math.Log(S[q])) : 3 * rMax;

        var l2 = new V2(Vec3.Dot(sc.Light, sc.R), -Vec3.Dot(sc.Light, sc.U)).Normalized();
        // Drama: a cast shadow in screen space. A wire point is in shadow when a nearer wire lies a little toward the light.
        bool[] shadow = null;
        if (drama && useDepth)
        {
            shadow = new bool[w * h];
            var offs = new[] { 0.7, 1.4, 2.1, 2.8 }.Select(o => new { DI = (int)Math.Round(l2.X * o / g), DJ = (int)Math.Round(l2.Y * o / g) }).ToArray();
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    float z0 = zf[j * w + i];
                    if (z0 == float.MaxValue) continue;
                    foreach (var o in offs)
                    {
                        int ii = i + o.DI, jj = j + o.DJ;
                        if (ii < 0 || jj < 0 || ii >= w || jj >= h) continue;
                        if (zf[jj * w + ii] < z0 - 1.5 * rTyp) { shadow[j * w + i] = true; break; }
                    }
                }
        }
        Func<int, int, double> shade = (i, j) =>
        {
            int a = Math.Max(1, Math.Min(w - 2, i)), b = Math.Max(1, Math.Min(h - 2, j));
            double gx = fld[b * w + a + 1] - fld[b * w + a - 1], gy = fld[(b + 1) * w + a] - fld[(b - 1) * w + a];
            double gl = Math.Sqrt(gx * gx + gy * gy);
            double sh = gl < 1e-12 ? 0 : -(gx * l2.X + gy * l2.Y) / gl;   // 1 = facing away from the light
            if (shadow != null && shadow[b * w + a]) sh = Math.Max(sh, 0.8);
            return sh;
        };
        var res = new List<List<V2>>();
        Func<int, int, bool> every = (i, j) => true;
        res.AddRange(Contours(fld, w, h, g, 0.0, every));
        var levels = drama
            ? new[] { new { C = 0.12, Th = -0.45 }, new { C = 0.24, Th = -0.15 }, new { C = 0.36, Th = 0.1 }, new { C = 0.48, Th = 0.32 }, new { C = 0.6, Th = 0.52 }, new { C = 0.72, Th = 0.7 } }
            : new[] { new { C = 0.17, Th = -0.15 }, new { C = 0.34, Th = 0.2 }, new { C = 0.51, Th = 0.48 }, new { C = 0.68, Th = 0.74 } };
        foreach (var lv in levels)
            res.AddRange(Contours(fld, w, h, g, -lv.C * rTyp, (i, j) => shade(i, j) > lv.Th));
        if (drama) res.AddRange(Contours(fld, w, h, g, 0.1, (i, j) => shade(i, j) > 0.35));   // heavier shadow edge
        var off = new V2(x0, y0);
        var lines2 = res.Where(l => l.Count >= 3).Select(l => Hatcher.Simplify(l.Select(q => q + off).ToList(), 0.01)).ToList();
        p.Layer("hatch").AddRange(persp ? ClipRect(lines2, fx0, fy0, fx1, fy1) : lines2);
        return p;
    }
}

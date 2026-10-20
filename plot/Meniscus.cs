using System;
using System.Collections.Generic;
using System.Linq;

// Meniscus (foam bed style, Alex 2026-10-19: "not too dry and boring but still seeing all the layers").
// The lattice as the structure, tone only where the physics puts it:
//   wet borders  in a real foam the liquid gathers in the Plateau borders, most of all at the nodes and where
//                the films meet the plane. Plane borders are ribbons that swell toward their nodes, every plane
//                node a small dense blob, and the cell corners on the plane carry short contour lines parallel to
//                the walls (the meniscus), long near the nodes, fading toward the middle of each border and cell.
//   depth        crisp on the plane; higher up the pile single wobbly lines, then broken lines, then dotted lines,
//                with a dusting of dots around the upper nodes (sharp in front, soft behind).
//   two pens     layer "fine" (everything above the plane: plot first with a fine grey / sepia pen) and layer
//                "bold" (the plane: borders, nodes, meniscus; plot second with a 0.3-0.5 black).
//   focus        one area (where the plane nodes are densest: the small cells) gets the full treatment; toward
//                the sheet edges the meniscus shortens, the blobs shrink and the stipple disappears.
public static partial class Edition
{
    static Plot Meniscus(FoamSolver s, BedSettings b, Scene skin, List<CleanBorder> borders, double zTop, View v,
                         double[] fr, List<Vec3> fit, Plot p, EditionSettings e, HatchStyle baseSt, int seed)
    {
        var rng = new Random(seed * 7 + 5);
        double x0 = b.Margin, y0 = b.Margin, x1 = p.Width - b.Margin, y1 = p.Height - b.Margin;
        var fine = p.Layer("fine");
        var bold = p.Layer("bold");

        // Borders on the sheet: plane ones (with their ends: the plane nodes) and the rest with their height.
        var plane = new List<List<V2>>(); var planeOpen = new List<bool>();
        var upper = new List<KeyValuePair<double, List<V2>>>();
        var planeNodes = new List<V2>(); var upperNodes = new List<KeyValuePair<double, V2>>();
        foreach (var bd in borders)
        {
            var pts = new List<Vec3>(bd.Points);
            if (bd.Closed && pts.Count > 0) pts.Add(pts[0]);
            if (pts.Count < 2) continue;
            var l2 = Smooth2(pts.Select(skin.Project).ToList());
            if (l2.Count < 2) continue;
            if (bd.OnHost)
            {
                plane.Add(l2); planeOpen.Add(!bd.Closed);
                if (!bd.Closed) { AddNode(planeNodes, l2[0]); AddNode(planeNodes, l2[l2.Count - 1]); }
            }
            else
            {
                double h = Math.Max(0.02, Math.Min(1, pts.Average(q => q.Z) / zTop));
                upper.Add(new KeyValuePair<double, List<V2>>(h, l2));
                if (!bd.Closed)
                {
                    upperNodes.Add(new KeyValuePair<double, V2>(pts[0].Z / zTop, l2[0]));
                    upperNodes.Add(new KeyValuePair<double, V2>(pts[pts.Count - 1].Z / zTop, l2[l2.Count - 1]));
                }
            }
        }

        // Focus: the densest patch of plane nodes (small cells), Gaussian falloff over about a third of the sheet.
        V2 fc = new V2(p.Width / 2, p.Height / 2); int bestN = -1;
        foreach (var q in planeNodes)
        {
            if (q.X < x0 + 30 || q.X > x1 - 30 || q.Y < y0 + 30 || q.Y > y1 - 30) continue;
            int cnt = planeNodes.Count(o => (o - q).Length < 45);
            if (cnt > bestN) { bestN = cnt; fc = q; }
        }
        double sig = 0.36 * Math.Min(p.Width, p.Height);
        Func<V2, double> F = q => { var d = q - fc; return Math.Exp(-(d.X * d.X + d.Y * d.Y) / (2 * sig * sig)); };

        // ---- fine pen: the pile above the plane ----
        // Deep walls: stipple only in the focus.
        var classic = Clone(e); classic.Light = "classic";
        var inner = Frame(s, v, -1, fr, p, classic, Clone(baseSt), fit);
        var inMs = new MarkSettings { DotMin = 0.42, DotMax = 4.5, DotGamma = 1.8 };
        fine.AddRange(Stipple(inner, inMs, rng, 0.75, q => Math.Pow(F(q), 1.3)));
        // Borders by height: wobbly lines, then broken, then dotted.
        foreach (var kv in upper)
        {
            double h = kv.Key;
            var one = new List<List<V2>> { kv.Value };
            if (h > 0.72) { fine.AddRange(DotLines(one, 0.42 + 0.7 * (h - 0.72), 0.04, inMs, rng)); continue; }
            var ms = new MarkSettings { WobbleWavelength = 4.5, Breaks = h > 0.45 ? 0.03 + 0.12 * (h - 0.45) : 0 };
            fine.AddRange(Wobble(one, 0.05 + 0.12 * h, ms, rng, ms.Breaks > 0));
        }
        // A dusting of dots around the upper nodes, in the focus.
        foreach (var kv in upperNodes)
        {
            double f = F(kv.Value);
            int n = (int)Math.Round(5 * f * (0.4 + kv.Key));
            for (int i = 0; i < n; i++)
            {
                double a = rng.NextDouble() * 2 * Math.PI, r = 0.6 + 2.2 * rng.NextDouble();
                var q = kv.Value + new V2(Math.Cos(a), Math.Sin(a)) * r;
                double ang = rng.NextDouble() * Math.PI;
                var o = new V2(Math.Cos(ang), Math.Sin(ang)) * 0.1;
                fine.Add(new List<V2> { q - o, q + o });
            }
        }

        // ---- bold pen: the plane ----
        // Distance (mm) to the nearest plane border, 0.25 mm grid: trims the meniscus lines where two walls'
        // zones meet (the lines of neighbouring walls mitre at the bisector instead of crossing).
        double g = 0.25;
        int gw = (int)Math.Ceiling(p.Width / g) + 1, gh = (int)Math.Ceiling(p.Height / g) + 1;
        var dist = new double[gw * gh];
        for (int i = 0; i < dist.Length; i++) dist[i] = 1e9;
        foreach (var l in plane)
            foreach (var q in Densify(l, 0.1))
            {
                int i = (int)Math.Round(q.X / g), j = (int)Math.Round(q.Y / g);
                if (i >= 0 && j >= 0 && i < gw && j < gh) dist[j * gw + i] = 0;
            }
        Chamfer(dist, gw, gh, g);
        Func<V2, double> D = q =>
        {
            int i = (int)Math.Round(q.X / g), j = (int)Math.Round(q.Y / g);
            return i >= 0 && j >= 0 && i < gw && j < gh ? dist[j * gw + i] : 1e9;
        };

        var wob = new MarkSettings { WobbleWavelength = 5, Breaks = 0 };
        for (int k = 0; k < plane.Count; k++)
        {
            var pts = Densify(plane[k], 0.2).ToList();
            if (pts.Count < 2) continue;
            var along = new double[pts.Count];
            for (int i = 1; i < pts.Count; i++) along[i] = along[i - 1] + (pts[i] - pts[i - 1]).Length;
            double L = along[pts.Count - 1];
            var nrm = new V2[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                int j = Math.Min(pts.Count - 1, i + 1), m = Math.Max(0, i - 1);
                var d = (pts[j] - pts[m]).Normalized(); nrm[i] = new V2(-d.Y, d.X);
            }
            // Node proximity along the border (0 mid-border .. 1 at a node); closed borders (no nodes) stay thin.
            Func<int, double> near = i => planeOpen[k] ? Math.Min(1, Math.Exp(-along[i] / 5.0) + Math.Exp(-(L - along[i]) / 5.0)) : 0.15;
            // The wet border: passes 0.14 mm apart, each drawn where the ribbon is wide enough for it.
            for (int pass = -4; pass <= 4; pass++)
            {
                double off = pass * 0.14;
                var runs = Runs(pts.Count, i => Math.Abs(off) <= 0.1 + 0.55 * near(i) * (0.55 + 0.45 * F(pts[i])), i => pts[i] + nrm[i] * off);
                foreach (var r in runs) { if (pass % 2 != 0) r.Reverse(); bold.Add(Hatcher.Simplify(r, 0.01)); }
            }
        }
        // The meniscus: contour lines of the distance to the plane walls, smoothed (so they round off in the
        // cell corners instead of mitring), kept where they lie within reach of the wall: far near the nodes,
        // short mid-border, scaled by the focus. (Round 1 used offset copies of each border: choppy, bristly nodes.)
        var nd = new double[gw * gh];
        for (int i = 0; i < nd.Length; i++) nd[i] = 1e9;
        foreach (var q in planeNodes)
        {
            int i = (int)Math.Round(q.X / g), j = (int)Math.Round(q.Y / g);
            if (i >= 0 && j >= 0 && i < gw && j < gh) nd[j * gw + i] = 0;
        }
        Chamfer(nd, gw, gh, g);
        var sm = BoxBlur(dist, gw, gh, 3, 3);
        var levels = Enumerable.Range(0, 9).Select(c => 0.7 + c * 0.4).ToList();
        Func<int, int, bool> within = (i, j) =>
        {
            double d = sm[j * gw + i], dn = nd[j * gw + i];
            var q = new V2(i * g, j * g);
            double reach = (0.45 + 3.4 * Math.Exp(-dn / 5.5)) * (0.35 + 0.65 * F(q));
            return d < reach;
        };
        foreach (var lv in levels)
            bold.AddRange(Wobble(Contours(sm, gw, gh, g, lv, within).Where(r => r.Count >= 3).Select(r => Hatcher.Simplify(r, 0.01)).ToList(), 0.025, wob, rng, false));

        // Plane nodes: small dense blobs (concentric rings), larger in the focus.
        foreach (var q in planeNodes)
        {
            double rb = 0.35 + 0.45 * F(q);
            for (double r = 0.1; r <= rb; r += 0.13)
            {
                var ring = new List<V2>();
                int n = Math.Max(8, (int)(2 * Math.PI * r / 0.15));
                for (int i = 0; i <= n; i++) { double a = 2 * Math.PI * i / n; ring.Add(q + new V2(Math.Cos(a), Math.Sin(a)) * r); }
                bold.Add(ring);
            }
        }

        foreach (var name in p.LayerOrder.ToList())
        {
            var clipped = ClipRect(p.Layers[name], x0, y0, x1, y1);
            p.Layers[name].Clear(); p.Layers[name].AddRange(clipped);
        }
        AddLabel(p, "MENISCUS  (FOAM BED)  PEN 1 FINE: LAYER FINE  PEN 2 BOLD: LAYER BOLD  " + s.Mesh.BubbleCount + " BUBBLES  SEED " + seed);
        return p;
    }

    // Separable box blur, `passes` times radius r (cells): about a Gaussian.
    static double[] BoxBlur(double[] src, int w, int h, int r, int passes)
    {
        var a = (double[])src.Clone(); var t = new double[a.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int j = 0; j < h; j++)
            {
                double acc = 0; int cnt = 0;
                for (int i = -r; i < w + r; i++)
                {
                    int add = i + r, rem = i - r - 1;
                    if (add < w) { acc += Math.Min(a[j * w + add], 50); cnt++; }
                    if (rem >= 0 && rem < w) { acc -= Math.Min(a[j * w + rem], 50); cnt--; }
                    if (i >= 0 && i < w) t[j * w + i] = acc / cnt;
                }
            }
            for (int i = 0; i < w; i++)
            {
                double acc = 0; int cnt = 0;
                for (int j = -r; j < h + r; j++)
                {
                    int add = j + r, rem = j - r - 1;
                    if (add < h) { acc += t[add * w + i]; cnt++; }
                    if (rem >= 0 && rem < h) { acc -= t[rem * w + i]; cnt--; }
                    if (j >= 0 && j < h) a[j * w + i] = acc / cnt;
                }
            }
        }
        return a;
    }

    // Marching squares: the level set f = lv as polylines (sheet mm, grid step g), only in cells where
    // keep(i, j) holds at the cell's corner; segments chained through shared grid edges.
    static List<List<V2>> Contours(double[] f, int w, int h, double g, double lv, Func<int, int, bool> keep)
    {
        // Edge key: horizontal edge (i,j)-(i+1,j) = 2*(j*w+i), vertical (i,j)-(i,j+1) = 2*(j*w+i)+1.
        var pt = new Dictionary<long, V2>();
        var adj = new Dictionary<long, List<long>>();
        Func<int, int, int, int, long> edge = (i0, j0, i1, j1) =>
        {
            long k = j0 == j1 ? 2L * (j0 * (long)w + Math.Min(i0, i1)) : 2L * (Math.Min(j0, j1) * (long)w + i0) + 1;
            if (!pt.ContainsKey(k))
            {
                double a = f[j0 * w + i0], b2 = f[j1 * w + i1], t = (lv - a) / (b2 - a);
                pt[k] = new V2((i0 + (i1 - i0) * t) * g, (j0 + (j1 - j0) * t) * g);
            }
            return k;
        };
        Action<long, long> link = (a, b2) =>
        {
            List<long> l;
            if (!adj.TryGetValue(a, out l)) adj[a] = l = new List<long>(2); l.Add(b2);
            if (!adj.TryGetValue(b2, out l)) adj[b2] = l = new List<long>(2); l.Add(a);
        };
        for (int j = 0; j + 1 < h; j++)
            for (int i = 0; i + 1 < w; i++)
            {
                double v0 = f[j * w + i], v1 = f[j * w + i + 1], v2 = f[(j + 1) * w + i + 1], v3 = f[(j + 1) * w + i];
                int c = (v0 > lv ? 1 : 0) | (v1 > lv ? 2 : 0) | (v2 > lv ? 4 : 0) | (v3 > lv ? 8 : 0);
                if (c == 0 || c == 15 || !keep(i, j)) continue;
                var es = new List<long>(4);
                if (((c & 1) != 0) != ((c & 2) != 0)) es.Add(edge(i, j, i + 1, j));          // top
                if (((c & 2) != 0) != ((c & 4) != 0)) es.Add(edge(i + 1, j, i + 1, j + 1));  // right
                if (((c & 4) != 0) != ((c & 8) != 0)) es.Add(edge(i, j + 1, i + 1, j + 1));  // bottom
                if (((c & 8) != 0) != ((c & 1) != 0)) es.Add(edge(i, j, i, j + 1));          // left
                if (es.Count == 2) link(es[0], es[1]);
                else if (es.Count == 4) { link(es[0], es[1]); link(es[2], es[3]); }
            }
        var used = new HashSet<long>();
        var res = new List<List<V2>>();
        // Start from ends (degree 1) first, then whatever is left (closed loops).
        foreach (var start in adj.Keys.OrderBy(k => adj[k].Count).ToList())
        {
            if (used.Contains(start)) continue;
            var line = new List<V2> { pt[start] }; used.Add(start);
            long cur = start;
            while (true)
            {
                long nx = -1;
                foreach (var o in adj[cur]) if (!used.Contains(o)) { nx = o; break; }
                if (nx < 0) break;
                used.Add(nx); line.Add(pt[nx]); cur = nx;
            }
            if (line.Count >= 2) res.Add(line);
        }
        return res;
    }

    static void AddNode(List<V2> nodes, V2 q)
    {
        foreach (var o in nodes) if ((o - q).Length < 0.4) return;
        nodes.Add(q);
    }

    // Consecutive indices where keep(i) holds, as polylines of at(i).
    static List<List<V2>> Runs(int n, Func<int, bool> keep, Func<int, V2> at)
    {
        var res = new List<List<V2>>(); var cur = new List<V2>();
        for (int i = 0; i < n; i++)
        {
            if (keep(i)) cur.Add(at(i));
            else { if (cur.Count >= 2) res.Add(cur); cur = new List<V2>(); }
        }
        if (cur.Count >= 2) res.Add(cur);
        return res;
    }
}

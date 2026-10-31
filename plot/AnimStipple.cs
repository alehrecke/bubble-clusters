using System;
using System.Collections.Generic;
using System.Linq;

// Stippled animation frames (Mist look) that don't "boil" (Alex, 2026-10-29). Dots come from fixed point sets with a
// rank each (progressive Poisson: points accepted while the minimum spacing shrinks, so any prefix of the ranks is
// itself evenly spread); a dot is drawn when its rank is below the density the shading asks for there. So as the
// shading changes, dots appear and disappear in place instead of reshuffling every frame.
//   foam phase   screen-space sets (skin, inner walls, ground) fixed on the sheet
//   stone phase  sets attached to each stone's surface (they tumble with it); the skin set cross-fades into them over
//                the first half second after the freeze; the foam's transparency (inner walls, back borders) fades out
//                over the last frames of the relaxed foam, so the foam turns solid before it falls
//   shadows      the stones' shadow on the (undrawn) ground, from the key light, stippled from a ground set
public static partial class Edition
{
    class RankedPoint { public V2 Q; public double U; }
    class SurfDot { public Vec3 P, N; public double U; }

    // Progressive Poisson over a w x h sheet (mm): spacings from rMax down to rMin; rank = acceptance order / count.
    static List<RankedPoint> RankedField(double w, double h, double rMin, double rMax, int seed)
    {
        var rng = new Random(seed);
        var pts = new List<V2>();
        double cell = rMin / Math.Sqrt(2);
        int gw = (int)Math.Ceiling(w / cell) + 1, gh = (int)Math.Ceiling(h / cell) + 1;
        var grid = new int[gw * gh];
        for (int i = 0; i < grid.Length; i++) grid[i] = -1;
        int stages = 14;
        for (int st = 0; st < stages; st++)
        {
            double r = rMax * Math.Pow(rMin / rMax, st / (double)(stages - 1));
            int tries = (int)(w * h / (r * r) * 3);
            int reach = (int)Math.Ceiling(r / cell);
            for (int t = 0; t < tries; t++)
            {
                var q = new V2(rng.NextDouble() * w, rng.NextDouble() * h);
                int cx = (int)(q.X / cell), cy = (int)(q.Y / cell);
                if (grid[cy * gw + cx] >= 0) continue;
                bool ok = true;
                for (int y = Math.Max(0, cy - reach); y <= Math.Min(gh - 1, cy + reach) && ok; y++)
                    for (int x = Math.Max(0, cx - reach); x <= Math.Min(gw - 1, cx + reach) && ok; x++)
                    {
                        int k = grid[y * gw + x];
                        if (k >= 0 && (pts[k] - q).Length < r) ok = false;
                    }
                if (!ok) continue;
                grid[cy * gw + cx] = pts.Count; pts.Add(q);
            }
        }
        return pts.Select((q, i) => new RankedPoint { Q = q, U = (i + 0.5) / pts.Count }).ToList();
    }

    // The same on a body's surface (local coordinates), area-weighted candidates, spacing in world units.
    static List<SurfDot> RankedSurface(List<Vec3> lv, List<int[]> tris, double rMin, int seed)
    {
        var rng = new Random(seed);
        var area = tris.Select(t => 0.5 * Vec3.Cross(lv[t[1]] - lv[t[0]], lv[t[2]] - lv[t[0]]).Length).ToArray();
        double total = area.Sum();
        var cum = new double[area.Length]; double acc = 0;
        for (int i = 0; i < area.Length; i++) { acc += area[i]; cum[i] = acc; }
        double rMax = rMin * 12;
        var pts = new List<SurfDot>();
        double cell = rMin;
        var grid = new Dictionary<long, List<int>>(LongKey.Comparer);
        Func<Vec3, long> key = q => unchecked(((long)Math.Floor(q.X / cell) * 73856093L) ^ ((long)Math.Floor(q.Y / cell) * 19349663L) ^ ((long)Math.Floor(q.Z / cell) * 83492791L));
        int stages = 12;
        for (int st = 0; st < stages; st++)
        {
            double r = rMax * Math.Pow(rMin / rMax, st / (double)(stages - 1));
            int tries = (int)(total / (r * r) * 3);
            int reach = (int)Math.Ceiling(r / cell);
            for (int t = 0; t < tries; t++)
            {
                int ti = Array.BinarySearch(cum, rng.NextDouble() * total); if (ti < 0) ti = ~ti; ti = Math.Min(ti, tris.Count - 1);
                double a = rng.NextDouble(), b = rng.NextDouble(); if (a + b > 1) { a = 1 - a; b = 1 - b; }
                var tr = tris[ti];
                var p = lv[tr[0]] + (lv[tr[1]] - lv[tr[0]]) * a + (lv[tr[2]] - lv[tr[0]]) * b;
                bool ok = true;
                for (int dx = -reach; dx <= reach && ok; dx++)
                    for (int dy = -reach; dy <= reach && ok; dy++)
                        for (int dz = -reach; dz <= reach && ok; dz++)
                        {
                            List<int> l; if (!grid.TryGetValue(key(p + new Vec3(dx * cell, dy * cell, dz * cell)), out l)) continue;
                            foreach (int k in l) if ((pts[k].P - p).Length < r) { ok = false; break; }
                        }
                if (!ok) continue;
                var n = Vec3.Cross(lv[tr[1]] - lv[tr[0]], lv[tr[2]] - lv[tr[0]]).Normalized();
                long kk = key(p); List<int> cl; if (!grid.TryGetValue(kk, out cl)) grid[kk] = cl = new List<int>();
                cl.Add(pts.Count); pts.Add(new SurfDot { P = p, N = n });
            }
        }
        for (int i = 0; i < pts.Count; i++) pts[i].U = (i + 0.5) / pts.Count;
        return pts;
    }

    static List<V2> Tick(V2 q, Random rng, double len = 0.2)
    {
        double a = rng.NextDouble() * Math.PI; var d = new V2(Math.Cos(a), Math.Sin(a)) * (len / 2);
        return new List<V2> { q - d, q + d };
    }

    // Tone 0..1 -> fraction of a ranked set to draw (Mist: spacing DotMin / tone^1.6, so density ~ tone^3.2).
    static double Frac(double tone) { tone = Math.Max(0, (tone - 0.08) / 0.92); return Math.Pow(Math.Min(1, tone), 3.2); }

    class StippleCtx
    {
        public List<RankedPoint> Skin, Inner, Ground;
        public List<List<SurfDot>> BodyDots;             // the current frame's stone set
        public List<List<List<SurfDot>>> BodyDotSets;    // per frozen set
        public double SMax = 1, StoneScale = 1;           // stone dots made at SMax; thinned by (S / SMax)^2
        public double FloorZ;
        public Vec3 KeyLight, ShadowLight;   // shadows from a steeper light (the key light's shadow ran off the sheet)
    }

    // One stippled frame. foamFade: 1 = fully transparent foam, 0 = solid (inner walls and back borders gone).
    // bodyMix: 0 = screen-space skin dots, 1 = stone-attached dots.
    static void StippleFrame(Plot p, Scene sc, FoamMesh fm, StippleCtx cx, double cell, bool foam,
                             List<List<Vec3>> creases, List<Vec3> pos, List<Quat> rot, double foamFade, double bodyMix, int fi)
    {
        var rng = new Random(9173);   // the same per frame: the tick angles stay put too
        var hatch = p.Layer("hatch"); var inner = p.Layer("inner"); var lines = p.Layer("innerborders"); var ground = p.Layer("ground");
        Func<V2, Vec3, int, double> tone = (q, n, si) =>
        {
            int s2; Vec3 pp, nn;
            if (!sc.Query(q, out s2, out pp, out nn)) return -1;
            return sc.Lit != null ? 1 - sc.Lit(s2, q, pp, nn) : 1 - Math.Max(0, Vec3.Dot(nn, sc.Light));
        };
        // Skin (screen-space set), fading out as the stone dots fade in.
        double skinW = 1 - bodyMix;
        if (skinW > 0)
            foreach (var rp in cx.Skin)
            {
                double t = tone(rp.Q, Vec3.Zero, 0);
                if (t < 0) continue;
                if (rp.U < Frac(1.05 * t) * skinW) hatch.Add(Tick(rp.Q, rng));
            }
        // Stone-attached dots.
        if (bodyMix > 0 && cx.BodyDots != null)
        {
            double veps = 3 / (sc.S * sc.Res);
            for (int i = 0; i < cx.BodyDots.Count; i++)
                foreach (var d in cx.BodyDots[i])
                {
                    var pw = pos[i] + rot[i].Rotate(d.P * cx.StoneScale); var nw = rot[i].Rotate(d.N);
                    double facing = -Vec3.Dot(nw, sc.F);
                    if (facing <= 0.02) continue;
                    var q = sc.Project(pw);
                    int s2; Vec3 pp, nn;
                    if (!sc.Query(q, out s2, out pp, out nn)) continue;
                    if (Math.Abs(sc.Depth(pp) - sc.Depth(pw)) > veps) continue;   // hidden behind another stone
                    double t = sc.Lit != null ? 1 - sc.Lit(s2, q, pp, nn) : 1 - Math.Max(0, Vec3.Dot(nn, sc.Light));
                    double thin = Math.Min(1, (sc.S * cx.StoneScale / cx.SMax) * (sc.S * cx.StoneScale / cx.SMax));
                    if (d.U < Frac(1.05 * t) * facing * bodyMix * thin) hatch.Add(Tick(q, rng));
                }
        }
        // Inner walls (foam only): two-sided interior scene, classic light, sparser.
        if (foam && foamFade > 0)
        {
            var isc = Scene.Surface(fm, -1);
            isc.F = sc.F; isc.R = sc.R; isc.U = sc.U; isc.S = sc.S; isc.Ox = sc.Ox; isc.Oy = sc.Oy; isc.Light = sc.Light;
            isc.Rasterize(sc.Res, p.Width, p.Height);
            foreach (var rp in cx.Inner)
            {
                int s2; Vec3 pp, nn;
                if (!isc.Query(rp.Q, out s2, out pp, out nn)) continue;
                if (Vec3.Dot(nn, isc.F) > 0) nn = nn * -1;
                double t = 1 - Math.Max(0, Vec3.Dot(nn, isc.Light));
                t = Math.Min(1, t + 0.25 * Math.Pow(1 - Math.Abs(Vec3.Dot(nn, isc.F)), 2));
                if (rp.U < Frac(0.8 * t) * 0.64 * foamFade) inner.Add(Tick(rp.Q, rng));
            }
        }
        // Borders / creases as dotted lines.
        var ms = new MarkSettings();
        if (foam)
        {
            var borders = BorderClean.Clean(fm, cell, new BorderCleanSettings { Spacing = cell * 0.5, IncludeHost = false });
            var outer = new List<List<Vec3>>(); var all = new List<List<Vec3>>();
            foreach (var bd in borders)
            {
                var pts = new List<Vec3>(bd.Points);
                if (bd.Closed && pts.Count > 0) pts.Add(pts[0]);
                all.Add(pts); if (bd.Bubbles.Contains(0)) outer.Add(pts);
            }
            hatch.AddRange(DotLines(Hatcher.VisibleRuns(sc, outer, 4 / (sc.S * sc.Res)), 0.6, 0.05, ms, rng));
            if (foamFade > 0.02)
                lines.AddRange(DotLines(Hatcher.VisibleRuns(sc, all, 1.2 / sc.S, 0.6, true).Select(Smooth2).ToList(), 0.9 / foamFade, 0.05, ms, rng));
        }
        if (creases != null && bodyMix > 0.02)
            hatch.AddRange(DotLines(Hatcher.VisibleRuns(sc, creases, 4 / (sc.S * sc.Res)), 0.6 / Math.Max(0.15, foam ? bodyMix : 1), 0.05, ms, rng));
        if (sc.Tris.Count > 0) hatch.AddRange(DotLines(Hatcher.VisibleRuns(sc, sc.Silhouettes(), 1.0 / sc.S, 0.6).Select(Smooth2).ToList(), 0.5, 0.05, ms, rng));

        // Shadow on the ground: the mesh projected along the light onto z = FloorZ, rasterised on a ground grid,
        // softened; ground dots wherever the shadow is and nothing is in front.
        var L = cx.ShadowLight;
        if (L.Z > 0.05)
        {
            Func<Vec3, Vec3> onGround = v => v - L * ((v.Z - cx.FloorZ) / L.Z);
            double gcell = 0.8 / sc.S;
            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            var gp = fm.V.Select(onGround).ToList();
            foreach (var v in gp) { minx = Math.Min(minx, v.X); miny = Math.Min(miny, v.Y); maxx = Math.Max(maxx, v.X); maxy = Math.Max(maxy, v.Y); }
            minx -= 6 * gcell; miny -= 6 * gcell; maxx += 6 * gcell; maxy += 6 * gcell;
            int gw = (int)Math.Ceiling((maxx - minx) / gcell) + 1, gh = (int)Math.Ceiling((maxy - miny) / gcell) + 1;
            if ((long)gw * gh < 6000000)
            {
                var mask = new double[gw * gh];
                foreach (int ti in sc.Tris)
                {
                    var t = fm.T[ti];
                    V2 a = new V2((gp[t.A].X - minx) / gcell, (gp[t.A].Y - miny) / gcell), b = new V2((gp[t.B].X - minx) / gcell, (gp[t.B].Y - miny) / gcell), c = new V2((gp[t.C].X - minx) / gcell, (gp[t.C].Y - miny) / gcell);
                    int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(gw - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
                    int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(gh - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
                    double den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    if (Math.Abs(den) < 1e-12) continue;
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            double l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / den, l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den;
                            if (l1 >= -0.01 && l2 >= -0.01 && l1 + l2 <= 1.01) mask[y * gw + x] = 1;
                        }
                }
                // Soften (two box passes of radius 2).
                for (int pass = 0; pass < 2; pass++)
                {
                    var tmp = new double[mask.Length];
                    for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++)
                        { double s0 = 0; int n = 0; for (int k = -2; k <= 2; k++) { int xx = x + k; if (xx < 0 || xx >= gw) continue; s0 += mask[y * gw + xx]; n++; } tmp[y * gw + x] = s0 / n; }
                    for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++)
                        { double s0 = 0; int n = 0; for (int k = -2; k <= 2; k++) { int yy = y + k; if (yy < 0 || yy >= gh) continue; s0 += tmp[yy * gw + x]; n++; } mask[y * gw + x] = s0 / n; }
                }
                foreach (var rp in cx.Ground)
                {
                    if (sc.Covered(rp.Q)) continue;
                    // The sheet point back onto the ground plane (orthographic camera).
                    double wx = (rp.Q.X - sc.Ox) / sc.S, wy = -(rp.Q.Y - sc.Oy) / sc.S;
                    var p0 = sc.R * wx + sc.U * wy;
                    if (Math.Abs(sc.F.Z) < 1e-6) continue;
                    double tt = (cx.FloorZ - p0.Z) / sc.F.Z;
                    var g = p0 + sc.F * tt;
                    int gx = (int)Math.Round((g.X - minx) / gcell), gy = (int)Math.Round((g.Y - miny) / gcell);
                    if (gx < 0 || gy < 0 || gx >= gw || gy >= gh) continue;
                    double sh = mask[gy * gw + gx];
                    double edge = Math.Min(Math.Min(rp.Q.X, p.Width - rp.Q.X), Math.Min(rp.Q.Y, p.Height - rp.Q.Y));
                    sh *= Math.Max(0, Math.Min(1, (edge - 6) / 10));   // fade out toward the sheet edge
                    if (sh > 0.02 && rp.U < Math.Pow(0.62 * sh, 2.2)) ground.Add(Tick(rp.Q, rng));
                }
            }
        }
    }
}

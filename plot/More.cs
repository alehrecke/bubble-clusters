using System;
using System.Collections.Generic;
using System.Linq;

// More mark-making styles (2026-10-20), several chosen for few pen lifts (the pen-lift servo wore out on
// ~230k lifts of stipple):
//   contour  the skin sliced by parallel planes (vertical axis: a topographic map of the cluster); nested line
//            families switched on by tone (all lines in the darks, every 2nd / 4th in the lights)
//   thread   stipple points joined nearest-neighbour into long wandering lines (one-line drawing): tangled in the
//            darks, open in the lights; lifts only on jumps
//   froth    tiny circles packed by tone (small and tight in the darks, large and sparse in the lights)
//   wave     parallel lines across the cluster rippling with tone (amplitude and frequency grow with darkness)
public static partial class Edition
{
    static readonly string[] MoreKinds = { "contour", "thread", "froth", "wave" };
    static bool IsMore(string kind) { return MoreKinds.Contains(kind); }

    static List<List<V2>> MoreMarks(string kind, FoamSolver s, Scene sc, Random rng)
    {
        switch (kind)
        {
            case "contour": return Contour(s, sc);
            case "thread": return Thread(sc, rng);
            case "froth": return Froth(sc, rng);
            default: return Wave(sc);
        }
    }

    // Darkness 0..1 at a sheet point (as the stipple sees it), -1 off the surface.
    static double ToneAt(Scene sc, V2 q)
    {
        int si; Vec3 pos, n;
        if (!sc.Query(q, out si, out pos, out n)) return -1;
        if (sc.TwoSided && Vec3.Dot(n, sc.F) > 0) n = n * -1;
        double tone = sc.Lit != null ? 1 - sc.Lit(si, q, pos, n) : 1 - Math.Max(0, Vec3.Dot(n, sc.Light));
        return Math.Max(0, Math.Min(1, (tone - 0.08) / 0.92));
    }

    static List<List<V2>> Contour(FoamSolver s, Scene sc)
    {
        var m = s.Mesh;
        var axis = new Vec3(0, 0, 1);
        double step = 0.62 / sc.S;                 // ~0.62 mm apart on the sheet (where the surface faces us)
        // Slice segments per level, chained through shared mesh edges.
        var segs = new Dictionary<int, List<KeyValuePair<long, long>>>();
        var pt = new Dictionary<long, Vec3>();
        Func<int, int, int, long> key = (a, b, k) => unchecked(((long)Math.Min(a, b) * 2000003L + Math.Max(a, b)) * 4099L + k);
        foreach (int ti in sc.Tris)
        {
            var t = m.T[ti];
            int[] vi = { t.A, t.B, t.C };
            double[] h = vi.Select(v => Vec3.Dot(m.V[v], axis) / step).ToArray();
            int k0 = (int)Math.Ceiling(h.Min()), k1 = (int)Math.Floor(h.Max());
            for (int k = k0; k <= k1; k++)
            {
                var ends = new List<long>(2);
                for (int e = 0; e < 3; e++)
                {
                    int a = e, b = (e + 1) % 3;
                    if ((h[a] < k) == (h[b] < k)) continue;
                    long kk = key(vi[a], vi[b], k & 4095);
                    if (!pt.ContainsKey(kk))
                    {
                        double u = (k - h[a]) / (h[b] - h[a]);
                        pt[kk] = m.V[vi[a]] + (m.V[vi[b]] - m.V[vi[a]]) * u;
                    }
                    ends.Add(kk);
                }
                if (ends.Count != 2) continue;
                List<KeyValuePair<long, long>> l;
                if (!segs.TryGetValue(k, out l)) segs[k] = l = new List<KeyValuePair<long, long>>();
                l.Add(new KeyValuePair<long, long>(ends[0], ends[1]));
            }
        }
        var res = new List<List<V2>>();
        double eps = 4 / (sc.S * sc.Res);
        foreach (var kv in segs)
        {
            int k = kv.Key, mod = ((k % 4) + 4) % 4;
            double thr = mod == 0 ? 0.0 : mod == 2 ? 0.32 : 0.6;
            var adj = new Dictionary<long, List<long>>();
            foreach (var sg in kv.Value)
            {
                List<long> l;
                if (!adj.TryGetValue(sg.Key, out l)) adj[sg.Key] = l = new List<long>(2); l.Add(sg.Value);
                if (!adj.TryGetValue(sg.Value, out l)) adj[sg.Value] = l = new List<long>(2); l.Add(sg.Key);
            }
            var used = new HashSet<long>();
            var lines = new List<List<Vec3>>();
            foreach (var st in adj.Keys.OrderBy(x => adj[x].Count))
            {
                if (used.Contains(st)) continue;
                var line = new List<Vec3> { pt[st] }; used.Add(st); long cur = st;
                while (true)
                {
                    long nx = -1;
                    foreach (var o in adj[cur]) if (!used.Contains(o)) { nx = o; break; }
                    if (nx < 0) break;
                    used.Add(nx); line.Add(pt[nx]); cur = nx;
                }
                if (line.Count >= 2) lines.Add(line);
            }
            foreach (var run in Hatcher.VisibleRuns(sc, lines, eps))
            {
                var pts = Densify(Smooth2(run), 0.25).ToList();
                res.AddRange(Runs(pts.Count, i => ToneAt(sc, pts[i]) >= thr, i => pts[i]).Select(r => Hatcher.Simplify(r, 0.01)));
            }
        }
        return res;
    }

    static List<List<V2>> Thread(Scene sc, Random rng)
    {
        // Points: the stipple's (dot centres), coarser so the thread stays legible.
        var ms = new MarkSettings { DotMin = 0.5, DotMax = 6, DotGamma = 1.1 };
        var pts = Stipple(sc, ms, rng, 1.0).Select(d => (d[0] + d[1]) * 0.5).ToList();
        // Greedy nearest-neighbour walk on a grid; a jump longer than maxJump lifts the pen.
        double cell = 2.0, maxJump = 2.6;
        var grid = new Dictionary<long, List<int>>(LongKey.Comparer);
        Func<long, long, long> gk = (x, y) => unchecked((x * 73856093L) ^ (y * 19349663L));
        for (int i = 0; i < pts.Count; i++)
        {
            long k = gk((long)Math.Floor(pts[i].X / cell), (long)Math.Floor(pts[i].Y / cell));
            List<int> l; if (!grid.TryGetValue(k, out l)) grid[k] = l = new List<int>(); l.Add(i);
        }
        var alive = new bool[pts.Count]; for (int i = 0; i < alive.Length; i++) alive[i] = true;
        int left = pts.Count;
        Action<int> kill = i =>
        {
            alive[i] = false; left--;
            grid[gk((long)Math.Floor(pts[i].X / cell), (long)Math.Floor(pts[i].Y / cell))].Remove(i);
        };
        var res = new List<List<V2>>();
        int cur = 0;
        var line = new List<V2>();
        while (left > 0)
        {
            if (!alive[cur]) { cur = Array.IndexOf(alive, true); line = new List<V2>(); }
            line.Add(pts[cur]); kill(cur);
            if (left == 0) break;
            int best = -1; double bd = double.MaxValue;
            long cx = (long)Math.Floor(pts[cur].X / cell), cy = (long)Math.Floor(pts[cur].Y / cell);
            for (int ring = 0; ring <= 2 && best < 0; ring++)
                for (long gy = cy - ring; gy <= cy + ring; gy++)
                    for (long gx = cx - ring; gx <= cx + ring; gx++)
                    {
                        List<int> l; if (!grid.TryGetValue(gk(gx, gy), out l)) continue;
                        foreach (int j in l) { double d = (pts[j] - pts[cur]).Length; if (d < bd) { bd = d; best = j; } }
                    }
            if (best < 0 || bd > maxJump)
            {
                if (line.Count >= 2) res.Add(ChaikinSmooth(line));
                line = new List<V2>();
                cur = best >= 0 ? best : Array.IndexOf(alive, true);
            }
            else cur = best;
        }
        if (line.Count >= 2) res.Add(ChaikinSmooth(line));
        return res;
    }

    static List<V2> ChaikinSmooth(List<V2> l)
    {
        var cur = l;
        for (int it = 0; it < 2; it++)
        {
            var nx = new List<V2> { cur[0] };
            for (int i = 0; i + 1 < cur.Count; i++)
            {
                nx.Add(cur[i] * 0.75 + cur[i + 1] * 0.25);
                nx.Add(cur[i] * 0.25 + cur[i + 1] * 0.75);
            }
            nx.Add(cur[cur.Count - 1]);
            cur = nx;
        }
        return cur;
    }

    static List<List<V2>> Froth(Scene sc, Random rng)
    {
        double dMin = 0.75, dMax = 5.5, gamma = 1.25;
        var placed = new List<KeyValuePair<V2, double>>();
        double cell = dMin;
        var grid = new Dictionary<long, List<int>>(LongKey.Comparer);
        Func<long, long, long> gk = (x, y) => unchecked((x * 73856093L) ^ (y * 19349663L));
        double w = sc.W / sc.Res, h = sc.H / sc.Res;
        int tries = (int)(w * h / (dMin * dMin) * 2.5);
        int reach = (int)Math.Ceiling(dMax / cell);
        for (int t = 0; t < tries; t++)
        {
            var q = new V2(rng.NextDouble() * w, rng.NextDouble() * h);
            double tone = ToneAt(sc, q);
            if (tone < 0.04) continue;
            double d = Math.Min(dMax, dMin / Math.Pow(tone, gamma));
            if (d >= dMax) continue;
            double rad = 0.4 * d;
            long cx = (long)Math.Floor(q.X / cell), cy = (long)Math.Floor(q.Y / cell);
            bool ok = true;
            for (long gy = cy - reach; gy <= cy + reach && ok; gy++)
                for (long gx = cx - reach; gx <= cx + reach && ok; gx++)
                {
                    List<int> l; if (!grid.TryGetValue(gk(gx, gy), out l)) continue;
                    foreach (int j in l)
                    {
                        var o = placed[j];
                        if ((o.Key - q).Length < rad + o.Value + 0.12) { ok = false; break; }
                    }
                }
            if (!ok) continue;
            long k = gk(cx, cy);
            List<int> cl; if (!grid.TryGetValue(k, out cl)) grid[k] = cl = new List<int>();
            cl.Add(placed.Count); placed.Add(new KeyValuePair<V2, double>(q, rad));
        }
        var res = new List<List<V2>>();
        foreach (var c in placed)
        {
            int n = Math.Max(8, (int)(2 * Math.PI * c.Value / 0.2));
            double a0 = rng.NextDouble() * 2 * Math.PI;
            var ring = new List<V2>();
            for (int i = 0; i <= n; i++) { double a = a0 + 2 * Math.PI * i / n; ring.Add(c.Key + new V2(Math.Cos(a), Math.Sin(a)) * c.Value); }
            res.Add(ring);
        }
        return res;
    }

    static List<List<V2>> Wave(Scene sc)
    {
        // Nested line families (as contour): every line in the darks, every 2nd / 4th in the lights.
        double spacing = 0.6, step = 0.08, ang = 12 * Math.PI / 180;
        var dir = new V2(Math.Cos(ang), Math.Sin(ang)); var nrm = new V2(-dir.Y, dir.X);
        double w = sc.W / sc.Res, h = sc.H / sc.Res;
        var mid = new V2(w / 2, h / 2);
        double half = Math.Sqrt(w * w + h * h) / 2;
        var res = new List<List<V2>>();
        int kmax = (int)(half / spacing);
        for (int k = -kmax; k <= kmax; k++)
        {
            double o = k * spacing;
            int mod = ((k % 4) + 4) % 4;
            double thr = mod == 0 ? 0.0 : mod == 2 ? 0.3 : 0.58;
            var cur = new List<V2>(); double phase = 0;
            for (double t = -half; t <= half; t += step)
            {
                var b = mid + nrm * o + dir * t;
                double tone = ToneAt(sc, b);
                if (tone < thr || tone < 0) { if (cur.Count >= 2) res.Add(Hatcher.Simplify(cur, 0.01)); cur = new List<V2>(); continue; }
                phase += step * 2 * Math.PI * (0.3 + 1.4 * tone);           // cycles per mm grow with darkness
                double amp = 0.9 * spacing * Math.Pow(tone, 1.3);
                var q = b + nrm * (amp * Math.Sin(phase));
                cur.Add(q);
            }
            if (cur.Count >= 2) res.Add(Hatcher.Simplify(cur, 0.01));
        }
        return res;
    }
}

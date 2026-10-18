using System;
using System.Collections.Generic;
using System.Linq;

// Generators decide where new bubbles go; the solver inserts them (FoamSolver.RequestAddBatch, one
// rebuild per batch). They run between solver ticks: in Grasshopper as components downstream of the
// solver's Foam output, or as user scripts calling the same Foam handle.

public class BorderDetailSettings
{
    public int PerBatch = 6;          // seeds per batch (one rebuild each batch)
    public double SizeMin = 0.15;     // new bubble radius as a fraction of the smallest bubble on that border
    public double SizeMax = 0.35;
    public int Where = 0;             // 0 any border, 1 outer borders only (touching the outside air: the visible ones), 2 inner only
    public double Spacing = 3;        // seeds at least this x (sum of the two seed radii) apart, and from nodes
    public double MinRadius = 0;      // floor (0: the solver's smallest, ~1 rebuild cell)
}

public static class BorderDetail
{
    public class Pick
    {
        public Vec3 At; public double Radius; public int[] Regions;
    }

    // Choose up to set.PerBatch seed points on Plateau borders. Borders are picked with probability
    // proportional to their length, then a point uniformly along the chosen border. The new radius is
    // a random fraction (SizeMin..SizeMax) of the smallest bubble meeting there.
    public static List<Pick> Choose(FoamSolver s, BorderDetailSettings set, Random rng)
    {
        var m = s.Mesh;
        var et = m.EdgeTris();
        List<int> nodes;
        var chains = FoamAnalysis.BorderChains(m, out nodes);
        var cand = new List<KeyValuePair<FoamAnalysis.Chain, int[]>>();
        var lens = new List<double>();
        foreach (var c in chains)
        {
            if (c.Verts.Count < 2) continue;
            List<int> tris;
            if (!et.TryGetValue(FoamMesh.EdgeKey(c.Verts[0], c.Verts[1]), out tris) || tris.Count != 3) continue;
            var regs = new SortedSet<int>();
            foreach (int ti in tris) { regs.Add(m.T[ti].Neg); regs.Add(m.T[ti].Pos); }
            if (regs.Contains(FoamMesh.Solid)) continue;              // where a wall stands on a host
            bool outer = regs.Contains(0);
            if ((set.Where == 1 && !outer) || (set.Where == 2 && outer)) continue;
            double len = 0;
            for (int i = 0; i + 1 < c.Verts.Count; i++) len += (m.V[c.Verts[i + 1]] - m.V[c.Verts[i]]).Length;
            if (len <= 0) continue;
            cand.Add(new KeyValuePair<FoamAnalysis.Chain, int[]>(c, regs.Where(r => r > 0).ToArray()));
            lens.Add(len);
        }
        var picks = new List<Pick>();
        if (cand.Count == 0) return picks;
        double total = lens.Sum();
        // Smallest bubble a rebuild keeps reliably: 1.5 rebuild cells (smaller ones fall between grid samples
        // and get dropped; run 2026-10-18_182417).
        double floor = Math.Max(set.MinRadius, 1.5 * s.TargetEdge * s.S.RebuildGridFactor);
        var nodePts = nodes.Select(v => m.V[v]).ToList();
        for (int attempt = 0; attempt < set.PerBatch * 20 && picks.Count < set.PerBatch; attempt++)
        {
            // Border by length.
            double u = rng.NextDouble() * total, acc = 0; int ci = 0;
            for (; ci < cand.Count - 1; ci++) { acc += lens[ci]; if (acc >= u) break; }
            var chain = cand[ci].Key;
            // Point along it.
            double target = rng.NextDouble() * lens[ci], walked = 0;
            Vec3 at = m.V[chain.Verts[0]];
            for (int i = 0; i + 1 < chain.Verts.Count; i++)
            {
                Vec3 a = m.V[chain.Verts[i]], b = m.V[chain.Verts[i + 1]];
                double l = (b - a).Length;
                if (walked + l >= target) { at = a + (b - a) * ((target - walked) / Math.Max(l, 1e-12)); break; }
                walked += l;
            }
            double local = cand[ci].Value.Length == 0 ? 1 : cand[ci].Value.Min(k => s.DesiredRadius(k));
            double want = local * (set.SizeMin + rng.NextDouble() * (set.SizeMax - set.SizeMin));
            if (floor > 1.5 * set.SizeMax * local) continue;   // this border's bubbles are too small for detail at this CellSize
            double r = Math.Max(floor, want);
            // Spacing from other picks and from nodes.
            bool ok = true;
            foreach (var p in picks) if ((p.At - at).Length < set.Spacing * (p.Radius + r)) { ok = false; break; }
            if (ok) foreach (var q in nodePts) if ((q - at).Length < set.Spacing * r) { ok = false; break; }
            if (!ok) continue;
            picks.Add(new Pick { At = at, Radius = r, Regions = cand[ci].Value });
        }
        return picks;
    }
}

public class StreamSettings
{
    public Vec3 Source;               // centre of the spawn disc (upstream)
    public Vec3 Flow = new Vec3(1, 0, 0);   // flow direction x strength (~0.3 gentle, ~1 brisk)
    public double Spread = 1;         // spawn disc radius, perpendicular to the flow
    public int Every = 120;           // solver steps between spawns
    public double RMin = 0.3, RMax = 0.7;
    public double Sink = 0;           // drift toward the host (the bed), same units as Flow
    public double Exit = 15;          // free bubbles further than this downstream of Source are removed
    public int MaxFree = 8;           // at most this many floating at once
}

// Stream bed: bubbles are spawned upstream, carried by the flow, and stick when they touch the bed (the
// host) or the stuck cluster; the rest float away and are removed. Call Tick between solver ticks.
public static class StreamBed
{
    public class State
    {
        public int NextSpawn = int.MinValue, Spawned, Removed;
        public HashSet<int> RemoveAsked = new HashSet<int>();
        public List<Vec3> LastSpawn = new List<Vec3>();
    }

    public static string Tick(FoamSolver s, StreamSettings set, Random rng, State st)
    {
        var m = s.Mesh;
        int n = m.BubbleCount;
        bool[] onHost;
        var piece = Contacts.Pieces(m, out onHost);
        // Stuck pieces: on the host; with no host, the largest piece (the cluster being built).
        var stuck = new bool[n + 1];
        if (s.Host != null) { for (int k = 1; k <= n; k++) stuck[k] = piece[k] > 0 && onHost[piece[k]]; }
        else
        {
            var size = new Dictionary<int, int>();
            for (int k = 1; k <= n; k++) if (piece[k] > 0) { int c; size.TryGetValue(piece[k], out c); size[piece[k]] = c + 1; }
            int big = size.Count == 0 ? 0 : size.OrderByDescending(kv => kv.Value).First().Key;
            for (int k = 1; k <= n; k++) stuck[k] = piece[k] > 0 && piece[k] == big && size[big] > 1;
        }
        var cen = s.Centroids();
        Vec3 dir = set.Flow.LengthSq > 0 ? set.Flow.Normalized() : new Vec3(1, 0, 0);
        // Forces on the floating bubbles; none on stuck ones.
        var force = new Vec3[n + 1];
        int floating = 0;
        for (int k = 1; k <= n; k++)
        {
            if (piece[k] == 0 || stuck[k] || s.IsDead(k)) continue;
            floating++;
            double r = Math.Max(s.DesiredRadius(k), 1e-6);
            Vec3 f = set.Flow / r;
            if (set.Sink != 0 && s.Host != null) f = f - s.Host.Normal(cen[k]) * (set.Sink / r);
            force[k] = f;
            // Left the domain downstream: remove.
            if (Vec3.Dot(cen[k] - set.Source, dir) > set.Exit && !st.RemoveAsked.Contains(k))
            {
                st.RemoveAsked.Add(k);
                s.RequestPop(k);
                st.Removed++;
            }
        }
        s.ExtForce = force;
        // Spawn.
        st.LastSpawn.Clear();
        if (s.Steps >= st.NextSpawn && floating < set.MaxFree && s.PendingInserts == 0)
        {
            Vec3 u = Vec3.Cross(dir, Math.Abs(dir.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0)).Normalized();
            Vec3 w = Vec3.Cross(dir, u);
            double a = 2 * Math.PI * rng.NextDouble(), rr = set.Spread * Math.Sqrt(rng.NextDouble());
            Vec3 p = set.Source + u * (rr * Math.Cos(a)) + w * (rr * Math.Sin(a));
            double rad = set.RMin + (set.RMax - set.RMin) * rng.NextDouble();
            // Keep clear of everything (a bubble just spawned may still be near the source).
            double gap = double.MaxValue;
            foreach (var q in m.V) gap = Math.Min(gap, (q - p).Length - rad);
            if (s.Host != null) gap = Math.Min(gap, s.Host.Height(p) - rad);
            if (gap > 2 * s.TargetEdge)
            {
                s.RequestInsertFree(p, rad);
                st.Spawned++;
                st.LastSpawn.Add(p);
                st.NextSpawn = s.Steps + set.Every;
            }
        }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "spawned {0}, removed {1}, floating {2}, stuck {3}", st.Spawned, st.Removed, floating,
            Enumerable.Range(1, n).Count(k => stuck[k] && !s.IsDead(k)));
    }
}

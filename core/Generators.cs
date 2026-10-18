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
        double floor = Math.Max(set.MinRadius, s.TargetEdge * s.S.RebuildGridFactor);
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
            double r = Math.Max(floor, local * (set.SizeMin + rng.NextDouble() * (set.SizeMax - set.SizeMin)));
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

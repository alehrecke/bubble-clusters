using System;
using System.Collections.Generic;
using System.Linq;

// Popping a bubble (Phase 3, T2), done on the mesh by relabelling, without a rebuild.
//
// A bubble pops when one of its films breaks:
//   - it has an outer film: that film breaks, its air joins the outside. Region k becomes 0, its
//     outer film (now 0|0) disappears, and its walls become outer films of the neighbours, which
//     then bulge out as they relax.
//   - it's fully inside the cluster: one of its walls breaks and it merges with that neighbour
//     (the largest shared wall). Region k becomes j, the k|j wall disappears, j keeps both volumes.
// Every other film keeps its vertices and orientation, so nothing else moves and all the other
// volumes stay exact. Only the films around the popped bubble change, and the solver relaxes them.
public static class Pop
{
    public class Result
    {
        public int Bubble, Into;   // Into = 0: popped to the outside; > 0: merged into that bubble
        public double Volume;      // the popped bubble's volume
        public string Fail;
        public override string ToString()
        {
            if (Fail != null) return "pop bubble " + Bubble + " refused: " + Fail;
            return Into == 0 ? "popped bubble " + Bubble + " (its air escaped)"
                             : "popped bubble " + Bubble + ": it was inside the cluster, so it merged into bubble " + Into;
        }
    }

    // into: -1 = decide as above, 0 = force popping to the outside, j > 0 = merge into j.
    // Changes the mesh in place only on success.
    public static Result Apply(FoamMesh m, int k, int into = -1)
    {
        var res = new Result { Bubble = k, Into = into };
        if (k < 1 || k > m.BubbleCount) { res.Fail = "no bubble " + k; return res; }
        double outer = 0;
        var wall = new Dictionary<int, double>();
        int alive = 0;
        var has = new bool[m.BubbleCount + 1];
        foreach (var t in m.T)
        {
            if (t.Neg > 0) has[t.Neg] = true;
            if (t.Pos > 0) has[t.Pos] = true;
            if (t.Neg != k && t.Pos != k) continue;
            int o = t.Neg == k ? t.Pos : t.Neg;
            double a = m.TriArea(t);
            if (o == FoamMesh.Solid) continue;   // its footprint on a host
            if (o == 0) outer += a;
            else { double w; wall.TryGetValue(o, out w); wall[o] = w + a; }
        }
        for (int b = 1; b <= m.BubbleCount; b++) if (has[b]) alive++;
        if (!has[k]) { res.Fail = "bubble " + k + " doesn't exist any more"; return res; }
        if (alive <= 1) { res.Fail = "it's the last bubble"; return res; }
        if (into < 0) into = outer > 0 || wall.Count == 0 ? 0 : wall.OrderByDescending(kv => kv.Value).First().Key;
        if (into > 0 && !wall.ContainsKey(into)) { res.Fail = "bubble " + into + " isn't a neighbour of " + k; return res; }
        res.Into = into;
        res.Volume = m.Volumes()[k];

        var copy = m.Clone();
        var keep = new List<FoamTri>(copy.T.Count);
        foreach (var t0 in copy.T)
        {
            var t = t0;
            if (t.Neg == k) t.Neg = into;
            if (t.Pos == k) t.Pos = into;
            if (t.Neg == t.Pos) continue;   // the broken film
            if ((t.Neg == FoamMesh.Solid && t.Pos == 0) || (t.Pos == FoamMesh.Solid && t.Neg == 0)) continue; // footprint, now dry
            keep.Add(t);
        }
        copy.T = keep;
        copy.Clean();
        var hist = Remesher.EdgeHistogram(copy);
        if (hist[1] > 0) { res.Fail = "the mesh would have " + hist[1] + " open edges"; return res; }
        m.V = copy.V; m.T = copy.T;
        return res;
    }

    // The bubble containing p, or 0 if p is outside every bubble.
    public static int InsideBubble(FoamMesh m, Vec3 p) { return BubbleAt(m, p, false); }

    // The bubble containing p (ray parity against each bubble's closed surface); if p is outside
    // every bubble, the one whose surface is closest (or 0 when nearest is false).
    public static int BubbleAt(FoamMesh m, Vec3 p, bool nearest = true)
    {
        var dir = new Vec3(0.8017837, 0.2672612, 0.5345225);
        var hits = new int[m.BubbleCount + 1];
        var near = new double[m.BubbleCount + 1];
        for (int k = 0; k <= m.BubbleCount; k++) near[k] = double.MaxValue;
        foreach (var t in m.T)
        {
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            bool hit = Contacts.RayHits(p, dir, a, b, c);
            double d = (FoamAnalysis.ClosestOnTriangle(p, a, b, c) - p).LengthSq;
            foreach (int k in new[] { t.Neg, t.Pos })
            {
                if (k <= 0) continue;
                if (hit) hits[k]++;
                if (d < near[k]) near[k] = d;
            }
        }
        // Inside more than one (shouldn't happen): the one whose surface is nearest.
        int best = 0; double bd = double.MaxValue;
        for (int k = 1; k <= m.BubbleCount; k++)
            if ((hits[k] & 1) == 1 && near[k] < bd) { bd = near[k]; best = k; }
        if (best > 0 || !nearest) return best;
        for (int k = 1; k <= m.BubbleCount; k++)
            if (near[k] < bd) { bd = near[k]; best = k; }
        return best;
    }
}

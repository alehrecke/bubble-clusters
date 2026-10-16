using System;
using System.Collections.Generic;
using System.Linq;

// Makes a rebuild act locally (Phase 3). The rebuilt mesh comes from fitted functions, so away
// from the event it only approximates the films it replaces. Squeezed small bubbles lost 20-30%
// of their volume per rebuild and the whole cluster visibly jumped (adds28 run: 19 rebuilds, bubbles at
// 71-94% after each). Here every rebuilt vertex outside the event zone is moved back onto the
// old surface with the same labels:
//   film vertex (2 regions)   -> closest point on the old film between the same two regions
//   border vertex (3 regions) -> closest point on the old border between the same three
//   node (4 regions)          -> the old node with the same four
// Topology comes from the rebuild and shape from the old mesh. Inside the zone the rebuilt
// geometry is kept (that's where the topology changed), with a smooth blend between the two.
// Vertices that find no old match nearby (new walls, new borders) stay where they are.
public static class ShapeTransfer
{
    public class Stats
    {
        public int Moved, Kept, Unmatched;
        public double MeanShift, MaxShift;
        public string Volumes = "";
        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "shape transfer: {0} vertices moved back onto the old films (mean {1:G2}, max {2:G2}), {3} kept in the event zone, {4} unmatched",
                Moved, MeanShift, MaxShift, Kept, Unmatched) + Volumes;
        }
    }

    // zoneAt / zoneR: event points and radii. Full transfer beyond zoneR + inner, none within
    // zoneR, smoothstep blend over `blend`. maxDist: an old match further away than this is ignored.
    // trust: shifts up to this are fit error and fully undone; from there to 2 x trust the transfer
    // fades out. A larger shift means the films really moved (e.g. around a dragged bubble whose pinch
    // spans its whole outline): pulling those back made bubble 14 136% in the drag_gh replay.
    // The weight field is then eased over the mesh so a vertex that keeps its rebuilt position
    // doesn't leave a step next to transferred neighbours.
    public static Stats Apply(FoamMesh nm, FoamMesh old, IList<Vec3> zoneAt, IList<double> zoneR, double inner, double blend, double maxDist, double trust)
    {
        var st = new Stats();
        var vOld = old.Volumes();
        var vBefore = nm.Volumes();
        var oldReg = old.VertexRegions();
        var newReg = nm.VertexRegions();
        var oldEdges = old.EdgeTris();

        // Old incident triangles and border edges per vertex.
        var vt = new List<int>[old.V.Count];
        var vb = new List<int>[old.V.Count];       // border neighbours
        for (int v = 0; v < old.V.Count; v++) { vt[v] = new List<int>(6); vb[v] = new List<int>(2); }
        for (int i = 0; i < old.T.Count; i++) { var t = old.T[i]; vt[t.A].Add(i); vt[t.B].Add(i); vt[t.C].Add(i); }
        foreach (var kv in oldEdges)
        {
            if (!old.IsBorder(kv.Value)) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            vb[a].Add(b); vb[b].Add(a);
        }

        // Hash of old vertices.
        double cell = maxDist;
        var grid = new Dictionary<long, List<int>>(LongKey.Comparer);
        for (int v = 0; v < old.V.Count; v++)
        {
            if (oldReg[v].Count < 2) continue;
            long k = Cell(old.V[v], cell);
            List<int> l;
            if (!grid.TryGetValue(k, out l)) { l = new List<int>(); grid[k] = l; }
            l.Add(v);
        }

        double sum = 0;
        var target = new Vec3[nm.V.Count];
        var wt = new double[nm.V.Count];
        for (int v = 0; v < nm.V.Count; v++)
        {
            var labels = newReg[v];
            if (labels.Count < 2) continue;
            var p = nm.V[v];
            double w = Weight(p, zoneAt, zoneR, inner, blend);
            if (w <= 0) { st.Kept++; continue; }

            // Nearest old vertex whose labels contain this vertex's labels.
            int best = -1; double bd = maxDist * maxDist;
            long cx = (long)Math.Floor(p.X / cell), cy = (long)Math.Floor(p.Y / cell), cz = (long)Math.Floor(p.Z / cell);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        List<int> l;
                        if (!grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out l)) continue;
                        foreach (int o in l)
                        {
                            double d = (old.V[o] - p).LengthSq;
                            if (d < bd && Contains(oldReg[o], labels)) { bd = d; best = o; }
                        }
                    }
            if (best < 0) { st.Unmatched++; continue; }

            Vec3 q;
            if (labels.Count >= 4)
            {
                if (oldReg[best].Count != labels.Count) { st.Unmatched++; continue; }
                q = old.V[best];
            }
            else if (labels.Count == 3)
                q = ClosestOnBorder(old, oldEdges, vb, best, labels, p);
            else
                q = ClosestOnFilm(old, vt, best, labels[0], labels[1], p);
            if (double.IsNaN(q.X)) { st.Unmatched++; continue; }

            double shift = (q - p).Length;
            double x = (2 * trust - shift) / trust;      // 1 at shift <= trust, 0 at 2 x trust
            double wTrust = x >= 1 ? 1 : x <= 0 ? 0 : x * x * (3 - 2 * x);
            if (wTrust <= 0) { st.Unmatched++; continue; }
            target[v] = q;
            wt[v] = w * wTrust;
            st.Moved++;
            sum += shift;
            if (shift > st.MaxShift) st.MaxShift = shift;
        }
        // Ease the weights: a vertex never transfers more than the average of itself and its ring.
        var ring = new List<int>[nm.V.Count];
        for (int v = 0; v < nm.V.Count; v++) ring[v] = new List<int>(6);
        foreach (var kv in nm.EdgeTris())
        {
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            ring[a].Add(b); ring[b].Add(a);
        }
        for (int it = 0; it < 3; it++)
        {
            var nw = new double[wt.Length];
            for (int v = 0; v < wt.Length; v++)
            {
                if (wt[v] <= 0 || ring[v].Count == 0) continue;
                double avg = 0;
                foreach (int u in ring[v]) avg += wt[u];
                avg /= ring[v].Count;
                nw[v] = Math.Min(wt[v], 0.5 * (wt[v] + avg));
            }
            wt = nw;
        }
        for (int v = 0; v < nm.V.Count; v++)
            if (wt[v] > 0) nm.V[v] = nm.V[v] + (target[v] - nm.V[v]) * wt[v];
        st.MeanShift = st.Moved > 0 ? sum / st.Moved : 0;
        var vAfter = nm.Volumes();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var parts = new List<string>();
        for (int k = 1; k < vOld.Length && k < vAfter.Length; k++)
            if (vOld[k] > 0 && (Math.Abs(vAfter[k] / vOld[k] - 1) > 0.05 || Math.Abs(vBefore[k] / vOld[k] - 1) > 0.05))
                parts.Add(k + ": " + (vBefore[k] / vOld[k]).ToString("P0", ci) + " -> " + (vAfter[k] / vOld[k]).ToString("P0", ci));
        if (parts.Count > 0) st.Volumes = "; volume vs snapshot, rebuilt -> transferred: " + string.Join(", ", parts);
        return st;
    }

    static double Weight(Vec3 p, IList<Vec3> at, IList<double> r, double inner, double blend)
    {
        double d = double.MaxValue;
        for (int i = 0; i < at.Count; i++) d = Math.Min(d, (p - at[i]).Length - r[i]);
        double x = (d - inner) / Math.Max(blend, 1e-12);
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        return x * x * (3 - 2 * x);
    }

    static bool Contains(List<int> sup, List<int> sub)
    {
        foreach (int r in sub) if (!sup.Contains(r)) return false;
        return true;
    }

    // Closest point on the old film between regions a and b, among the triangles around `start`
    // and its neighbours (2-ring), which is enough at rebuild-grid distances.
    static Vec3 ClosestOnFilm(FoamMesh old, List<int>[] vt, int start, int a, int b, Vec3 p)
    {
        var ring = new HashSet<int>();
        foreach (int ti in vt[start])
        {
            var t = old.T[ti];
            ring.Add(t.A); ring.Add(t.B); ring.Add(t.C);
        }
        Vec3 best = new Vec3(double.NaN, 0, 0); double bd = double.MaxValue;
        var done = new HashSet<int>();
        foreach (int u in ring)
            foreach (int ti in vt[u])
            {
                if (!done.Add(ti)) continue;
                var t = old.T[ti];
                if (!((t.Neg == a && t.Pos == b) || (t.Neg == b && t.Pos == a))) continue;
                var q = FoamAnalysis.ClosestOnTriangle(p, old.V[t.A], old.V[t.B], old.V[t.C]);
                double d = (q - p).LengthSq;
                if (d < bd) { bd = d; best = q; }
            }
        return best;
    }

    // Closest point on the old border with these three regions, walking a few edges along it
    // from `start` in both directions.
    static Vec3 ClosestOnBorder(FoamMesh old, Dictionary<long, List<int>> oldEdges, List<int>[] vb, int start, List<int> labels, Vec3 p)
    {
        Vec3 best = new Vec3(double.NaN, 0, 0); double bd = double.MaxValue;
        var seen = new HashSet<int> { start };
        var frontier = new List<int> { start };
        for (int depth = 0; depth < 4 && frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (int u in frontier)
                foreach (int w in vb[u])
                {
                    List<int> tris;
                    if (!oldEdges.TryGetValue(FoamMesh.EdgeKey(u, w), out tris)) continue;
                    bool match = true;
                    foreach (int r in labels)
                    {
                        bool any = false;
                        foreach (int ti in tris) if (old.T[ti].Neg == r || old.T[ti].Pos == r) { any = true; break; }
                        if (!any) { match = false; break; }
                    }
                    if (!match) continue;
                    var q = FoamAnalysis.ClosestOnSegment(p, old.V[u], old.V[w]);
                    double d = (q - p).LengthSq;
                    if (d < bd) { bd = d; best = q; }
                    if (seen.Add(w)) next.Add(w);
                }
            frontier = next;
        }
        return best;
    }

    static long Cell(Vec3 p, double cell)
    {
        return Key((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
    }

    static long Key(long x, long y, long z)
    {
        unchecked { return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L); }
    }
}

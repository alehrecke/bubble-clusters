using System;
using System.Collections.Generic;
using System.Linq;

// Isotropic remeshing (Botsch & Kobbelt 2004) adapted to multi-region films:
//   split edges > 4/3 L, collapse edges < 4/5 L, flip film edges toward Delaunay.
// Plateau borders are features: they are split along themselves, a border vertex only
// collapses along its border, and border edges are never flipped. Region labels and
// orientation are carried onto every new triangle, so volumes are untouched (splits and
// flips) or change only slightly (collapses; the solver's volume correction absorbs that).
//
// Implementation (2026-10-16): each operation builds a vertex -> triangle table once (Topo) and
// updates it as it splits / collapses / flips, so edge lookups are small list intersections. The
// first version rebuilt a global edge dictionary, region lists and neighbour sets on every pass:
// ~540 ms for one remesh round on a 45k-triangle mesh that needed almost nothing, and ~4 s of each
// ~5 s rebuild.
public static class Remesher
{
    public class Result { public int Splits, Collapses, Flips; }

    // Vertex -> live triangle table over a FoamMesh, kept current by the operations below.
    sealed class Topo
    {
        public readonly FoamMesh M;
        public readonly List<List<int>> VT;
        public readonly List<bool> Dead;
        readonly List<int> scratch = new List<int>(8);

        public Topo(FoamMesh m)
        {
            M = m;
            VT = new List<List<int>>(m.V.Count + 64);
            for (int v = 0; v < m.V.Count; v++) VT.Add(new List<int>(7));
            Dead = new List<bool>(m.T.Count + 64);
            for (int i = 0; i < m.T.Count; i++)
            {
                var t = m.T[i];
                VT[t.A].Add(i); VT[t.B].Add(i); VT[t.C].Add(i);
                Dead.Add(false);
            }
        }

        public int AddVertex(Vec3 p) { M.V.Add(p); VT.Add(new List<int>(7)); return M.V.Count - 1; }

        public int AddTri(FoamTri t)
        {
            int i = M.T.Count;
            M.T.Add(t); Dead.Add(false);
            VT[t.A].Add(i); VT[t.B].Add(i); VT[t.C].Add(i);
            return i;
        }

        public static bool Has(FoamTri t, int v) { return t.A == v || t.B == v || t.C == v; }

        // Live triangles on edge (a, b), into `res` (cleared first).
        public List<int> EdgeTris(int a, int b, List<int> res)
        {
            res.Clear();
            var la = VT[a];
            for (int i = 0; i < la.Count; i++) if (Has(M.T[la[i]], b)) res.Add(la[i]);
            return res;
        }

        public bool EdgeExists(int a, int b)
        {
            var la = VT[a];
            for (int i = 0; i < la.Count; i++) if (Has(M.T[la[i]], b)) return true;
            return false;
        }

        public List<int> Ring(int v, List<int> res)
        {
            res.Clear();
            foreach (int ti in VT[v])
            {
                var t = M.T[ti];
                if (t.A != v && !res.Contains(t.A)) res.Add(t.A);
                if (t.B != v && !res.Contains(t.B)) res.Add(t.B);
                if (t.C != v && !res.Contains(t.C)) res.Add(t.C);
            }
            return res;
        }

        public List<int> Regions(int v, List<int> res)
        {
            res.Clear();
            foreach (int ti in VT[v])
            {
                var t = M.T[ti];
                if (!res.Contains(t.Neg)) res.Add(t.Neg);
                if (!res.Contains(t.Pos)) res.Add(t.Pos);
            }
            res.Sort();
            return res;
        }

        public bool EdgeIsBorder(int a, int b) { return M.IsBorder(EdgeTris(a, b, scratch)); }

        // Number of border edges at v.
        public int BorderCount(int v)
        {
            int n = 0;
            var ring = Ring(v, new List<int>(8));
            foreach (int w in ring) if (EdgeIsBorder(v, w)) n++;
            return n;
        }

        // All live edges, each once, as (min << 32 | max) keys.
        public long[] Edges()
        {
            var keys = new long[3 * M.T.Count];
            int n = 0;
            for (int i = 0; i < M.T.Count; i++)
            {
                if (Dead[i]) continue;
                var t = M.T[i];
                keys[n++] = FoamMesh.EdgeKey(t.A, t.B); keys[n++] = FoamMesh.EdgeKey(t.B, t.C); keys[n++] = FoamMesh.EdgeKey(t.C, t.A);
            }
            Array.Sort(keys, 0, n);
            int u = 0;
            for (int i = 0; i < n; i++) if (i == 0 || keys[i] != keys[i - 1]) keys[u++] = keys[i];
            Array.Resize(ref keys, u);
            return keys;
        }

        // Edges to examine: all of them (dirty == null, first pass), else only those touching a vertex
        // changed in the previous pass. Later passes then cost O(changes), not O(mesh).
        public long[] Edges(HashSet<int> dirty)
        {
            if (dirty == null) return Edges();
            var set = new HashSet<long>(LongKey.Comparer);
            var ring = new List<int>(8);
            foreach (int v in dirty)
            {
                if (v >= VT.Count) continue;
                foreach (int w in Ring(v, ring)) set.Add(FoamMesh.EdgeKey(v, w));
            }
            var keys = set.ToArray();
            Array.Sort(keys);
            return keys;
        }

        public bool[] BorderVertices(long[] edges)
        {
            var on = new bool[M.V.Count];
            foreach (long e in edges)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                if (EdgeIsBorder(a, b)) on[a] = on[b] = true;
            }
            return on;
        }

        public void Kill(int ti)
        {
            if (Dead[ti]) return;
            Dead[ti] = true;
            var t = M.T[ti];
            VT[t.A].Remove(ti); VT[t.B].Remove(ti); VT[t.C].Remove(ti);
        }

        // Drop dead triangles (and, via Clean, unused vertices and duplicates).
        public void Compact()
        {
            if (!Dead.Contains(true)) return;
            var keep = new List<FoamTri>(M.T.Count);
            for (int i = 0; i < M.T.Count; i++) if (!Dead[i]) keep.Add(M.T[i]);
            M.T = keep;
            M.Clean();
        }
    }

    // Optional per-region size scale in (0, 1] (index = region, [0] = outside = 1): edges touching
    // region k aim for L * scale[k], so small bubbles get proportionally finer triangles.
    // Passed as a parameter (not a static) because rebuilds run on a background thread.
    static double EdgeScale(FoamMesh m, List<int> tris, double[] sc)
    {
        if (sc == null) return 1;
        double s = 1;
        foreach (int ti in tris)
        {
            var t = m.T[ti];
            if (t.Neg >= 0 && t.Neg < sc.Length && sc[t.Neg] < s) s = sc[t.Neg];
            if (t.Pos >= 0 && t.Pos < sc.Length && sc[t.Pos] < s) s = sc[t.Pos];
        }
        return s;
    }

    // MaxPasses caps the inner repeat of each operation (split / collapse / flip). The mesher
    // wants full cleanup (6); the solver's periodic remesh uses 2.
    public static Result Run(FoamMesh m, double L, int iterations, double[] scale = null, int maxPasses = 6)
    {
        var r = new Result();
        for (int it = 0; it < iterations; it++)
        {
            r.Splits += SplitLong(m, 4.0 / 3.0 * L, true, scale, maxPasses);
            r.Collapses += CollapseShort(m, 0.8 * L, 4.0 / 3.0 * L, scale, maxPasses);
            r.Flips += FlipDelaunay(m, Math.Min(4, maxPasses));
        }
        return r;
    }

    // Also splits "chords": film edges whose two ends are both on Plateau borders. A film
    // strip made only of border vertices has no interior to bend, and its triangles lie along
    // the border, kinking it (found in the first GH run: period-3 oscillation).
    public static int SplitChords(FoamMesh m) { return SplitLong(m, double.MaxValue, true); }

    public static int SplitLong(FoamMesh m, double maxLen, bool chords = false, double[] scale = null, int maxPasses = 6)
    {
        var tp = new Topo(m);
        var tris = new List<int>(4);
        int total = 0;
        List<bool> onBorder = chords ? new List<bool>(tp.BorderVertices(tp.Edges())) : null;
        HashSet<int> dirty = null;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var edges = tp.Edges(dirty);
            dirty = new HashSet<int>();
            var cand = new List<KeyValuePair<long, double>>();
            foreach (long e in edges)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                double len = (m.V[a] - m.V[b]).Length;
                tp.EdgeTris(a, b, tris);
                bool chord = chords && tris.Count == 2 && onBorder[a] && onBorder[b] && !m.IsBorder(tris);
                if (len > maxLen * EdgeScale(m, tris, scale) || chord) cand.Add(new KeyValuePair<long, double>(e, len));
            }
            if (cand.Count == 0) break;
            cand.Sort((p, q) => q.Value.CompareTo(p.Value));
            int done = 0;
            foreach (var kv in cand)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                tp.EdgeTris(a, b, tris);
                if (tris.Count == 0) continue;            // already split this pass
                bool border = m.IsBorder(tris);
                int mid = tp.AddVertex((m.V[a] + m.V[b]) * 0.5);
                if (chords) onBorder.Add(border);         // a border's midpoint is on the border; a film edge's isn't
                foreach (int ti in tris.ToArray())
                {
                    var t = m.T[ti];
                    // Rotate so the split edge is (A,B), keeping orientation.
                    if (!IsEdge(t.A, t.B, a, b))
                    {
                        if (IsEdge(t.B, t.C, a, b)) t = new FoamTri(t.B, t.C, t.A, t.Neg, t.Pos);
                        else t = new FoamTri(t.C, t.A, t.B, t.Neg, t.Pos);
                    }
                    // (A, B, C) -> (A, mid, C) in place + new (mid, B, C).
                    m.T[ti] = new FoamTri(t.A, mid, t.C, t.Neg, t.Pos);
                    tp.VT[t.B].Remove(ti);
                    tp.VT[mid].Add(ti);
                    tp.AddTri(new FoamTri(mid, t.B, t.C, t.Neg, t.Pos));
                    dirty.Add(t.C);
                }
                dirty.Add(mid); dirty.Add(a); dirty.Add(b);
                done++;
            }
            total += done;
            if (done == 0) break;
        }
        return total;
    }

    public static bool[] BorderVertices(FoamMesh m, Dictionary<long, List<int>> edges)
    {
        var onBorder = new bool[m.V.Count];
        foreach (var kv in edges)
            if (m.IsBorder(kv.Value)) { onBorder[(int)(kv.Key >> 32)] = true; onBorder[(int)(kv.Key & 0xffffffff)] = true; }
        return onBorder;
    }

    static bool IsEdge(int p, int q, int a, int b) { return (p == a && q == b) || (p == b && q == a); }

    public static int CollapseShort(FoamMesh m, double minLen, double maxLen, double[] scale = null, int maxPasses = 6)
    {
        var tp = new Topo(m);
        var etris = new List<int>(4);
        var regU = new List<int>(5); var regV = new List<int>(5);
        var ringU = new List<int>(8); var ringV = new List<int>(8); var ringD = new List<int>(8); var ringK = new List<int>(8);
        var opp = new List<int>(4);
        var et = new List<int>(4);
        int total = 0;
        HashSet<int> dirty = null;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var cand = new List<KeyValuePair<long, double>>();
            var scan = tp.Edges(dirty);
            dirty = new HashSet<int>();
            foreach (long e in scan)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                double len = (m.V[a] - m.V[b]).Length;
                if (len < minLen * EdgeScale(m, tp.EdgeTris(a, b, etris), scale)) cand.Add(new KeyValuePair<long, double>(e, len));
            }
            if (cand.Count == 0) break;
            cand.Sort((p, q) => p.Value.CompareTo(q.Value));
            int done = 0;
            foreach (var kv in cand)
            {
                int u = (int)(kv.Key >> 32), v = (int)(kv.Key & 0xffffffff);
                tp.EdgeTris(u, v, etris);
                if (etris.Count == 0) continue;                          // gone (a neighbour collapsed)
                double len = (m.V[u] - m.V[v]).Length;
                if (len >= minLen * EdgeScale(m, etris, scale)) continue; // no longer short
                bool borderEdge = m.IsBorder(etris);
                tp.Regions(u, regU); tp.Regions(v, regV);
                int bu = tp.BorderCount(u), bv = tp.BorderCount(v);
                int keep, drop, bKeep, bDrop;
                bool sameClass = regU.Count == regV.Count && Subset(regU, regV);
                if (Subset(regV, regU) && !(sameClass && bv > bu)) { keep = u; drop = v; bKeep = bu; bDrop = bv; }
                else if (Subset(regU, regV)) { keep = v; drop = u; bKeep = bv; bDrop = bu; }
                else continue;
                var regDrop = drop == u ? regU : regV;
                if (regDrop.Count >= 3 && !borderEdge) continue;              // borders collapse only along themselves
                if (bDrop > 2 || (bDrop > 0 && bKeep == 0)) continue;          // never remove nodes
                // Link condition: shared neighbours must be exactly the edge's opposite vertices.
                opp.Clear();
                foreach (int ti in etris)
                {
                    var t = m.T[ti];
                    if (t.A != u && t.A != v) opp.Add(t.A);
                    if (t.B != u && t.B != v) opp.Add(t.B);
                    if (t.C != u && t.C != v) opp.Add(t.C);
                }
                tp.Ring(u, ringU); tp.Ring(v, ringV);
                bool linkOk = true;
                foreach (int w in ringU) if (w != v && ringV.Contains(w) && !opp.Contains(w)) { linkOk = false; break; }
                if (!linkOk) continue;
                // Same-class collapses (film-film, along a border) go to the midpoint.
                Vec3 target = sameClass && bDrop == bKeep ? (m.V[u] + m.V[v]) * 0.5 : m.V[keep];
                // Don't create chords: a film vertex collapsing onto a border vertex must not
                // connect it to another border vertex except along an existing border edge.
                var rDrop = drop == u ? ringU : ringV;
                var rKeep = drop == u ? ringV : ringU;
                bool makesChord = false;
                if (bKeep > 0 && bDrop == 0)
                    foreach (int w in rDrop)
                    {
                        if (w == keep || tp.BorderCount(w) == 0) continue;
                        tp.EdgeTris(keep, w, et);
                        if (et.Count == 0 || !m.IsBorder(et)) { makesChord = true; break; }
                    }
                if (makesChord) continue;
                bool tooLong = false;
                double maxHere = maxLen * EdgeScale(m, etris, scale);
                foreach (int w in rDrop) if ((m.V[w] - target).Length > maxHere) { tooLong = true; break; }
                foreach (int w in rKeep) if (!tooLong && (m.V[w] - target).Length > maxHere) tooLong = true;
                if (tooLong) continue;
                if (!KeepsOrientation(m, tp.VT[drop], drop, keep, target)) continue;
                if (!KeepsOrientation(m, tp.VT[keep], keep, keep, target)) continue;

                // Collapse drop -> keep.
                foreach (int ti in etris.ToArray()) tp.Kill(ti);
                foreach (int ti in tp.VT[drop].ToArray())
                {
                    var t = m.T[ti];
                    if (t.A == drop) t.A = keep;
                    if (t.B == drop) t.B = keep;
                    if (t.C == drop) t.C = keep;
                    m.T[ti] = t;
                    tp.VT[keep].Add(ti);
                }
                tp.VT[drop].Clear();
                m.V[keep] = target;
                dirty.Add(keep);
                foreach (int w in rKeep) dirty.Add(w);
                foreach (int w in rDrop) dirty.Add(w);
                dirty.Remove(drop);
                done++;
            }
            total += done;
            if (done == 0) break;
        }
        tp.Compact();
        return total;
    }

    static bool Subset(List<int> small, List<int> big)
    {
        foreach (int x in small) if (!big.Contains(x)) return false;
        return true;
    }

    // Moving vertex `moved` to `target` (and renaming it `keep`) must not flip or squash
    // any triangle that survives the collapse.
    static bool KeepsOrientation(FoamMesh m, List<int> tris, int moved, int keep, Vec3 target)
    {
        foreach (int ti in tris)
        {
            var t = m.T[ti];
            if (moved != keep && (t.A == keep || t.B == keep || t.C == keep)) continue; // removed
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            Vec3 n0 = Vec3.Cross(b - a, c - a);
            double scale = (b - a).LengthSq + (c - a).LengthSq;
            if (t.A == moved) a = target;
            if (t.B == moved) b = target;
            if (t.C == moved) c = target;
            Vec3 n1 = Vec3.Cross(b - a, c - a);
            if (n0.Length < 1e-6 * scale) continue;
            if (Vec3.Dot(n0, n1) <= 0.3 * n0.Length * n1.Length) return false;
        }
        return true;
    }

    // Flip interior film edges (exactly two triangles of the same film) when the sum of the
    // opposite angles exceeds 180 degrees (Delaunay criterion), if the result doesn't fold.
    public static int FlipDelaunay(FoamMesh m, int maxPasses = 4)
    {
        var tp = new Topo(m);
        var tris = new List<int>(4);
        int total = 0;
        // Flips never touch borders, so which vertices are on one doesn't change.
        var onBorder = tp.BorderVertices(tp.Edges());
        HashSet<int> dirty = null;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var edges = tp.Edges(dirty);
            dirty = new HashSet<int>();
            var touched = new bool[m.T.Count];
            int done = 0;
            foreach (long e in edges)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                tp.EdgeTris(a, b, tris);
                if (tris.Count != 2) continue;
                int t0 = tris[0], t1 = tris[1];
                if (touched[t0] || touched[t1]) continue;
                var A = m.T[t0]; var B = m.T[t1];
                if (A.Neg != B.Neg || A.Pos != B.Pos) continue;
                // Orient A as (p, q, x) with edge p->q, B must then contain q->p.
                A = RotateTo(A, a, b);
                if (A.A < 0) { A = RotateTo(m.T[t0], b, a); if (A.A < 0) continue; }
                int p = A.A, q = A.B, x = A.C;
                var Bq = RotateTo(B, q, p);
                if (Bq.A < 0) continue; // inconsistent orientation
                int y = Bq.C;
                if (x == y) continue;
                if (tp.EdgeExists(x, y)) continue;
                // An "ear" has all three corners on a Plateau border: it lies along the border
                // instead of spanning the film, kinks it, and makes the solver oscillate.
                // Flip it away whenever the other side's apex is off the border.
                bool ear = onBorder[p] && onBorder[q] && (onBorder[x] != onBorder[y]);
                double angX = Angle(m.V[p] - m.V[x], m.V[q] - m.V[x]);
                double angY = Angle(m.V[p] - m.V[y], m.V[q] - m.V[y]);
                if (!ear && angX + angY <= Math.PI + 1e-9) continue;
                // New triangles (x, p, y) and (y, q, x) — check they don't fold.
                Vec3 n0 = Vec3.Cross(m.V[q] - m.V[p], m.V[x] - m.V[p]) + Vec3.Cross(m.V[p] - m.V[q], m.V[y] - m.V[q]);
                Vec3 n1 = Vec3.Cross(m.V[p] - m.V[x], m.V[y] - m.V[x]);
                Vec3 n2 = Vec3.Cross(m.V[q] - m.V[y], m.V[x] - m.V[y]);
                if (Vec3.Dot(n1, n0) <= 0 || Vec3.Dot(n2, n0) <= 0) continue;
                if (!ear && Vec3.Dot(n1, n2) <= 0.5 * n1.Length * n2.Length) continue;
                // t0 (p, q, x) -> (x, p, y): loses q, gains y.  t1 (q, p, y) -> (y, q, x): loses p, gains x.
                m.T[t0] = new FoamTri(x, p, y, A.Neg, A.Pos);
                m.T[t1] = new FoamTri(y, q, x, A.Neg, A.Pos);
                tp.VT[q].Remove(t0); tp.VT[y].Add(t0);
                tp.VT[p].Remove(t1); tp.VT[x].Add(t1);
                touched[t0] = touched[t1] = true;
                dirty.Add(p); dirty.Add(q); dirty.Add(x); dirty.Add(y);
                done++;
            }
            total += done;
            if (done == 0) break;
        }
        return total;
    }

    // Rotate t so that its first two vertices are (a, b) in that order; A = -1 if impossible.
    static FoamTri RotateTo(FoamTri t, int a, int b)
    {
        if (t.A == a && t.B == b) return t;
        if (t.B == a && t.C == b) return new FoamTri(t.B, t.C, t.A, t.Neg, t.Pos);
        if (t.C == a && t.A == b) return new FoamTri(t.C, t.A, t.B, t.Neg, t.Pos);
        return new FoamTri(-1, -1, -1, t.Neg, t.Pos);
    }

    static double Angle(Vec3 u, Vec3 v)
    {
        double c = Vec3.Dot(u, v) / Math.Max(u.Length * v.Length, 1e-300);
        return Math.Acos(Math.Max(-1, Math.Min(1, c)));
    }

    // Edge incidence histogram: [1] open edges (should be 0), [2] film (incl. contact lines on a host), [3] border, [4+] errors.
    public static int[] EdgeHistogram(FoamMesh m)
    {
        var h = new int[6];
        foreach (var kv in m.EdgeTris()) h[Math.Min(kv.Value.Count, 5)]++;
        return h;
    }
}

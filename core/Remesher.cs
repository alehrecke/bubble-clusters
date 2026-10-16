using System;
using System.Collections.Generic;
using System.Linq;

// Isotropic remeshing (Botsch & Kobbelt 2004) adapted to multi-region films:
//   split edges > 4/3 L, collapse edges < 4/5 L, flip film edges toward Delaunay.
// Plateau borders are features: they are split along themselves, a border vertex only
// collapses along its border, and border edges are never flipped. Region labels and
// orientation are carried onto every new triangle, so volumes are untouched (splits and
// flips) or change only slightly (collapses; the solver's volume correction absorbs that).
public static class Remesher
{
    public class Result { public int Splits, Collapses, Flips; }

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
    // wants full cleanup (6). The solver's periodic remesh runs every few steps, so 2 is enough,
    // and each pass rebuilds the edge table (~150 ms per pass at 25k tris in Rhino).
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
        int total = 0;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var edges = m.EdgeTris();
            var onBorder = chords ? BorderVertices(m, edges) : null;
            var cand = new List<KeyValuePair<long, double>>();
            foreach (var kv in edges)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                double len = (m.V[a] - m.V[b]).Length;
                bool chord = chords && kv.Value.Count == 2 && onBorder[a] && onBorder[b] && !m.IsBorder(kv.Value);
                if (len > maxLen * EdgeScale(m, kv.Value, scale) || chord) cand.Add(new KeyValuePair<long, double>(kv.Key, len));
            }
            if (cand.Count == 0) break;
            cand.Sort((p, q) => q.Value.CompareTo(p.Value));
            var touched = new bool[m.T.Count];
            var add = new List<FoamTri>();
            int done = 0;
            foreach (var kv in cand)
            {
                var tris = edges[kv.Key];
                if (tris.Any(t => touched[t])) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                int mid = m.V.Count;
                m.V.Add((m.V[a] + m.V[b]) * 0.5);
                foreach (int ti in tris)
                {
                    touched[ti] = true;
                    var t = m.T[ti];
                    // Rotate so the split edge is (A,B), keeping orientation.
                    if (!IsEdge(t.A, t.B, a, b))
                    {
                        if (IsEdge(t.B, t.C, a, b)) t = new FoamTri(t.B, t.C, t.A, t.Neg, t.Pos);
                        else t = new FoamTri(t.C, t.A, t.B, t.Neg, t.Pos);
                    }
                    m.T[ti] = new FoamTri(t.A, mid, t.C, t.Neg, t.Pos);
                    add.Add(new FoamTri(mid, t.B, t.C, t.Neg, t.Pos));
                }
                done++;
            }
            m.T.AddRange(add);
            total += done;
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
        int total = 0;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var regions = m.VertexRegions();
            var edges = m.EdgeTris();
            int nv = m.V.Count;
            var vt = new List<int>[nv];
            var ring = new HashSet<int>[nv];
            for (int i = 0; i < nv; i++) { vt[i] = new List<int>(8); ring[i] = new HashSet<int>(); }
            for (int i = 0; i < m.T.Count; i++)
            {
                var t = m.T[i];
                vt[t.A].Add(i); vt[t.B].Add(i); vt[t.C].Add(i);
                ring[t.A].Add(t.B); ring[t.A].Add(t.C);
                ring[t.B].Add(t.A); ring[t.B].Add(t.C);
                ring[t.C].Add(t.A); ring[t.C].Add(t.B);
            }
            var border = new int[nv];
            foreach (var kv in edges)
                if (m.IsBorder(kv.Value)) { border[(int)(kv.Key >> 32)]++; border[(int)(kv.Key & 0xffffffff)]++; }

            var cand = new List<KeyValuePair<long, double>>();
            foreach (var kv in edges)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                double len = (m.V[a] - m.V[b]).Length;
                if (len < minLen * EdgeScale(m, kv.Value, scale)) cand.Add(new KeyValuePair<long, double>(kv.Key, len));
            }
            if (cand.Count == 0) break;
            cand.Sort((p, q) => p.Value.CompareTo(q.Value));
            var locked = new bool[nv];
            var map = Enumerable.Range(0, nv).ToArray();
            var newPos = new Dictionary<int, Vec3>();
            int done = 0;
            foreach (var kv in cand)
            {
                int u = (int)(kv.Key >> 32), v = (int)(kv.Key & 0xffffffff);
                if (locked[u] || locked[v]) continue;
                var etris = edges[kv.Key];
                bool borderEdge = m.IsBorder(etris);
                int keep, drop;
                bool sameClass = regions[u].Count == regions[v].Count && Subset(regions[u], regions[v]);
                if (Subset(regions[v], regions[u]) && !(sameClass && border[v] > border[u])) { keep = u; drop = v; }
                else if (Subset(regions[u], regions[v])) { keep = v; drop = u; }
                else continue;
                if (regions[drop].Count >= 3 && !borderEdge) continue;   // borders collapse only along themselves
                if (border[drop] > 2 || (border[drop] > 0 && border[keep] == 0)) continue; // never remove nodes
                // Link condition: shared neighbours must be exactly the edge's opposite vertices.
                var opp = new HashSet<int>();
                foreach (int ti in etris)
                {
                    var t = m.T[ti];
                    opp.Add(t.A); opp.Add(t.B); opp.Add(t.C);
                }
                opp.Remove(u); opp.Remove(v);
                bool linkOk = true;
                foreach (int w in ring[u]) if (w != v && ring[v].Contains(w) && !opp.Contains(w)) { linkOk = false; break; }
                if (!linkOk) continue;
                // Same-class collapses (film-film, along a border) go to the midpoint.
                Vec3 target = sameClass && border[drop] == border[keep] ? (m.V[u] + m.V[v]) * 0.5 : m.V[keep];
                // Don't create chords: a film vertex collapsing onto a border vertex must not
                // connect it to another border vertex except along an existing border edge.
                bool makesChord = false;
                if (border[keep] > 0 && border[drop] == 0)
                    foreach (int w in ring[drop])
                    {
                        if (w == keep || border[w] == 0) continue;
                        List<int> et;
                        if (!edges.TryGetValue(FoamMesh.EdgeKey(keep, w), out et) || !m.IsBorder(et)) { makesChord = true; break; }
                    }
                if (makesChord) continue;
                bool tooLong = false;
                double maxHere = maxLen * EdgeScale(m, etris, scale);
                foreach (int w in ring[drop]) if ((m.V[w] - target).Length > maxHere) { tooLong = true; break; }
                foreach (int w in ring[keep]) if (!tooLong && (m.V[w] - target).Length > maxHere) tooLong = true;
                if (tooLong) continue;
                if (!KeepsOrientation(m, vt[drop], drop, keep, target)) continue;
                if (!KeepsOrientation(m, vt[keep], keep, keep, target)) continue;
                map[drop] = keep;
                newPos[keep] = target;
                locked[u] = locked[v] = true;
                foreach (int w in ring[u]) locked[w] = true;
                foreach (int w in ring[v]) locked[w] = true;
                done++;
            }
            if (done == 0) break;
            foreach (var kv in newPos) m.V[kv.Key] = kv.Value;
            for (int i = 0; i < m.T.Count; i++)
            {
                var t = m.T[i];
                t.A = map[t.A]; t.B = map[t.B]; t.C = map[t.C];
                m.T[i] = t;
            }
            m.Clean();
            total += done;
        }
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
        int total = 0;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var edges = m.EdgeTris();
            var touched = new bool[m.T.Count];
            var onBorder = new bool[m.V.Count];
            foreach (var kv in edges)
                if (m.IsBorder(kv.Value)) { onBorder[(int)(kv.Key >> 32)] = true; onBorder[(int)(kv.Key & 0xffffffff)] = true; }
            int done = 0;
            foreach (var kv in edges)
            {
                if (kv.Value.Count != 2) continue;
                int t0 = kv.Value[0], t1 = kv.Value[1];
                if (touched[t0] || touched[t1]) continue;
                var A = m.T[t0]; var B = m.T[t1];
                if (A.Neg != B.Neg || A.Pos != B.Pos) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                // Orient A as (p, q, x) with edge p->q, B must then contain q->p.
                A = RotateTo(A, a, b);
                if (A.A < 0) continue;
                int p = A.A, q = A.B, x = A.C;
                var Bq = RotateTo(B, q, p);
                if (Bq.A < 0) continue; // inconsistent orientation
                int y = Bq.C;
                if (x == y) continue;
                if (edges.ContainsKey(FoamMesh.EdgeKey(x, y))) continue;
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
                m.T[t0] = new FoamTri(x, p, y, A.Neg, A.Pos);
                m.T[t1] = new FoamTri(y, q, x, A.Neg, A.Pos);
                touched[t0] = touched[t1] = true;
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

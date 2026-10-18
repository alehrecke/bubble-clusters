using System;
using System.Collections.Generic;
using System.Linq;

// New contacts between separate pieces of foam (Phase 3, "contact" events).
//
// Bubbles that share no wall don't interact at all in the film solver: a bubble dragged out
// of the cluster (separated by a rebuild) and dragged back passed straight through the others
// (Alex's run 2026-10-15_213610). Here bubbles are grouped into connected pieces (by shared
// walls), and every pair of pieces is tested:
//   near    - an outer-film vertex of one piece within eps of an outer-film vertex of the other
//   inside  - an outer-film vertex of one piece lies inside the other piece (ray parity against
//             its closed outer surface): covers a bubble pushed deep into the cluster
// The event lists the smaller piece's bubbles in Grow: the rebuild enlarges those functions by
// about a grid cell before volume calibration, so the two pieces overlap and the diagram gives
// them a shared wall. Calibration then restores every volume (and overlap only grows under it,
// because each side loses volume to the other).
public static class Contacts
{
    // Connected pieces: pieceOf[k] = a representative bubble of k's piece (0 for bubbles with no films);
    // onHost[rep] = true when any bubble of that piece sits on the host (has a footprint).
    public static int[] Pieces(FoamMesh m, out bool[] onHost)
    {
        int n = m.BubbleCount;
        var parent = Enumerable.Range(0, n + 1).ToArray();
        Func<int, int> find = null;
        find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
        var alive = new bool[n + 1];
        var foot = new bool[n + 1];
        foreach (var t in m.T)
        {
            if (t.Neg > 0) alive[t.Neg] = true;
            if (t.Pos > 0) alive[t.Pos] = true;
            if (FoamMesh.IsSolid(t)) { int b = t.Neg == FoamMesh.Solid ? t.Pos : t.Neg; if (b > 0) foot[b] = true; }
            if (t.Neg > 0 && t.Pos > 0) parent[find(t.Neg)] = find(t.Pos);
        }
        var piece = new int[n + 1];
        onHost = new bool[n + 1];
        for (int k = 1; k <= n; k++)
        {
            if (!alive[k]) continue;
            piece[k] = find(k);
            if (foot[k]) onHost[piece[k]] = true;
        }
        return piece;
    }

    public static List<Rearranger.Event> Detect(FoamSolver s)
    {
        var m = s.Mesh;
        int n = m.BubbleCount;
        var events = new List<Rearranger.Event>();

        // Pieces: union-find over bubbles sharing a wall.
        var parent = Enumerable.Range(0, n + 1).ToArray();
        Func<int, int> find = null;
        find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
        var outerTris = new List<int>[n + 1];
        var footTris = new List<int>[n + 1];
        for (int k = 0; k <= n; k++) { outerTris[k] = new List<int>(); footTris[k] = new List<int>(); }
        var onFoot = new bool[m.V.Count];
        for (int i = 0; i < m.T.Count; i++)
        {
            var t = m.T[i];
            if (FoamMesh.IsSolid(t))
            {
                int b = t.Neg == FoamMesh.Solid ? t.Pos : t.Neg;
                if (b > 0) footTris[b].Add(i);
                onFoot[t.A] = onFoot[t.B] = onFoot[t.C] = true;
                continue;
            }
            if (t.Neg > 0 && t.Pos > 0) { parent[find(t.Neg)] = find(t.Pos); continue; }
            int k = t.Neg == 0 ? t.Pos : t.Neg;
            if (k > 0) outerTris[k].Add(i);
        }
        var pieces = new Dictionary<int, List<int>>();
        for (int k = 1; k <= n; k++)
        {
            if (s.IsDead(k) || outerTris[k].Count == 0) continue;
            int r = find(k);
            List<int> l;
            if (!pieces.TryGetValue(r, out l)) { l = new List<int>(); pieces[r] = l; }
            l.Add(k);
        }
        double eps = 0.5 * s.TargetEdge;

        // Landing on the host: a piece with no footprint whose outer film comes within eps of the
        // surface, or any piece whose outer film dips below it. The rebuild hands the part below the
        // surface to the solid, and the bubble becomes a dome sitting on it.
        if (s.Host != null)
            foreach (var p in pieces.Values)
            {
                bool hasFoot = p.Any(k => footTris[k].Count > 0);
                double limit = hasFoot ? -0.5 * eps : eps;
                int hitK = -1; Vec3 hitAt = Vec3.Zero; double hitH = double.MaxValue;
                foreach (int k in p)
                    foreach (int ti in outerTris[k])
                    {
                        var t = m.T[ti];
                        foreach (int v in new[] { t.A, t.B, t.C })
                        {
                            if (onFoot[v]) continue;
                            double hv = s.Host.Height(m.V[v]);
                            if (hv < limit && hv < hitH) { hitH = hv; hitK = k; hitAt = m.V[v]; }
                        }
                    }
                if (hitK > 0)
                {
                    // Landing: shift the bubble so its lowest point ends up one rebuild-grid cell inside the solid.
                    double cell = s.TargetEdge * s.S.RebuildGridFactor;
                    Vec3 shift = hasFoot ? Vec3.Zero : s.Host.Normal(hitAt) * (-(Math.Max(hitH, 0) + cell));
                    events.Add(new Rearranger.Event
                    {
                        Kind = "contact", Regions = new[] { hitK, FoamMesh.Solid }, Value = 0,
                        Detail = (hasFoot ? "pushed into" : "lands on") + " the surface (" + p.Count + "-bubble piece)",
                        At = s.Host.Project(hitAt), Radius = 4 * eps, Shift = shift,
                    });
                }
            }

        if (pieces.Count < 2) return events;
        var list = pieces.Values.ToList();
        // The piece's closed surface for the inside test includes its footprints.
        var tris = list.Select(p => p.SelectMany(k => outerTris[k].Concat(footTris[k])).ToList()).ToList();
        var verts = list.Select(p => p.SelectMany(k => outerTris[k]).ToList()).ToList().Select(ts => ts.SelectMany(ti => new[] { m.T[ti].A, m.T[ti].B, m.T[ti].C }).Distinct().ToList()).ToList();
        var lo = new Vec3[list.Count]; var hi = new Vec3[list.Count];
        for (int p = 0; p < list.Count; p++)
        {
            lo[p] = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            hi[p] = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (int v in verts[p])
            {
                var q = m.V[v];
                lo[p] = new Vec3(Math.Min(lo[p].X, q.X), Math.Min(lo[p].Y, q.Y), Math.Min(lo[p].Z, q.Z));
                hi[p] = new Vec3(Math.Max(hi[p].X, q.X), Math.Max(hi[p].Y, q.Y), Math.Max(hi[p].Z, q.Z));
            }
        }
        for (int a = 0; a < list.Count; a++)
            for (int b = a + 1; b < list.Count; b++)
            {
                if (lo[a].X > hi[b].X + eps || lo[b].X > hi[a].X + eps || lo[a].Y > hi[b].Y + eps ||
                    lo[b].Y > hi[a].Y + eps || lo[a].Z > hi[b].Z + eps || lo[b].Z > hi[a].Z + eps) continue;
                string how = null;
                Vec3 at;
                if (Near(m, verts[a], verts[b], eps, out at)) how = "near";
                else if (AnyInside(m, verts[a], tris[b], out at) || AnyInside(m, verts[b], tris[a], out at)) how = "inside";
                if (how == null) continue;
                int small = list[a].Count <= list[b].Count ? a : b, big = small == a ? b : a;
                events.Add(new Rearranger.Event
                {
                    Kind = "contact",
                    Regions = new[] { list[small][0], list[big][0] },
                    Value = 0,
                    Grow = list[small].ToArray(),
                    Detail = how + " (" + list[small].Count + "-bubble piece touches " + list[big].Count + "-bubble piece)",
                    At = at, Radius = 4 * eps,
                });
            }
        return events;
    }

    // Any vertex of A within eps of a vertex of B (spatial hash on B).
    static bool Near(FoamMesh m, List<int> a, List<int> b, double eps, out Vec3 at)
    {
        at = Vec3.Zero;
        var cells = new Dictionary<long, List<int>>(LongKey.Comparer);
        // unchecked inside the lambda: Rhino compiles with overflow checks, and an unchecked block
        // at the call site doesn't reach into a lambda body.
        Func<Vec3, long> key = p => unchecked(((long)Math.Floor(p.X / eps) * 73856093L) ^ ((long)Math.Floor(p.Y / eps) * 19349663L) ^ ((long)Math.Floor(p.Z / eps) * 83492791L));
        foreach (int v in b)
        {
            long k;
            unchecked { k = key(m.V[v]); }
            List<int> l;
            if (!cells.TryGetValue(k, out l)) { l = new List<int>(); cells[k] = l; }
            l.Add(v);
        }
        double e2 = eps * eps;
        foreach (int v in a)
        {
            var p = m.V[v];
            long cx = (long)Math.Floor(p.X / eps), cy = (long)Math.Floor(p.Y / eps), cz = (long)Math.Floor(p.Z / eps);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        long k;
                        unchecked { k = ((cx + dx) * 73856093L) ^ ((cy + dy) * 19349663L) ^ ((cz + dz) * 83492791L); }
                        List<int> l;
                        if (!cells.TryGetValue(k, out l)) continue;
                        foreach (int w in l) if ((m.V[w] - p).LengthSq < e2) { at = (m.V[w] + p) * 0.5; return true; }
                    }
        }
        return false;
    }

    // Is any of (a subsample of) the points inside the closed surface made of `tris`? Ray parity
    // along a fixed, slightly skewed direction (avoids hitting edges exactly).
    static bool AnyInside(FoamMesh m, List<int> pts, List<int> tris, out Vec3 at)
    {
        at = Vec3.Zero;
        var dir = new Vec3(0.8017837, 0.2672612, 0.5345225);
        int stride = Math.Max(1, pts.Count / 120);
        for (int i = 0; i < pts.Count; i += stride)
        {
            var o = m.V[pts[i]];
            int hits = 0;
            foreach (int ti in tris)
            {
                var t = m.T[ti];
                if (RayHits(o, dir, m.V[t.A], m.V[t.B], m.V[t.C])) hits++;
            }
            if ((hits & 1) == 1) { at = o; return true; }
        }
        return false;
    }

    // Möller–Trumbore, t > 0.
    public static bool RayHits(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 e1 = b - a, e2 = c - a, p = Vec3.Cross(d, e2);
        double det = Vec3.Dot(e1, p);
        if (Math.Abs(det) < 1e-14) return false;
        double inv = 1 / det;
        Vec3 tv = o - a;
        double u = Vec3.Dot(tv, p) * inv;
        if (u < 0 || u > 1) return false;
        Vec3 q = Vec3.Cross(tv, e1);
        double v = Vec3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return false;
        return Vec3.Dot(e2, q) * inv > 1e-12;
    }
}

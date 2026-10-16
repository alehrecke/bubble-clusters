using System;
using System.Collections.Generic;
using System.Linq;

// One triangle of film. Its normal, (B-A)x(C-A), points out of region Neg and into region Pos.
// Region 0 is the outside air; bubbles are 1..BubbleCount.
public struct FoamTri
{
    public int A, B, C, Neg, Pos;
    public FoamTri(int a, int b, int c, int neg, int pos) { A = a; B = b; C = c; Neg = neg; Pos = pos; }
}

// A non-manifold triangle mesh of soap films. Plateau borders are edges with 3+ triangles.
public class FoamMesh
{
    public List<Vec3> V = new List<Vec3>();
    public List<FoamTri> T = new List<FoamTri>();
    public int BubbleCount;

    // Region label of a host solid (see HostSurface). Negative, so every "region > 0" test treats it
    // like the outside, which is what it is for volumes.
    public const int Solid = -1;

    public static bool IsSolid(FoamTri t) { return t.Neg == Solid || t.Pos == Solid; }

    // A Plateau border or other feature edge: 3+ films meet, or a film meets a footprint on a
    // host (that contact line has only 2 triangles). Other 2-triangle edges with mismatched films
    // only appear transiently in the mesher's soup, and must stay collapsible there: counting them
    // as borders made rebuilds fail (cell01 replay: inside-out bubble, open edges).
    public bool IsBorder(List<int> tris)
    {
        if (tris.Count >= 3) return true;
        if (tris.Count != 2) return false;
        var a = T[tris[0]]; var b = T[tris[1]];
        if (!IsSolid(a) && !IsSolid(b)) return false;
        return !((a.Neg == b.Neg && a.Pos == b.Pos) || (a.Neg == b.Pos && a.Pos == b.Neg));
    }

    public FoamMesh Clone()
    {
        var m = new FoamMesh { BubbleCount = BubbleCount };
        m.V.AddRange(V);
        m.T.AddRange(T);
        return m;
    }

    public static long EdgeKey(int a, int b)
    {
        return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    }

    public double TriArea(FoamTri t)
    {
        return 0.5 * Vec3.Cross(V[t.B] - V[t.A], V[t.C] - V[t.A]).Length;
    }

    // Film area (footprints on a host don't count: they carry no tension).
    public double TotalArea()
    {
        double s = 0;
        foreach (var t in T) if (!IsSolid(t)) s += TriArea(t);
        return s;
    }

    // Signed volume of each region (index 0 = outside, left at 0).
    public double[] Volumes()
    {
        var vol = new double[BubbleCount + 1];
        foreach (var t in T)
        {
            double v = Vec3.Dot(V[t.A], Vec3.Cross(V[t.B], V[t.C])) / 6.0;
            if (t.Neg > 0) vol[t.Neg] += v;
            if (t.Pos > 0) vol[t.Pos] -= v;
        }
        return vol;
    }

    public double MeanEdgeLength()
    {
        if (T.Count == 0) return 0;
        double s = 0;
        foreach (var t in T)
            s += (V[t.B] - V[t.A]).Length + (V[t.C] - V[t.B]).Length + (V[t.A] - V[t.C]).Length;
        return s / (3.0 * T.Count);
    }

    // Edge → incident triangle indices.
    public Dictionary<long, List<int>> EdgeTris()
    {
        var map = new Dictionary<long, List<int>>(LongKey.Comparer);
        for (int i = 0; i < T.Count; i++)
        {
            var t = T[i];
            Add(map, EdgeKey(t.A, t.B), i);
            Add(map, EdgeKey(t.B, t.C), i);
            Add(map, EdgeKey(t.C, t.A), i);
        }
        return map;
    }

    static void Add(Dictionary<long, List<int>> map, long k, int i)
    {
        List<int> l;
        if (!map.TryGetValue(k, out l)) { l = new List<int>(3); map[k] = l; }
        l.Add(i);
    }

    // Sorted set of region labels around each vertex.
    public List<int>[] VertexRegions()
    {
        var r = new List<int>[V.Count];
        for (int i = 0; i < V.Count; i++) r[i] = new List<int>(4);
        foreach (var t in T)
        {
            foreach (int v in new[] { t.A, t.B, t.C })
            {
                if (!r[v].Contains(t.Neg)) r[v].Add(t.Neg);
                if (!r[v].Contains(t.Pos)) r[v].Add(t.Pos);
            }
        }
        foreach (var l in r) l.Sort();
        return r;
    }

    // Drop degenerate / duplicate triangles and unused vertices.
    public void Clean()
    {
        var seen = new HashSet<long>(LongKey.Comparer);
        var keep = new List<FoamTri>(T.Count);
        foreach (var t in T)
        {
            if (t.A == t.B || t.B == t.C || t.C == t.A || t.Neg == t.Pos) continue;
            var s = new[] { t.A, t.B, t.C };
            Array.Sort(s);
            long key = ((long)s[0] << 42) | ((long)s[1] << 21) | (long)s[2];
            if (!seen.Add(key)) continue;
            keep.Add(t);
        }
        T = keep;
        var map = new int[V.Count];
        for (int i = 0; i < map.Length; i++) map[i] = -1;
        var nv = new List<Vec3>();
        for (int i = 0; i < T.Count; i++)
        {
            var t = T[i];
            t.A = Remap(map, nv, t.A); t.B = Remap(map, nv, t.B); t.C = Remap(map, nv, t.C);
            T[i] = t;
        }
        V = nv;
    }

    int Remap(int[] map, List<Vec3> nv, int i)
    {
        if (map[i] < 0) { map[i] = nv.Count; nv.Add(V[i]); }
        return map[i];
    }
}

// Edge/triangle keys pack vertex indices into a long. .NET's default long hash XORs the two
// halves, so nearby edges collide massively; this mixes the bits properly.
public sealed class LongKey : IEqualityComparer<long>
{
    public static readonly LongKey Comparer = new LongKey();
    public bool Equals(long a, long b) { return a == b; }
    public int GetHashCode(long k)
    {
        // unchecked: Rhino compiles scripts with overflow checking on; this hash relies on wrapping.
        unchecked
        {
            ulong x = (ulong)k;
            x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
            return (int)x;
        }
    }
}

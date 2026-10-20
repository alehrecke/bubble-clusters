using System;
using System.Collections.Generic;
using System.Linq;

// Every film a vertical sight line passes through (the foam bed is seen straight up, along +z), sorted from the
// plane upward: depth peeling for the "deep windows" style, where the walls behind the first one show through.
// Triangles are bucketed on an xy grid (world units).
public class FilmStack
{
    readonly FoamMesh m;
    readonly double cell, x0, y0;
    readonly int nx, ny;
    readonly List<int>[] buckets;
    readonly Vec3[] fn;                    // face normal per mesh triangle (unit)

    public struct Hit { public double Z; public Vec3 N; public int Tri; }

    public FilmStack(FoamMesh mesh, double cellSize)
    {
        m = mesh; cell = cellSize;
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        foreach (var v in m.V) { minx = Math.Min(minx, v.X); miny = Math.Min(miny, v.Y); maxx = Math.Max(maxx, v.X); maxy = Math.Max(maxy, v.Y); }
        x0 = minx; y0 = miny;
        nx = (int)Math.Ceiling((maxx - minx) / cell) + 1; ny = (int)Math.Ceiling((maxy - miny) / cell) + 1;
        buckets = new List<int>[nx * ny];
        fn = new Vec3[m.T.Count];
        for (int i = 0; i < m.T.Count; i++)
        {
            var t = m.T[i];
            if (FoamMesh.IsSolid(t) || (t.Neg <= 0 && t.Pos <= 0)) continue;
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            var n = Vec3.Cross(b - a, c - a);
            if (n.LengthSq < 1e-30) continue;
            fn[i] = n.Normalized();
            int i0 = (int)((Math.Min(a.X, Math.Min(b.X, c.X)) - x0) / cell), i1 = (int)((Math.Max(a.X, Math.Max(b.X, c.X)) - x0) / cell);
            int j0 = (int)((Math.Min(a.Y, Math.Min(b.Y, c.Y)) - y0) / cell), j1 = (int)((Math.Max(a.Y, Math.Max(b.Y, c.Y)) - y0) / cell);
            for (int j = j0; j <= j1; j++)
                for (int k = i0; k <= i1; k++)
                {
                    var l = buckets[j * nx + k];
                    if (l == null) buckets[j * nx + k] = l = new List<int>(8);
                    l.Add(i);
                }
        }
    }

    // All films above (x, y), lowest first.
    public List<Hit> Query(double x, double y)
    {
        var res = new List<Hit>();
        int i = (int)((x - x0) / cell), j = (int)((y - y0) / cell);
        if (i < 0 || j < 0 || i >= nx || j >= ny) return res;
        var l = buckets[j * nx + i];
        if (l == null) return res;
        foreach (int ti in l)
        {
            var t = m.T[ti];
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            // Barycentric in xy.
            double d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(d) < 1e-18) continue;
            double l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            double l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            double l3 = 1 - l1 - l2;
            if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;
            res.Add(new Hit { Z = l1 * a.Z + l2 * b.Z + l3 * c.Z, N = fn[ti], Tri = ti });
        }
        res.Sort((p, q) => p.Z.CompareTo(q.Z));
        // A sight line through a mesh edge hits both neighbouring triangles: merge hits closer than a hair.
        var merged = new List<Hit>(res.Count);
        foreach (var h in res) if (merged.Count == 0 || h.Z - merged[merged.Count - 1].Z > 1e-6) merged.Add(h);
        return merged;
    }
}

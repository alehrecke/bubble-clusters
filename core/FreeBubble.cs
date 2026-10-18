using System;
using System.Collections.Generic;

// A free bubble dropped into open space without a rebuild: an icosphere appended to the film mesh as
// its own piece (region k inside, outside air around it). A lone sphere is already at equilibrium, so it
// needs no relaxation; when it touches the foam, the normal contact rebuild joins it. Generators that
// spawn many bubbles (stream bed) would otherwise pay one rebuild per bubble.
public static class FreeBubble
{
    // Icosphere with edges about `edge` long, outward-facing, labelled (Neg = region, Pos = 0).
    public static void Append(FoamMesh m, int region, Vec3 center, double radius, double edge)
    {
        double t = (1 + Math.Sqrt(5)) / 2;
        var v = new List<Vec3>
        {
            new Vec3(-1, t, 0), new Vec3(1, t, 0), new Vec3(-1, -t, 0), new Vec3(1, -t, 0),
            new Vec3(0, -1, t), new Vec3(0, 1, t), new Vec3(0, -1, -t), new Vec3(0, 1, -t),
            new Vec3(t, 0, -1), new Vec3(t, 0, 1), new Vec3(-t, 0, -1), new Vec3(-t, 0, 1),
        };
        var f = new List<int[]>
        {
            new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 }, new[] { 0, 7, 10 }, new[] { 0, 10, 11 },
            new[] { 1, 5, 9 }, new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 }, new[] { 7, 1, 8 },
            new[] { 3, 9, 4 }, new[] { 3, 4, 2 }, new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
            new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 }, new[] { 8, 6, 7 }, new[] { 9, 8, 1 },
        };
        for (int i = 0; i < v.Count; i++) v[i] = v[i].Normalized();
        // Subdivide until edges are short enough (icosahedron edge = 1.0515 r).
        int levels = 0;
        double e0 = 1.0515 * radius;
        while (e0 / (1 << levels) > 1.15 * edge && levels < 6) levels++;
        for (int l = 0; l < levels; l++)
        {
            var mid = new Dictionary<long, int>();
            Func<int, int, int> midpoint = (a, b) =>
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                int idx;
                if (mid.TryGetValue(key, out idx)) return idx;
                idx = v.Count;
                v.Add(((v[a] + v[b]) * 0.5).Normalized());
                mid[key] = idx;
                return idx;
            };
            var nf = new List<int[]>(f.Count * 4);
            foreach (var tri in f)
            {
                int a = midpoint(tri[0], tri[1]), b = midpoint(tri[1], tri[2]), c = midpoint(tri[2], tri[0]);
                nf.Add(new[] { tri[0], a, c }); nf.Add(new[] { tri[1], b, a }); nf.Add(new[] { tri[2], c, b }); nf.Add(new[] { a, b, c });
            }
            f = nf;
        }
        // Orientation check: the normal must point out of the bubble (into the outside air, region 0).
        double vol = 0;
        foreach (var tri in f) vol += Vec3.Dot(v[tri[0]], Vec3.Cross(v[tri[1]], v[tri[2]]));
        bool flip = vol < 0;
        int baseIdx = m.V.Count;
        foreach (var p in v) m.V.Add(center + p * radius);
        foreach (var tri in f)
            m.T.Add(flip ? new FoamTri(baseIdx + tri[0], baseIdx + tri[2], baseIdx + tri[1], region, 0)
                         : new FoamTri(baseIdx + tri[0], baseIdx + tri[1], baseIdx + tri[2], region, 0));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

// What gets drawn: a set of surface triangles (with outward normals), a camera (orthographic), and a
// depth / triangle-id buffer on the sheet for hidden-line removal and surface lookups.
public class Scene
{
    public FoamMesh M;
    public List<int> Tris = new List<int>();        // surface triangles (indices into M.T)
    public List<bool> Flip = new List<bool>();      // outward normal = -(triangle normal)
    public List<int> Owner = new List<int>();       // bubble each surface triangle belongs to
    public List<long> Face = new List<long>();      // film id (region pair) per surface triangle
    public List<int> FaceIdx = new List<int>();     // the same as a small index (0..films-1)
    Vec3[] vn;                                      // per surface triangle corner: smooth outward normal (3 per tri)

    // Camera: forward F (into the scene), right R, up U. Sheet: x = Ox + S (p.R), y = Oy - S (p.U).
    public Vec3 F, R, U;
    public double S, Ox, Oy;
    public Vec3 Light;                              // unit vector toward the light

    // Buffers.
    public double Res;                              // pixels per mm
    public int W, H;
    float[] depth; int[] tri;
    V2[] proj; double[] projDepth;                  // per mesh vertex (sheet mm, depth)

    public static Scene Surface(FoamMesh m, int bubble)
    {
        // bubble > 0: that bubble's closed surface (all its films). bubble == 0: the cluster's outer skin
        // (the films facing the outside air), the cluster drawn as an opaque solid.
        var sc = new Scene { M = m };
        for (int i = 0; i < m.T.Count; i++)
        {
            var t = m.T[i];
            if (FoamMesh.IsSolid(t)) continue;
            int own;
            bool flip;
            if (bubble > 0)
            {
                if (t.Neg == bubble) { own = bubble; flip = false; }
                else if (t.Pos == bubble) { own = bubble; flip = true; }
                else continue;
            }
            else
            {
                if (t.Pos == 0 && t.Neg > 0) { own = t.Neg; flip = false; }
                else if (t.Neg == 0 && t.Pos > 0) { own = t.Pos; flip = true; }
                else continue;
            }
            sc.Tris.Add(i); sc.Flip.Add(flip); sc.Owner.Add(own);
            sc.Face.Add(FoamMesh.EdgeKey(t.Neg, t.Pos));
        }
        var idx = new Dictionary<long, int>();
        foreach (long f in sc.Face) { int i; if (!idx.TryGetValue(f, out i)) { i = idx.Count; idx[f] = i; } sc.FaceIdx.Add(i); }
        sc.SmoothNormals();
        return sc;
    }

    Vec3 TriNormal(int s)
    {
        var t = M.T[Tris[s]];
        var n = Vec3.Cross(M.V[t.B] - M.V[t.A], M.V[t.C] - M.V[t.A]);
        return Flip[s] ? n * -1 : n;
    }

    // Per film, area-weighted vertex normals: smooth within a film, sharp across Plateau borders.
    void SmoothNormals()
    {
        var acc = new Dictionary<long, Vec3>();
        for (int s = 0; s < Tris.Count; s++)
        {
            var t = M.T[Tris[s]]; var n = TriNormal(s);
            foreach (int v in new[] { t.A, t.B, t.C })
            {
                long key = Face[s] * 4194304L + v;
                Vec3 a; acc.TryGetValue(key, out a); acc[key] = a + n;
            }
        }
        vn = new Vec3[3 * Tris.Count];
        for (int s = 0; s < Tris.Count; s++)
        {
            var t = M.T[Tris[s]];
            vn[3 * s] = acc[Face[s] * 4194304L + t.A].Normalized();
            vn[3 * s + 1] = acc[Face[s] * 4194304L + t.B].Normalized();
            vn[3 * s + 2] = acc[Face[s] * 4194304L + t.C].Normalized();
        }
    }

    // Camera looking along `forward`, world up = Z; the scene's projection fitted into the box (mm).
    public void SetCamera(Vec3 forward, double x0, double y0, double w, double h, IEnumerable<Vec3> fitPoints = null)
    {
        F = forward.Normalized();
        R = Vec3.Cross(F, new Vec3(0, 0, 1)); if (R.LengthSq < 1e-9) R = new Vec3(1, 0, 0); R = R.Normalized();
        U = Vec3.Cross(R, F).Normalized();
        var pts = fitPoints ?? Tris.SelectMany(i => new[] { M.V[M.T[i].A], M.V[M.T[i].B], M.V[M.T[i].C] });
        double minx = double.MaxValue, maxx = double.MinValue, miny = double.MaxValue, maxy = double.MinValue;
        foreach (var p in pts)
        {
            double x = Vec3.Dot(p, R), y = Vec3.Dot(p, U);
            minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
        }
        S = Math.Min(w / Math.Max(maxx - minx, 1e-9), h / Math.Max(maxy - miny, 1e-9));
        Ox = x0 + w / 2 - S * (minx + maxx) / 2;
        Oy = y0 + h / 2 + S * (miny + maxy) / 2;
    }

    public V2 Project(Vec3 p) { return new V2(Ox + S * Vec3.Dot(p, R), Oy - S * Vec3.Dot(p, U)); }
    public double Depth(Vec3 p) { return Vec3.Dot(p, F); }

    public void Rasterize(double res, double sheetW, double sheetH)
    {
        Res = res; W = (int)Math.Ceiling(sheetW * res); H = (int)Math.Ceiling(sheetH * res);
        depth = new float[W * H]; tri = new int[W * H];
        for (int i = 0; i < depth.Length; i++) { depth[i] = float.MaxValue; tri[i] = -1; }
        proj = new V2[M.V.Count]; projDepth = new double[M.V.Count];
        for (int v = 0; v < M.V.Count; v++) { proj[v] = Project(M.V[v]); projDepth[v] = Depth(M.V[v]); }
        for (int s = 0; s < Tris.Count; s++)
        {
            var t = M.T[Tris[s]];
            V2 a = proj[t.A] * res, b = proj[t.B] * res, c = proj[t.C] * res;
            double da = projDepth[t.A], db = projDepth[t.B], dc = projDepth[t.C];
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            double den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(den) < 1e-12) continue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double l0 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / den;
                    double l1 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / den;
                    double l2 = 1 - l0 - l1;
                    if (l0 < -1e-6 || l1 < -1e-6 || l2 < -1e-6) continue;
                    double d = l0 * da + l1 * db + l2 * dc;
                    int i = y * W + x;
                    if (d < depth[i]) { depth[i] = (float)d; tri[i] = s; }
                }
        }
    }

    // Surface under a sheet point: surface triangle, position, smooth outward normal.
    public bool Query(V2 q, out int s, out Vec3 pos, out Vec3 n)
    {
        s = -1; pos = n = Vec3.Zero;
        int x = (int)(q.X * Res), y = (int)(q.Y * Res);
        if (x < 0 || y < 0 || x >= W || y >= H) return false;
        s = tri[y * W + x];
        if (s < 0) return false;
        var t = M.T[Tris[s]];
        V2 a = proj[t.A], b = proj[t.B], c = proj[t.C];
        double den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        double l0 = ((b.Y - c.Y) * (q.X - c.X) + (c.X - b.X) * (q.Y - c.Y)) / den;
        double l1 = ((c.Y - a.Y) * (q.X - c.X) + (a.X - c.X) * (q.Y - c.Y)) / den;
        double l2 = 1 - l0 - l1;
        pos = M.V[t.A] * l0 + M.V[t.B] * l1 + M.V[t.C] * l2;
        n = (vn[3 * s] * l0 + vn[3 * s + 1] * l1 + vn[3 * s + 2] * l2).Normalized();
        return true;
    }

    // Is the 3D point visible (not behind the buffered surface)? eps in world units.
    public bool Visible(Vec3 p, double eps)
    {
        V2 q = Project(p);
        int x = (int)(q.X * Res), y = (int)(q.Y * Res);
        if (x < 0 || y < 0 || x >= W || y >= H) return true;
        double d = Depth(p);
        // Lines lie on the surface: compare against the FARTHEST depth in a 3x3 neighbourhood. A line hidden
        // behind something has the occluder all around it; an outline (silhouette) always has background or
        // its own surface next to it. (The nearest depth hid every silhouette: just inside an outline the
        // surface is edge-on and much closer to the viewer than the outline itself.)
        float far = float.MinValue;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= W || yy >= H) return true;
                far = Math.Max(far, depth[yy * W + xx]);
            }
        return far == float.MaxValue || d <= far + eps;
    }

    public bool Covered(V2 q)
    {
        int x = (int)(q.X * Res), y = (int)(q.Y * Res);
        return x >= 0 && y >= 0 && x < W && y < H && tri[y * W + x] >= 0;
    }

    // Silhouette edges of the surface (front-facing next to back-facing, or an open edge), as 3D chains.
    public List<List<Vec3>> Silhouettes()
    {
        var edgeFaces = new Dictionary<long, List<int>>(LongKey.Comparer);
        for (int s = 0; s < Tris.Count; s++)
        {
            var t = M.T[Tris[s]];
            foreach (var e in new[] { FoamMesh.EdgeKey(t.A, t.B), FoamMesh.EdgeKey(t.B, t.C), FoamMesh.EdgeKey(t.C, t.A) })
            {
                List<int> l; if (!edgeFaces.TryGetValue(e, out l)) { l = new List<int>(2); edgeFaces[e] = l; }
                l.Add(s);
            }
        }
        var segs = new List<long>();
        foreach (var kv in edgeFaces)
        {
            bool front = false, back = false;
            foreach (int s in kv.Value) { if (Vec3.Dot(TriNormal(s), F) < 0) front = true; else back = true; }
            if (front && back) segs.Add(kv.Key);
        }
        return Chain(segs);
    }

    List<List<Vec3>> Chain(List<long> edges)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (long e in edges)
        {
            int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
            if (!adj.ContainsKey(a)) adj[a] = new List<int>(); if (!adj.ContainsKey(b)) adj[b] = new List<int>();
            adj[a].Add(b); adj[b].Add(a);
        }
        var used = new HashSet<long>(LongKey.Comparer);
        var res = new List<List<Vec3>>();
        foreach (var start in adj.Keys.OrderBy(k => adj[k].Count == 2 ? 1 : 0))
            foreach (int nb in adj[start])
            {
                if (used.Contains(FoamMesh.EdgeKey(start, nb))) continue;
                var ch = new List<int> { start };
                int prev = start, cur = nb;
                used.Add(FoamMesh.EdgeKey(start, nb));
                while (true)
                {
                    ch.Add(cur);
                    if (adj[cur].Count != 2) break;
                    int nx = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
                    if (used.Contains(FoamMesh.EdgeKey(cur, nx))) break;
                    used.Add(FoamMesh.EdgeKey(cur, nx)); prev = cur; cur = nx;
                }
                res.Add(ch.Select(v => M.V[v]).ToList());
            }
        return res;
    }
}

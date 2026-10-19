using System;
using System.Collections.Generic;
using System.Linq;

// Dramatic lighting for a scene: a low raking key light with cast shadows (soft, from a shadow map), and
// ambient occlusion (how much of the sky each point sees, from depth maps in many directions), so the
// Plateau-border creases and the gaps between bubbles fall into deep shade.
public class DepthMap
{
    readonly Vec3 F, R, U;
    readonly double x0, y0;
    public readonly double Cell;
    readonly int N;
    readonly float[] d;

    // Orthographic depth map looking along -towardLight (from the light into the scene).
    public DepthMap(FoamMesh m, IList<int> tris, Vec3 towardLight, int n)
    {
        N = n; F = towardLight.Normalized() * -1;
        var a = Math.Abs(F.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
        R = Vec3.Cross(F, a).Normalized(); U = Vec3.Cross(R, F).Normalized();
        double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
        foreach (int i in tris)
            foreach (int v in new[] { m.T[i].A, m.T[i].B, m.T[i].C })
            {
                double x = Vec3.Dot(m.V[v], R), y = Vec3.Dot(m.V[v], U);
                minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
            }
        Cell = Math.Max(maxx - minx, maxy - miny) / (N - 4);
        x0 = minx - 2 * Cell; y0 = miny - 2 * Cell;
        d = new float[N * N];
        for (int i = 0; i < d.Length; i++) d[i] = float.MaxValue;
        foreach (int i in tris)
        {
            var t = m.T[i];
            Vec3 pa = m.V[t.A], pb = m.V[t.B], pc = m.V[t.C];
            double ax = (Vec3.Dot(pa, R) - x0) / Cell, ay = (Vec3.Dot(pa, U) - y0) / Cell, az = Vec3.Dot(pa, F);
            double bx = (Vec3.Dot(pb, R) - x0) / Cell, by = (Vec3.Dot(pb, U) - y0) / Cell, bz = Vec3.Dot(pb, F);
            double cx = (Vec3.Dot(pc, R) - x0) / Cell, cy = (Vec3.Dot(pc, U) - y0) / Cell, cz = Vec3.Dot(pc, F);
            double den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Math.Abs(den) < 1e-12) continue;
            int i0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), i1 = Math.Min(N - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int j0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), j1 = Math.Min(N - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            for (int y = j0; y <= j1; y++)
                for (int x = i0; x <= i1; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double l0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                    double l1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                    double l2 = 1 - l0 - l1;
                    // A little conservative, so thin slivers between bubbles still cast.
                    if (l0 < -0.02 || l1 < -0.02 || l2 < -0.02) continue;
                    float z = (float)(l0 * az + l1 * bz + l2 * cz);
                    int k = y * N + x;
                    if (z < d[k]) d[k] = z;
                }
        }
    }

    // Fraction of a (2r+1)^2 neighbourhood in which p is lit (not behind the nearest surface + bias).
    public double Lit(Vec3 p, double bias, int r = 1)
    {
        double fx = (Vec3.Dot(p, R) - x0) / Cell, fy = (Vec3.Dot(p, U) - y0) / Cell, z = Vec3.Dot(p, F);
        int x = (int)fx, y = (int)fy, lit = 0, tot = 0;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int xx = x + dx, yy = y + dy; tot++;
                if (xx < 0 || yy < 0 || xx >= N || yy >= N) { lit++; continue; }
                if (z <= d[yy * N + xx] + bias) lit++;
            }
        return (double)lit / tot;
    }
}

public class LightSettings
{
    public double Key = 0.9;           // key light strength
    public double Ambient = 0.32;      // sky light (scaled by ambient occlusion)
    public double AoPower = 2.5;       // >1 deepens the creases
    public int AoDirections = 64;
    public int ShadowRes = 1400, AoRes = 200;
    public int Softness = 2;           // shadow edge softness (map cells)
}

public static class Lighting
{
    // Brightness 0 (black) .. 1 (paper) at a surface point; installed into the scene for the hatcher.
    public static void Setup(Scene sc, Vec3 towardLight, LightSettings ls)
    {
        sc.Light = towardLight.Normalized();
        var shadow = new DepthMap(sc.M, sc.Tris, sc.Light, ls.ShadowRes);

        // Ambient occlusion per surface-triangle corner, from depth maps over the whole sphere of directions
        // (each corner uses the ones on its side).
        int K = ls.AoDirections;
        var dirs = new Vec3[K]; var maps = new DepthMap[K];
        double golden = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < K; i++)
        {
            double y = 1 - 2 * (i + 0.5) / K, r = Math.Sqrt(1 - y * y), th = golden * i;
            dirs[i] = new Vec3(Math.Cos(th) * r, y, Math.Sin(th) * r);
            maps[i] = new DepthMap(sc.M, sc.Tris, dirs[i], ls.AoRes);
        }
        var ao = new double[3 * sc.Tris.Count];
        System.Threading.Tasks.Parallel.For(0, sc.Tris.Count, k =>
        {
            var t = sc.M.T[sc.Tris[k]];
            int[] vs = { t.A, t.B, t.C };
            for (int c = 0; c < 3; c++)
            {
                Vec3 n = sc.CornerNormal(3 * k + c), p = sc.M.V[vs[c]];
                double vis = 0, wsum = 0;
                for (int i = 0; i < K; i++)
                {
                    double w = Vec3.Dot(n, dirs[i]);
                    if (w <= 0) continue;
                    var map = maps[i];
                    vis += w * map.Lit(p + n * (1.5 * map.Cell), 2 * map.Cell, 0);
                    wsum += w;
                }
                ao[3 * k + c] = wsum > 0 ? vis / wsum : 1;
            }
        });

        sc.Lit = (s, q, pos, n) =>
        {
            double occ = Math.Pow(Math.Max(0, Math.Min(1, sc.InterpCorner(ao, s, q))), ls.AoPower);
            double dif = Vec3.Dot(n, sc.Light);
            double key = 0;
            if (dif > 0)
            {
                double bias = shadow.Cell * (2 + 3 * Math.Sqrt(1 - dif * dif) / Math.Max(0.15, dif));
                key = dif * shadow.Lit(pos + n * shadow.Cell, bias, ls.Softness);
            }
            return Math.Min(1, ls.Key * key + ls.Ambient * occ);
        };
    }
}

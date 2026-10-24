using System;
using System.Collections.Generic;
using System.Linq;

// Struts (2026-10-24, jewellery): the Plateau borders as round metal wire, checked against casting limits
// (Shapeways Silver 925: wire >= 0.8 mm supported, 0.3 mm clearance), and as one watertight solid for export.
//   Prepare  borders -> wire centrelines; near a bore (the finger) the centreline is pushed out so the wire keeps
//            at least 0.8 x its diameter of metal after the bore is cut flat (comfortable inner rails, round bore)
//   Check    short borders (< wire: become blobs), wires that pass within Clearance of each other without meeting,
//            films whose opening (2 x film point to its borders - wire) closes or gets tight
//   Solid    distance field (min over wire capsules and node spheres; the bore cut out) -> surface nets: one
//            closed quad mesh with rounded nodes, ready for a Shapeways upload
public class StrutWarning
{
    public string Kind;     // "short" | "clearance" | "closed" | "tight"
    public Vec3 At;
    public double Value;    // mm: border length, gap, or opening
}

public class StrutSolidResult
{
    public List<Vec3> V = new List<Vec3>();
    public List<int[]> Quads = new List<int[]>();
    public double Volume;   // mm^3
}

public static class Struts
{
    // Wire centrelines from cleaned borders; bore = the finger (null = none).
    public static List<List<Vec3>> Prepare(List<CleanBorder> borders, double wire, CylinderHost bore)
    {
        var res = new List<List<Vec3>>();
        foreach (var b in borders)
        {
            var pts = new List<Vec3>(b.Points);
            if (b.Closed && pts.Count > 0) pts.Add(pts[0]);
            if (bore != null)
            {
                double minH = 0.3 * wire;              // centre this far outside the bore: metal left = 0.8 x wire
                for (int i = 0; i < pts.Count; i++)
                {
                    double h = bore.Height(pts[i]);
                    if (h < minH) pts[i] = pts[i] + bore.Normal(pts[i]) * (minH - h);
                }
            }
            res.Add(pts);
        }
        return res;
    }

    public static List<Vec3> Nodes(List<CleanBorder> borders, List<List<Vec3>> wires)
    {
        var nodes = new List<Vec3>();
        for (int k = 0; k < borders.Count; k++)
        {
            if (borders[k].Closed) continue;
            foreach (var q in new[] { wires[k][0], wires[k][wires[k].Count - 1] })
                if (!nodes.Any(n => (n - q).LengthSq < 1e-8)) nodes.Add(q);
        }
        return nodes;
    }

    static double Len(List<Vec3> p) { double s = 0; for (int i = 1; i < p.Count; i++) s += (p[i] - p[i - 1]).Length; return s; }

    public static List<StrutWarning> Check(FoamMesh m, List<CleanBorder> borders, List<List<Vec3>> wires, List<Vec3> nodes,
                                           double wire, double clearance, double minOpening)
    {
        var w = new List<StrutWarning>();
        for (int k = 0; k < wires.Count; k++)
        {
            double L = Len(wires[k]);
            if (L < wire && !borders[k].Closed) w.Add(new StrutWarning { Kind = "short", At = wires[k][wires[k].Count / 2], Value = L });
        }
        // Clearance: points of different wires closer than wire + clearance, away from shared nodes (one warning per pair).
        double g = wire + clearance;
        var pts = new List<KeyValuePair<int, Vec3>>();
        for (int k = 0; k < wires.Count; k++) foreach (var p in Resample(wires[k], 0.25 * wire)) pts.Add(new KeyValuePair<int, Vec3>(k, p));
        var grid = new Dictionary<long, List<int>>(LongKey.Comparer);
        Func<Vec3, long> key = q => unchecked(((long)Math.Floor(q.X / g) * 73856093L) ^ ((long)Math.Floor(q.Y / g) * 19349663L) ^ ((long)Math.Floor(q.Z / g) * 83492791L));
        for (int i = 0; i < pts.Count; i++) { long kk = key(pts[i].Value); List<int> l; if (!grid.TryGetValue(kk, out l)) grid[kk] = l = new List<int>(); l.Add(i); }
        var nodeGrid = new Dictionary<long, List<Vec3>>(LongKey.Comparer);
        foreach (var n in nodes) { long kk = key(n); List<Vec3> l; if (!nodeGrid.TryGetValue(kk, out l)) nodeGrid[kk] = l = new List<Vec3>(); l.Add(n); }
        Func<Vec3, bool> nearNode = q =>
        {
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
            {
                List<Vec3> l; if (!nodeGrid.TryGetValue(key(q + new Vec3(dx * g, dy * g, dz * g)), out l)) continue;
                foreach (var n in l) if ((n - q).Length < 1.5 * wire + clearance) return true;
            }
            return false;
        };
        var worst = new Dictionary<long, StrutWarning>();
        for (int i = 0; i < pts.Count; i++)
        {
            var q = pts[i].Value;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
            {
                List<int> l; if (!grid.TryGetValue(key(q + new Vec3(dx * g, dy * g, dz * g)), out l)) continue;
                foreach (int j in l)
                {
                    if (pts[j].Key <= pts[i].Key) continue;
                    double gap = (pts[j].Value - q).Length - wire;
                    if (gap >= clearance || nearNode(q) || nearNode(pts[j].Value)) continue;
                    long pk = ((long)pts[i].Key << 32) | (uint)pts[j].Key;
                    StrutWarning prev;
                    if (!worst.TryGetValue(pk, out prev) || gap < prev.Value)
                        worst[pk] = new StrutWarning { Kind = "clearance", At = (q + pts[j].Value) * 0.5, Value = gap };
                }
            }
        }
        w.AddRange(worst.Values);
        // Openings: each film's point (nearest its area centroid) to the borders around it.
        var dual = FoamDual.Build(m);
        foreach (var e in dual.Edges)
        {
            var own = new List<int>();
            for (int k = 0; k < borders.Count; k++) if (borders[k].Bubbles.Contains(e.A) && borders[k].Bubbles.Contains(e.B)) own.Add(k);
            if (own.Count == 0) continue;
            double md = own.SelectMany(k => wires[k]).Min(p => (p - e.FilmPoint).Length);
            double open = 2 * md - wire;
            if (open < clearance) w.Add(new StrutWarning { Kind = "closed", At = e.FilmPoint, Value = open });
            else if (open < minOpening) w.Add(new StrutWarning { Kind = "tight", At = e.FilmPoint, Value = open });
        }
        return w;
    }

    static IEnumerable<Vec3> Resample(List<Vec3> p, double step)
    {
        for (int i = 0; i + 1 < p.Count; i++)
        {
            int n = Math.Max(1, (int)Math.Ceiling((p[i + 1] - p[i]).Length / step));
            for (int j = 0; j < n; j++) yield return p[i] + (p[i + 1] - p[i]) * ((double)j / n);
        }
        if (p.Count > 0) yield return p[p.Count - 1];
    }

    // One closed mesh: field = min(capsule distances - wire/2, node distances - nodeR), bore cut out; surface nets.
    public static StrutSolidResult Solid(List<List<Vec3>> wires, List<Vec3> nodes, double wire, double nodeScale, double voxel, CylinderHost bore)
    {
        double r = wire / 2, rn = r * Math.Max(1, nodeScale), h = voxel > 0 ? voxel : wire / 8;
        double pad = rn + 3 * h;
        var lo = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue); var hi = lo * -1;
        foreach (var l in wires) foreach (var p in l) { lo = Vec3.Min(lo, p); hi = Vec3.Max(hi, p); }
        lo = lo - new Vec3(pad, pad, pad); hi = hi + new Vec3(pad, pad, pad);
        int nx = (int)Math.Ceiling((hi.X - lo.X) / h) + 1, ny = (int)Math.Ceiling((hi.Y - lo.Y) / h) + 1, nz = (int)Math.Ceiling((hi.Z - lo.Z) / h) + 1;
        if ((long)nx * ny * nz > 60000000) throw new InvalidOperationException(string.Format("Solid: {0} x {1} x {2} voxels is too many; raise Voxel.", nx, ny, nz));
        var f = new float[nx * ny * nz];
        for (int i = 0; i < f.Length; i++) f[i] = 1e3f;
        Func<int, int, int, int> idx = (i, j, k) => (k * ny + j) * nx + i;
        Action<Vec3, Vec3, double> capsule = (a, b, rad) =>
        {
            var mn = Vec3.Min(a, b) - new Vec3(rad + 2 * h, rad + 2 * h, rad + 2 * h);
            var mx = Vec3.Max(a, b) + new Vec3(rad + 2 * h, rad + 2 * h, rad + 2 * h);
            int i0 = Math.Max(0, (int)((mn.X - lo.X) / h)), i1 = Math.Min(nx - 1, (int)((mx.X - lo.X) / h) + 1);
            int j0 = Math.Max(0, (int)((mn.Y - lo.Y) / h)), j1 = Math.Min(ny - 1, (int)((mx.Y - lo.Y) / h) + 1);
            int k0 = Math.Max(0, (int)((mn.Z - lo.Z) / h)), k1 = Math.Min(nz - 1, (int)((mx.Z - lo.Z) / h) + 1);
            var ab = b - a; double ab2 = Math.Max(1e-18, ab.LengthSq);
            for (int k = k0; k <= k1; k++)
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        var p = new Vec3(lo.X + i * h, lo.Y + j * h, lo.Z + k * h);
                        double t = Math.Max(0, Math.Min(1, Vec3.Dot(p - a, ab) / ab2));
                        double d = (p - (a + ab * t)).Length - rad;
                        int q = idx(i, j, k);
                        if (d < f[q]) f[q] = (float)d;
                    }
        };
        foreach (var l in wires) for (int s = 0; s + 1 < l.Count; s++) capsule(l[s], l[s + 1], r);
        foreach (var n in nodes) capsule(n, n, rn);
        if (bore != null)
            for (int k = 0; k < nz; k++) for (int j = 0; j < ny; j++) for (int i = 0; i < nx; i++)
            {
                var p = new Vec3(lo.X + i * h, lo.Y + j * h, lo.Z + k * h);
                double cut = -bore.Height(p);                // > 0 inside the finger
                int q = idx(i, j, k);
                if (cut > f[q]) f[q] = (float)cut;
            }

        // Surface nets: a vertex per cell the surface crosses (mean of its edge crossings), a quad per crossed edge.
        var res = new StrutSolidResult();
        var cellV = new Dictionary<int, int>();
        int[,] corner = { { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 }, { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 } };
        int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
        Func<int, int, int, int> vertexOf = (i, j, k) =>
        {
            int c = (k * (ny - 1) + j) * (nx - 1) + i;
            int vi; if (cellV.TryGetValue(c, out vi)) return vi;
            var sum = Vec3.Zero; int cnt = 0;
            for (int e = 0; e < 12; e++)
            {
                int a = edges[e, 0], b = edges[e, 1];
                double fa = f[idx(i + corner[a, 0], j + corner[a, 1], k + corner[a, 2])], fb = f[idx(i + corner[b, 0], j + corner[b, 1], k + corner[b, 2])];
                if ((fa < 0) == (fb < 0)) continue;
                double t = fa / (fa - fb);
                var pa = new Vec3(lo.X + (i + corner[a, 0]) * h, lo.Y + (j + corner[a, 1]) * h, lo.Z + (k + corner[a, 2]) * h);
                var pb = new Vec3(lo.X + (i + corner[b, 0]) * h, lo.Y + (j + corner[b, 1]) * h, lo.Z + (k + corner[b, 2]) * h);
                sum = sum + pa + (pb - pa) * t; cnt++;
            }
            vi = res.V.Count; res.V.Add(cnt > 0 ? sum / cnt : new Vec3(lo.X + (i + 0.5) * h, lo.Y + (j + 0.5) * h, lo.Z + (k + 0.5) * h));
            cellV[c] = vi; return vi;
        };
        for (int k = 1; k < nz - 1; k++)
            for (int j = 1; j < ny - 1; j++)
                for (int i = 1; i < nx - 1; i++)
                {
                    float f0 = f[idx(i, j, k)];
                    bool in0 = f0 < 0;
                    // +x edge: cells (i, j-1..j, k-1..k)
                    if (i + 1 < nx && in0 != (f[idx(i + 1, j, k)] < 0))
                        AddQuad(res, vertexOf(i, j - 1, k - 1), vertexOf(i, j, k - 1), vertexOf(i, j, k), vertexOf(i, j - 1, k), in0);
                    if (j + 1 < ny && in0 != (f[idx(i, j + 1, k)] < 0))
                        AddQuad(res, vertexOf(i - 1, j, k - 1), vertexOf(i - 1, j, k), vertexOf(i, j, k), vertexOf(i, j, k - 1), in0);
                    if (k + 1 < nz && in0 != (f[idx(i, j, k + 1)] < 0))
                        AddQuad(res, vertexOf(i - 1, j - 1, k), vertexOf(i, j - 1, k), vertexOf(i, j, k), vertexOf(i - 1, j, k), in0);
                }
        // Volume (divergence theorem over the quads split into triangles).
        double vol = 0;
        foreach (var q in res.Quads)
        {
            vol += Vec3.Dot(res.V[q[0]], Vec3.Cross(res.V[q[1]], res.V[q[2]])) / 6.0;
            vol += Vec3.Dot(res.V[q[0]], Vec3.Cross(res.V[q[2]], res.V[q[3]])) / 6.0;
        }
        res.Volume = Math.Abs(vol);
        return res;
    }

    static void AddQuad(StrutSolidResult r, int a, int b, int c, int d, bool flip)
    {
        r.Quads.Add(flip ? new[] { a, b, c, d } : new[] { d, c, b, a });
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

// Clean Plateau borders for fabrication (pipes / thickening). The raw borders are mesh-edge chains with
// a few degrees of vertex noise, more while the foam is still relaxing. For each border between two
// nodes (or a closed loop):
//   1. Taubin smoothing (lambda/mu, no shrinking) with the end nodes fixed, so the network stays connected:
//      every border that meets at a node ends on exactly the same point.
//   2. If the smoothed border is within tolerance of a circular arc through its two nodes, it becomes that
//      arc. Physically a border is where two of its films meet; where those films are near-spherical, that
//      intersection is a circle, so many borders are arcs.
//   3. Resampled evenly along its length.
public class BorderCleanSettings
{
    public int Iterations = 12;       // smoothing passes (0 = none)
    public double ArcTolerance = 0.5; // max deviation from the arc, x CellSize (0 = never use arcs)
    public double Spacing = 0;        // resampling step (0 = CellSize)
    public bool IncludeHost = true;   // include the outlines where bubbles sit on a host
}

public class CleanBorder
{
    public List<Vec3> Points = new List<Vec3>();
    public bool Closed, IsArc, OnHost;
    public Vec3 Center, Normal;       // arcs: centre and plane normal
    public double Radius;
    public int StartNode = -1, EndNode = -1;   // mesh vertex ids of the end nodes (open borders)
    public int[] Bubbles;             // regions meeting along it (0 = outside air)
    public double RawTurning, CleanTurning;    // mean turning angle per vertex (degrees), before / after
    public double ArcDeviation = double.NaN;   // max distance of the smoothed border from its arc
}

public static class BorderClean
{
    public static double MaxTurnDeg = 10;

    public static List<CleanBorder> Clean(FoamMesh m, double cellSize, BorderCleanSettings set)
    {
        var res = new List<CleanBorder>();
        var et = m.EdgeTris();
        List<int> nodes;
        var chains = FoamAnalysis.BorderChains(m, out nodes);
        double spacing = set.Spacing > 0 ? set.Spacing : cellSize;
        double tol = set.ArcTolerance * cellSize;
        foreach (var c in chains)
        {
            if (c.Verts.Count < 2) continue;
            var regs = new SortedSet<int>();
            List<int> tris;
            if (et.TryGetValue(FoamMesh.EdgeKey(c.Verts[0], c.Verts[1]), out tris))
                foreach (int ti in tris) { regs.Add(m.T[ti].Neg); regs.Add(m.T[ti].Pos); }
            bool onHost = regs.Contains(FoamMesh.Solid);
            if (onHost && !set.IncludeHost) continue;
            var cb = new CleanBorder { Closed = c.Closed, OnHost = onHost, Bubbles = regs.Where(r => r >= 0).ToArray() };
            var pts = c.Verts.Select(v => m.V[v]).ToList();
            if (c.Closed && pts.Count > 1 && (pts[0] - pts[pts.Count - 1]).Length < 1e-12) pts.RemoveAt(pts.Count - 1);
            if (!c.Closed) { cb.StartNode = c.Verts[0]; cb.EndNode = c.Verts[c.Verts.Count - 1]; }
            cb.RawTurning = MeanTurning(pts, c.Closed);
            var sm = Taubin(pts, c.Closed, set.Iterations);
            // Arc?
            if (tol > 0 && sm.Count >= 3)
            {
                Vec3 center, normal; double radius;
                if (FitArc(sm, c.Closed, out center, out normal, out radius))
                {
                    double dev = 0;
                    foreach (var p in sm) dev = Math.Max(dev, ArcDistance(p, center, normal, radius));
                    cb.ArcDeviation = dev;
                    if (dev <= tol)
                    {
                        cb.IsArc = true; cb.Center = center; cb.Normal = normal; cb.Radius = radius;
                        cb.Points = SampleArc(sm, c.Closed, center, normal, radius, spacing);
                    }
                }
            }
            if (!cb.IsArc) cb.Points = Resample(sm, c.Closed, spacing);
            cb.CleanTurning = MeanTurning(cb.Points, c.Closed);
            res.Add(cb);
        }
        return res;
    }

    static double MeanTurning(List<Vec3> p, bool closed)
    {
        int n = p.Count; if (n < 3) return 0;
        double sum = 0; int cnt = 0;
        for (int i = closed ? 0 : 1; i < (closed ? n : n - 1); i++)
        {
            Vec3 a = p[(i - 1 + n) % n], b = p[i], c = p[(i + 1) % n];
            Vec3 u = b - a, v = c - b;
            double l = u.Length * v.Length;
            if (l < 1e-20) continue;
            sum += Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(u, v) / l))) * 180 / Math.PI; cnt++;
        }
        return cnt > 0 ? sum / cnt : 0;
    }

    // Taubin lambda|mu smoothing: a shrink step then an inflate step, so the curve smooths without
    // contracting. End points fixed on open borders.
    static List<Vec3> Taubin(List<Vec3> p, bool closed, int iterations)
    {
        var cur = new List<Vec3>(p);
        int n = cur.Count;
        if (n < 3) return cur;
        foreach (double w in Enumerable.Repeat(new[] { 0.5, -0.53 }, iterations).SelectMany(x => x))
        {
            var nxt = new List<Vec3>(cur);
            for (int i = 0; i < n; i++)
            {
                if (!closed && (i == 0 || i == n - 1)) continue;
                Vec3 a = cur[(i - 1 + n) % n], c = cur[(i + 1) % n];
                nxt[i] = cur[i] + ((a + c) * 0.5 - cur[i]) * w;
            }
            cur = nxt;
        }
        return cur;
    }

    // Circle through the end points and the arc-length midpoint (open), or three points a third apart
    // (closed). Returns false for (near) straight borders.
    static bool FitArc(List<Vec3> p, bool closed, out Vec3 center, out Vec3 normal, out double radius)
    {
        center = normal = Vec3.Zero; radius = 0;
        var cum = Cumulative(p, closed);
        double L = cum[cum.Count - 1];
        Vec3 a, b, c;
        if (closed) { a = At(p, cum, closed, 0); b = At(p, cum, closed, L / 3); c = At(p, cum, closed, 2 * L / 3); }
        else { a = p[0]; b = At(p, cum, closed, L / 2); c = p[p.Count - 1]; }
        Vec3 ab = b - a, ac = c - a;
        Vec3 n = Vec3.Cross(ab, ac);
        double nn = n.LengthSq;
        if (nn < 1e-12 * ab.LengthSq * ac.LengthSq) return false;
        // Circumcentre of a, b, c.
        center = a + (Vec3.Cross(n, ab) * ac.LengthSq + Vec3.Cross(ac, n) * ab.LengthSq) / (2 * nn);
        normal = n / Math.Sqrt(nn);
        radius = (a - center).Length;
        return radius < 1e6;
    }

    // Distance from p to the circle (centre, normal, radius).
    static double ArcDistance(Vec3 p, Vec3 c, Vec3 n, double r)
    {
        Vec3 d = p - c;
        double h = Vec3.Dot(d, n);
        Vec3 inPlane = d - n * h;
        double rho = inPlane.Length;
        return Math.Sqrt(h * h + (rho - r) * (rho - r));
    }

    // The arc from the first to the last point, passing on the side of the border's own midpoint.
    static List<Vec3> SampleArc(List<Vec3> p, bool closed, Vec3 c, Vec3 n, double r, double spacing)
    {
        Vec3 u = (p[0] - c); u = (u - n * Vec3.Dot(u, n)).Normalized();
        Vec3 v = Vec3.Cross(n, u);
        Func<Vec3, double> angle = q => { Vec3 d = q - c; return Math.Atan2(Vec3.Dot(d, v), Vec3.Dot(d, u)); };
        double sweep;
        if (closed) sweep = 2 * Math.PI;
        else
        {
            double end = angle(p[p.Count - 1]), mid = angle(p[p.Count / 2]);
            if (end <= 0) end += 2 * Math.PI;
            if (mid <= 0) mid += 2 * Math.PI;
            sweep = mid < end ? end : end - 2 * Math.PI;   // go the way that passes the midpoint
        }
        // Enough segments for both the spacing and at most MaxTurnDeg of turning per segment (small arcs would
        // otherwise come out faceted for a mesh thickener).
        int segs = Math.Max(closed ? 8 : 2, (int)Math.Ceiling(Math.Max(Math.Abs(sweep) * r / spacing, Math.Abs(sweep) / (MaxTurnDeg * Math.PI / 180))));
        var res = new List<Vec3>(segs + 1);
        for (int i = 0; i < (closed ? segs : segs + 1); i++)
        {
            double t = sweep * i / segs;
            res.Add(c + u * (r * Math.Cos(t)) + v * (r * Math.Sin(t)));
        }
        if (!closed) { res[0] = p[0]; res[res.Count - 1] = p[p.Count - 1]; }   // nodes exactly
        return res;
    }

    static List<double> Cumulative(List<Vec3> p, bool closed)
    {
        var cum = new List<double> { 0 };
        int n = p.Count;
        for (int i = 1; i < (closed ? n + 1 : n); i++) cum.Add(cum[i - 1] + (p[i % n] - p[i - 1]).Length);
        return cum;
    }

    static Vec3 At(List<Vec3> p, List<double> cum, bool closed, double s)
    {
        int n = p.Count;
        for (int i = 1; i < cum.Count; i++)
            if (cum[i] >= s)
            {
                double t = (s - cum[i - 1]) / Math.Max(cum[i] - cum[i - 1], 1e-20);
                return p[i - 1] + (p[i % n] - p[i - 1]) * t;
            }
        return closed ? p[0] : p[n - 1];
    }

    static List<Vec3> Resample(List<Vec3> p, bool closed, double spacing)
    {
        var cum = Cumulative(p, closed);
        double L = cum[cum.Count - 1];
        int segs = Math.Max(closed ? 6 : 1, (int)Math.Round(L / spacing));
        var res = new List<Vec3>(segs + 1);
        for (int i = 0; i < (closed ? segs : segs + 1); i++) res.Add(At(p, cum, closed, L * i / segs));
        if (!closed) { res[0] = p[0]; res[res.Count - 1] = p[p.Count - 1]; }
        return res;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

// Ring seed (2026-10-24, jewellery): a single file of small bubbles around the underside of a finger, swelling into a
// small foam on top. Feeds the Bubble Solver with a CylinderHost of the bore radius, so the bubbles rest on the finger
// and the bore stays round. Frame: Origin = bore centre, Axis = along the finger, Up = toward the top of the ring.
// Units are the document's (mm for jewellery).
public class RingSeedSettings
{
    public double Size = 7;                // US ring size; bore diameter = 11.63 + 0.8128 x size (size 7: 17.32 mm)
    public double BandRadius = 0.9;        // bubbles on the underside
    public double TopRadius = 2.2;         // largest bubble on top
    public double Spread = 55;             // degrees either side of the top where the band swells
    public int TopCount = 10;              // extra bubbles collected on top
    public double Sink = 0.3;              // how far each bubble starts into the finger, x its radius (gives a footprint)
    public int Seed = 1;
    public Vec3 Origin = Vec3.Zero, Axis = new Vec3(0, 0, 1), Up = new Vec3(0, 1, 0);
}

public static class RingSeed
{
    public static double BoreDiameter(double usSize) { return 11.63 + 0.8128 * usSize; }

    public static void Build(RingSeedSettings s, out List<Vec3> centers, out List<double> radii)
    {
        var rng = new Random(s.Seed);
        var ax = s.Axis.Normalized();
        var up = (s.Up - ax * Vec3.Dot(s.Up, ax)).Normalized();
        var side = Vec3.Cross(ax, up);
        double Rb = BoreDiameter(s.Size) / 2;
        double spread = Math.Max(5, s.Spread) * Math.PI / 180;
        // Band radius at angle t from the top (0 = top, +-pi = underneath): swells toward the top, a little jitter.
        Func<double, double> rAt = t =>
        {
            double w = Math.Exp(-(t / spread) * (t / spread));
            return (s.BandRadius + (0.75 * s.TopRadius - s.BandRadius) * w) * (0.85 + 0.3 * rng.NextDouble());
        };
        Func<double, double, Vec3> onRing = (t, h) => s.Origin + (up * Math.Cos(t) + side * Math.Sin(t)) * h;

        // Walk once around: each next bubble one double-bubble spacing along the circle its centres sit on, then
        // stretch the steps evenly so the loop closes.
        var ts = new List<double>(); var rs = new List<double>();
        double tt = -Math.PI; double r0 = rAt(tt);
        while (tt < Math.PI)
        {
            ts.Add(tt); rs.Add(r0);
            double r1 = rAt(tt + 0.1);
            double h = Rb + (1 - s.Sink) * 0.5 * (r0 + r1);
            tt += ClusterPacker.PairDistance(r0, r1) / h;
            r0 = r1;
        }
        double scale = 2 * Math.PI / (tt + Math.PI);
        centers = new List<Vec3>(); radii = new List<double>();
        for (int i = 0; i < ts.Count; i++)
        {
            double t = -Math.PI + (ts[i] + Math.PI) * scale;
            centers.Add(onRing(t, Rb + (1 - s.Sink) * rs[i]));
            radii.Add(rs[i]);
        }
        int nBand = centers.Count;

        // The top foam: extra bubbles scattered above the top, packed against the band (band fixed) and gathered
        // toward a point above the finger, kept out of the finger.
        var attract = onRing(0, Rb + 1.2 * s.TopRadius);
        for (int k = 0; k < s.TopCount; k++)
        {
            double r = s.BandRadius + (s.TopRadius - s.BandRadius) * Math.Pow(rng.NextDouble(), 1.3);
            double t = (rng.NextDouble() * 2 - 1) * 0.7 * spread;
            double h = Rb + r + rng.NextDouble() * 2 * s.TopRadius;
            centers.Add(onRing(t, h) + ax * ((rng.NextDouble() * 2 - 1) * s.TopRadius));
            radii.Add(r);
        }
        int n = centers.Count;
        for (int it = 0; it < 800; it++)
        {
            for (int i = nBand; i < n; i++) centers[i] = centers[i] + (attract - centers[i]) * 0.02;
            for (int i = 0; i < n; i++)
                for (int j = Math.Max(i + 1, nBand); j < n; j++)
                {
                    var d = centers[j] - centers[i];
                    double l = d.Length, want = ClusterPacker.PairDistance(radii[i], radii[j]);
                    if (l >= want) continue;
                    if (l < 1e-9) { d = ax * 1e-3; l = 1e-3; }
                    var corr = d / l * (want - l);
                    if (i < nBand) centers[j] = centers[j] + corr;                    // band bubbles stay put
                    else { double wi = radii[j] / (radii[i] + radii[j]); centers[i] = centers[i] - corr * wi; centers[j] = centers[j] + corr * (1 - wi); }
                }
            for (int i = nBand; i < n; i++)
            {
                var p = centers[i] - s.Origin; var perp = p - ax * Vec3.Dot(p, ax);
                double minH = Rb + (1 - s.Sink) * radii[i];
                if (perp.Length < minH) centers[i] = centers[i] + perp.Normalized() * (minH - perp.Length);
            }
        }
    }
}

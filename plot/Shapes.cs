using System;
using System.Collections.Generic;
using System.Linq;

// Cluster silhouettes beyond the round ball (Alex, 2026-10-20: "try the shape variety"). ClusterPacker gathers
// bubbles toward the centroid, which always gives a ball. Here each bubble is gathered toward its own target
// instead (the nearest point of a guide shape), with the same double-bubble spacing push-apart, so the mesher
// starts from a chain, ring, raft, branching or budding arrangement and the solver settles it from there.
//   chain   bubbles along a smooth random 3D curve, about 1-2 bubbles thick
//   ring    the same along a closed loop (a wobbly circle)
//   raft    a flat sheet: gathered into a plane (one layer, like a bubble raft on water)
//   branch  three curved arms from a common centre
//   buds    a main ball with two or three clumps of smaller bubbles budding off its side
public static partial class Edition
{
    static void ShapePack(string shape, List<Vec3> c, List<double> r, Random rng)
    {
        int n = r.Count;
        double len = r.Sum(x => 2 * x) * 0.62;                 // guide-curve length for chain-like shapes
        var guide = new List<List<Vec3>>();                     // polylines the bubbles gather onto
        Func<int, Vec3, Vec3> target;
        var rot = RandomFrame(rng);

        if (shape == "chain" || shape == "ring")
        {
            var g = new List<Vec3>();
            double f1 = 0.6 + rng.NextDouble(), f2 = 0.5 + rng.NextDouble(), p1 = rng.NextDouble() * 6.3, p2 = rng.NextDouble() * 6.3;
            for (int k = 0; k <= 240; k++)
            {
                double t = k / 240.0;
                Vec3 q = shape == "chain"
                    ? new Vec3((t - 0.5) * len, 0.16 * len * Math.Sin(2 * Math.PI * f1 * t + p1), 0.12 * len * Math.Sin(2 * Math.PI * f2 * t + p2))
                    : new Vec3(len / (2 * Math.PI) * Math.Cos(2 * Math.PI * t) * (1 + 0.15 * Math.Sin(4 * Math.PI * t + p1)),
                               len / (2 * Math.PI) * Math.Sin(2 * Math.PI * t) * (1 + 0.15 * Math.Sin(4 * Math.PI * t + p1)),
                               0.08 * len * Math.Sin(2 * Math.PI * 3 * t + p2));
                g.Add(rot(q));
            }
            guide.Add(g);
            for (int i = 0; i < n; i++) c.Add(g[rng.Next(g.Count)] + Jitter(rng, r[i]));
            target = (i, q) => Nearest(guide, q);
        }
        else if (shape == "branch")
        {
            double armLen = len / 3;
            var a0 = rng.NextDouble() * 2 * Math.PI;
            for (int k = 0; k < 3; k++)
            {
                double a = a0 + k * 2 * Math.PI / 3 + (rng.NextDouble() - 0.5) * 0.6, bend = (rng.NextDouble() - 0.5) * 1.2, lift = (rng.NextDouble() - 0.5) * 0.9;
                var g = new List<Vec3>();
                for (int s = 0; s <= 80; s++)
                {
                    double t = s / 80.0, aa = a + bend * t;
                    g.Add(rot(new Vec3(Math.Cos(aa) * t * armLen, Math.Sin(aa) * t * armLen, lift * t * armLen)));
                }
                guide.Add(g);
            }
            for (int i = 0; i < n; i++) { var g = guide[i % 3]; c.Add(g[rng.Next(g.Count)] + Jitter(rng, r[i])); }
            target = (i, q) => Nearest(guide, q);
        }
        else if (shape == "raft")
        {
            double rd = Math.Sqrt(r.Sum(x => x * x) * 1.15);
            for (int i = 0; i < n; i++)
            {
                double a = rng.NextDouble() * 2 * Math.PI, d = rd * Math.Sqrt(rng.NextDouble());
                c.Add(rot(new Vec3(d * Math.Cos(a), d * Math.Sin(a), 0)));
            }
            var nz = rot(new Vec3(0, 0, 1));
            // Into the plane strongly, toward the middle gently (an elliptical sheet, not a disc: x squeezed less).
            target = (i, q) => { var h = nz * Vec3.Dot(q, nz); var inPlane = q - h; return inPlane * 0.985; };
        }
        else // buds
        {
            int nMain = Math.Max(4, (int)Math.Round(n * 0.68));
            // Buds are smaller than the main body's bubbles.
            for (int i = nMain; i < n; i++) r[i] *= 0.5;
            double Rm = 0.9 * Math.Pow(nMain, 1.0 / 3) * r.Take(nMain).Average();
            int clumps = 2 + rng.Next(2);
            var dirs = Enumerable.Range(0, clumps).Select(k => RandomUnit(rng)).ToList();
            var anchor = new Vec3[n];
            for (int i = 0; i < n; i++)
            {
                if (i < nMain)
                {
                    Vec3 p; do p = new Vec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1); while (p.LengthSq > 1);
                    c.Add(p * Rm); anchor[i] = Vec3.Zero;
                }
                else
                {
                    var d = dirs[(i - nMain) % clumps];
                    anchor[i] = d * (Rm * 1.25);
                    c.Add(anchor[i] + Jitter(rng, r[i]));
                }
            }
            target = (i, q) => anchor[i];
        }

        // Gather toward the targets + push apart to the double-bubble spacing (as ClusterPacker).
        for (int it = 0; it < 1500; it++)
        {
            for (int i = 0; i < n; i++) c[i] = c[i] + (target(i, c[i]) - c[i]) * 0.03;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    Vec3 d = c[j] - c[i];
                    double l = d.Length, want = ClusterPacker.PairDistance(r[i], r[j]);
                    if (l >= want) continue;
                    if (l < 1e-9) { d = new Vec3(1e-3, 0, 0); l = 1e-3; }
                    Vec3 corr = d / l * (0.5 * (l - want));
                    double wi = r[j] / (r[i] + r[j]);
                    c[i] = c[i] + corr * wi;
                    c[j] = c[j] - corr * (1 - wi);
                }
        }
        // Centre on the origin.
        var mid = c.Aggregate(Vec3.Zero, (a, b) => a + b) / n;
        for (int i = 0; i < n; i++) c[i] = c[i] - mid;
    }

    static Vec3 Nearest(List<List<Vec3>> guide, Vec3 q)
    {
        Vec3 best = q; double bd = double.MaxValue;
        foreach (var g in guide)
            for (int k = 0; k + 1 < g.Count; k++)
            {
                Vec3 a = g[k], ab = g[k + 1] - a;
                double t = Math.Max(0, Math.Min(1, Vec3.Dot(q - a, ab) / Math.Max(1e-12, ab.LengthSq)));
                var p = a + ab * t;
                double d = (p - q).LengthSq;
                if (d < bd) { bd = d; best = p; }
            }
        return best;
    }

    static Vec3 Jitter(Random rng, double r) { return RandomUnit(rng) * (r * 0.6 * rng.NextDouble()); }

    static Vec3 RandomUnit(Random rng)
    {
        Vec3 p; do p = new Vec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1); while (p.LengthSq > 1 || p.LengthSq < 1e-4);
        return p.Normalized();
    }

    // A random rotation (as a function), so shapes don't all lie along the same axes relative to the camera.
    static Func<Vec3, Vec3> RandomFrame(Random rng)
    {
        var x = RandomUnit(rng);
        var t = Math.Abs(x.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
        var y = Vec3.Cross(t, x).Normalized();
        var z = Vec3.Cross(x, y);
        return q => x * q.X + y * q.Y + z * q.Z;
    }
}

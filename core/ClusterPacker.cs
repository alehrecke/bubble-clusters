using System;
using System.Collections.Generic;

// Fast particle pre-pass: gathers bubble centres into a cluster where no pair is closer than
// the double-bubble spacing d = sqrt(rA^2 + rB^2 - rA rB) (spheres meeting at 60 degrees, so
// the films meet at 120). The Möbius mesher then starts close to equilibrium.
// This is also the layer interactive dragging and seeding will drive.
public static class ClusterPacker
{
    public static double PairDistance(double ra, double rb) { return Math.Sqrt(ra * ra + rb * rb - ra * rb); }

    // Soft-sphere packing: pairs closer than the double-bubble spacing are pushed apart
    // (Gauss-Seidel, halfway per pass, smaller bubble moves more); a small cohesion step
    // toward the centroid (or attractor) gathers the cluster. Pairs are never pulled together
    // directly, so bubbles that are blocked by a third one don't get crushed into it.
    // Returns the largest relative compression among pairs (0 = nothing too close).
    public static double Pack(List<Vec3> c, IList<double> r, int iterations, double cohesion, Vec3? attractor = null)
    {
        int n = c.Count;
        double worst = 0;
        for (int it = 0; it < iterations; it++)
        {
            Vec3 centre = Vec3.Zero;
            for (int i = 0; i < n; i++) centre = centre + c[i];
            centre = attractor ?? centre / Math.Max(n, 1);
            for (int i = 0; i < n; i++) c[i] = c[i] + (centre - c[i]) * cohesion;
            worst = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    Vec3 d = c[j] - c[i];
                    double len = d.Length;
                    double target = PairDistance(r[i], r[j]);
                    if (len >= target) continue; // only push apart; cohesion does the gathering
                    if (len < 1e-9) { d = new Vec3(1e-3, 0, 0); len = 1e-3; }
                    double err = (target - len) / target;
                    if (err > worst) worst = err;
                    Vec3 corr = d / len * (0.5 * (len - target));
                    double wi = r[j] / (r[i] + r[j]); // smaller bubbles move more
                    c[i] = c[i] + corr * wi;
                    c[j] = c[j] - corr * (1 - wi);
                }
        }
        return worst;
    }
}

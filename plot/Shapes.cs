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
        double len = r.Sum(x => 2 * x) * (shape == "ring" ? 0.8 : 0.62);   // guide-curve length for chain-like shapes
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
            // Round 2 (2026-10-24): round-1 buds were too small and close to read as anything but a ball; now bigger
            // (0.7 x), in 1-2 clumps, set further out so each reads as its own lobe.
            // Round 3: a few clearly smaller buds (0.55 x) on a stalk, so the solver can't sink them into the hollows
            // between the big bubbles; ShapeView then looks at it side-on so the lobes stick out of the silhouette.
            int nMain = Math.Max(4, n - Math.Max(3, Math.Min(7, (int)Math.Round(n * 0.3))));
            for (int i = nMain; i < n; i++) r[i] *= 0.55;
            double Rm = 0.9 * Math.Pow(nMain, 1.0 / 3) * r.Take(nMain).Average();
            int clumps = 1 + rng.Next(2);
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
                    anchor[i] = d * (Rm + 2.4 * r.Skip(nMain).Average());
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
        // Round 2: keep it in one piece. Round-1 chains, rings and branches often came apart (gaps along the guide:
        // nothing pulled bubbles along it), leaving stray bubbles on the sheet. Overlapping spheres (d < ra + rb)
        // become films in the mesher; any piece not touching the largest one is moved against it, then repacked.
        for (int rep = 0; rep < 30; rep++)
        {
            var comp = Components(c, r);
            int nComp = comp.Max() + 1;
            if (nComp == 1) break;
            int main = Enumerable.Range(0, nComp).OrderByDescending(k => comp.Count(x => x == k)).First();
            for (int k = 0; k < nComp; k++)
            {
                if (k == main) continue;
                int bi = -1, bj = -1; double bd = double.MaxValue;
                for (int i = 0; i < n; i++)
                    if (comp[i] == k)
                        for (int j = 0; j < n; j++)
                            if (comp[j] == main) { double d = (c[i] - c[j]).Length - r[i] - r[j]; if (d < bd) { bd = d; bi = i; bj = j; } }
                var dir = (c[bi] - c[bj]).Normalized();
                var shift = c[bj] + dir * (0.95 * ClusterPacker.PairDistance(r[bi], r[bj])) - c[bi];
                for (int i = 0; i < n; i++) if (comp[i] == k) c[i] = c[i] + shift;
            }
            for (int it = 0; it < 200; it++)
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                    {
                        Vec3 d = c[j] - c[i];
                        double l = d.Length, want = ClusterPacker.PairDistance(r[i], r[j]);
                        if (l >= want) continue;
                        if (l < 1e-9) { d = new Vec3(1e-3, 0, 0); l = 1e-3; }
                        Vec3 corr = d / l * (0.5 * (l - want));
                        double wi = r[j] / (r[i] + r[j]);
                        c[i] = c[i] + corr * wi; c[j] = c[j] - corr * (1 - wi);
                    }
        }
        // Centre on the origin.
        var mid = c.Aggregate(Vec3.Zero, (a, b) => a + b) / n;
        for (int i = 0; i < n; i++) c[i] = c[i] - mid;
    }

    // Free-floating extras (Alex, 2026-10-24: "I kind of liked the free floating bubbles ... a mix of smaller and
    // larger clusters"). Each satellite is a single bubble or a small cluster of 2-6 (packed at the double-bubble
    // spacing), smaller than the main bubbles, set clear of the cluster at 0.4-2.5 mean radii, never touching it
    // or each other, so the solver keeps them as separate pieces.
    static void AddSatellites(List<Vec3> c, List<double> r, int count, Random rng)
    {
        double rMean = r.Average();
        var mid = c.Aggregate(Vec3.Zero, (a, b) => a + b) / c.Count;
        double reach = c.Select((p, i) => (p - mid).Length + r[i]).Max();
        int nMain = c.Count;
        for (int sIdx = 0; sIdx < count; sIdx++)
        {
            double u = rng.NextDouble();
            int size = u < 0.45 ? 1 : u < 0.7 ? 2 : u < 0.85 ? 3 : 4 + rng.Next(3);
            var gr = new List<double>(); var gc = new List<Vec3>();
            for (int k = 0; k < size; k++) gr.Add(rMean * (0.3 + 0.55 * Math.Pow(rng.NextDouble(), 1.5)));
            for (int k = 0; k < size; k++) gc.Add(RandomUnit(rng) * (0.5 * gr.Average() * rng.NextDouble()));
            if (size > 1) ClusterPacker.Pack(gc, gr, 400, 0.02);
            double gReach = gc.Select((p, k) => p.Length + gr[k]).Max();
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var dir = RandomUnit(rng);
                var at = mid + dir * (reach + gReach + rMean * (0.4 + 2.1 * rng.NextDouble()));
                bool clear = true;
                for (int k = 0; k < size && clear; k++)
                    for (int i = 0; i < c.Count && clear; i++)
                        if ((c[i] - (at + gc[k])).Length < (r[i] + gr[k]) * (i < nMain ? 1.25 : 1.15)) clear = false;
                if (!clear) continue;
                for (int k = 0; k < size; k++) { c.Add(at + gc[k]); r.Add(gr[k]); }
                break;
            }
        }
    }

    // Connected pieces: bubbles whose spheres overlap (they will share a film).
    static int[] Components(List<Vec3> c, List<double> r)
    {
        int n = c.Count; var comp = Enumerable.Repeat(-1, n).ToArray(); int k = 0;
        for (int s = 0; s < n; s++)
        {
            if (comp[s] >= 0) continue;
            var st = new Stack<int>(); st.Push(s); comp[s] = k;
            while (st.Count > 0)
            {
                int i = st.Pop();
                for (int j = 0; j < n; j++)
                    if (comp[j] < 0 && (c[i] - c[j]).Length < 0.98 * (r[i] + r[j])) { comp[j] = k; st.Push(j); }
            }
            k++;
        }
        return comp;
    }

    // A view that shows a shaped cluster's form: chains and branches side-on (perpendicular to their long axis),
    // rings from near their axis so the hole shows, others from the seed. Principal axes from the mesh vertices.
    public static View ShapeView(FoamSolver s, string shape, int seed)
    {
        if (shape == "ball" || shape == "raft") return MakeView(seed);
        var V = s.Mesh.V;
        var m = V.Aggregate(Vec3.Zero, (a, b) => a + b) / V.Count;
        double[,] C = new double[3, 3];
        foreach (var p in V)
        {
            var d = p - m; double[] x = { d.X, d.Y, d.Z };
            for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) C[a, b] += x[a] * x[b];
        }
        Func<double[], double[]> mul = x => new[] { C[0, 0] * x[0] + C[0, 1] * x[1] + C[0, 2] * x[2], C[1, 0] * x[0] + C[1, 1] * x[1] + C[1, 2] * x[2], C[2, 0] * x[0] + C[2, 1] * x[1] + C[2, 2] * x[2] };
        double[] e1 = { 1, 0.3, 0.2 };
        for (int it = 0; it < 60; it++) { e1 = mul(e1); double l = Math.Sqrt(e1.Sum(z => z * z)); for (int a = 0; a < 3; a++) e1[a] /= l; }
        var axis = new Vec3(e1[0], e1[1], e1[2]);
        var rng = new Random(seed * 17 + 3);
        double el = 15 + 20 * rng.NextDouble();
        Vec3 look;
        if (shape == "ring")
        {
            // Smallest axis = ring normal: deflate e1 and its partner, take the cross product of the two largest.
            double lam = e1.Zip(mul(e1), (a, b) => a * b).Sum();
            double[] e2 = { -e1[1], e1[0], 0.3 };
            for (int it = 0; it < 60; it++)
            {
                var y = mul(e2); for (int a = 0; a < 3; a++) y[a] -= lam * e1[a] * e1.Zip(e2, (u, w) => u * w).Sum();
                double dp = y.Zip(e1, (u, w) => u * w).Sum(); for (int a = 0; a < 3; a++) y[a] -= dp * e1[a];
                double l = Math.Sqrt(y.Sum(z => z * z)); for (int a = 0; a < 3; a++) e2[a] = y[a] / l;
            }
            look = Vec3.Cross(axis, new Vec3(e2[0], e2[1], e2[2])).Normalized();
            if (look.Z > 0) look = look * -1;
            // Tilt off the exact axis a little so it reads as a ring in 3D, and keep the elevation sensible.
            el = Math.Min(70, Math.Max(25, Math.Asin(Math.Min(1, -look.Z)) * 180 / Math.PI));
            double azr = Math.Atan2(-look.Y, -look.X) * 180 / Math.PI + 15;
            return MakeView(seed, azr, el);
        }
        var h = new Vec3(-axis.Y, axis.X, 0);
        if (h.LengthSq < 1e-6) return MakeView(seed);
        h = h.Normalized();
        if (rng.NextDouble() < 0.5) h = h * -1;
        double az = Math.Atan2(-h.Y, -h.X) * 180 / Math.PI;
        return MakeView(seed, az, el);
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

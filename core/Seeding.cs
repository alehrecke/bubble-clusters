using System;
using System.Collections.Generic;
using System.Linq;

// Starting arrangements: crystal lattices, size fields, and graded random scatter. These produce centres + radii for the
// solver (Bubble Solver's Centers / Radii); the solver relaxes them into the matching foam.
//
// Lattices (Spacing = nearest-neighbour distance between centres; every site gets the same radius, just large
// enough to fill its Voronoi cell, so the foam starts as the lattice's Voronoi cells):
//   SC     simple cubic: cubes. Not a stable foam (4 films meet at every edge), so it rearranges.
//   BCC    body-centred cubic: Kelvin's truncated octahedra (14 faces: 6 squares, 8 hexagons).
//   FCC    face-centred cubic: rhombic dodecahedra (12 faces). Has 4-fold vertices, so it rearranges.
//   HCP    hexagonal close-packed: trapezo-rhombic dodecahedra. Also 4-fold vertices.
//   WP     Weaire-Phelan (A15 sites): 2 pentagonal dodecahedra + 6 tetrakaidecahedra per cell, all equal volume.
//          Lower area than Kelvin; the best known equal-volume foam.
//   Hex    a single honeycomb layer (a raft on a host plane, or a free sheet).
public enum LatticeKind { SC, BCC, FCC, HCP, WP, Hex }

public class LatticeSettings
{
    public LatticeKind Kind = LatticeKind.BCC;
    public double Spacing = 2;              // nearest-neighbour centre distance
    public Vec3 Origin = Vec3.Zero;         // lattice frame (sites are generated in this frame)
    public Vec3 X = new Vec3(1, 0, 0), Y = new Vec3(0, 1, 0), Z = new Vec3(0, 0, 1);
    // Region, in world coordinates. Inside == null: a ball of BallRadius around Origin (BallRadius <= 0:
    // a box of Count cells along each axis from Origin).
    public Func<Vec3, bool> Inside;
    public Vec3 BoundsMin, BoundsMax;       // bounding box of Inside (required with Inside)
    public double BallRadius = 0;
    public int Count = 3;
    public double Jitter = 0;               // random offset, as a fraction of Spacing
    public int Seed = 1;
}

public static class Seeding
{
    // Sites per unit cell (cubic cell edge a, in units of a) and the nearest-neighbour distance in units of a.
    static void Basis(LatticeKind k, out List<Vec3> basis, out double nn, out Vec3 cell)
    {
        basis = new List<Vec3>(); cell = new Vec3(1, 1, 1);
        switch (k)
        {
            case LatticeKind.SC: basis.Add(Vec3.Zero); nn = 1; break;
            case LatticeKind.BCC: basis.Add(Vec3.Zero); basis.Add(new Vec3(0.5, 0.5, 0.5)); nn = Math.Sqrt(3) / 2; break;
            case LatticeKind.FCC:
                basis.Add(Vec3.Zero); basis.Add(new Vec3(0.5, 0.5, 0)); basis.Add(new Vec3(0.5, 0, 0.5)); basis.Add(new Vec3(0, 0.5, 0.5));
                nn = Math.Sqrt(0.5); break;
            case LatticeKind.WP:
                basis.Add(Vec3.Zero); basis.Add(new Vec3(0.5, 0.5, 0.5));
                basis.Add(new Vec3(0.25, 0, 0.5)); basis.Add(new Vec3(0.75, 0, 0.5));
                basis.Add(new Vec3(0.5, 0.25, 0)); basis.Add(new Vec3(0.5, 0.75, 0));
                basis.Add(new Vec3(0, 0.5, 0.25)); basis.Add(new Vec3(0, 0.5, 0.75));
                nn = 0.5; break;
            case LatticeKind.HCP:
                // Orthorhombic cell: a x a*sqrt3 x 2c with c = a*sqrt(2/3) (layer spacing), 4 sites.
                double h = Math.Sqrt(2.0 / 3.0), s3 = Math.Sqrt(3);
                cell = new Vec3(1, s3, 2 * h);
                basis.Add(Vec3.Zero); basis.Add(new Vec3(0.5, s3 / 2, 0));
                basis.Add(new Vec3(0.5, s3 / 6, h)); basis.Add(new Vec3(1.0, s3 / 6 + s3 / 2, h));
                nn = 1; break;
            default: // Hex: one layer, cell a x a*sqrt3, 2 sites
                cell = new Vec3(1, Math.Sqrt(3), 1);
                basis.Add(Vec3.Zero); basis.Add(new Vec3(0.5, Math.Sqrt(3) / 2, 0));
                nn = 1; break;
        }
    }

    // Volume per site / nn^3, so equal-volume radius = (3 V / 4 pi)^(1/3).
    public static double SiteVolume(LatticeKind k, double spacing)
    {
        List<Vec3> b; double nn; Vec3 cell; Basis(k, out b, out nn, out cell);
        double a = spacing / nn;
        if (k == LatticeKind.Hex) return 0.866 * spacing * spacing * spacing;   // honeycomb prism about as tall as wide
        return cell.X * cell.Y * cell.Z * a * a * a / b.Count;
    }

    public static double EqualVolumeRadius(LatticeKind k, double spacing)
    {
        return Math.Pow(3 * SiteVolume(k, spacing) / (4 * Math.PI), 1.0 / 3.0);
    }

    // Radius that covers every site's whole Voronoi cell (the farthest point of the cell from its site), found by
    // sampling. Seeding at the equal-volume radius instead leaves the cell corners uncovered: those pockets are
    // outside air inside the cluster, and no interior bubble forms (first BCC test: 0 interior bubbles).
    public static double CoveringRadius(LatticeKind k, double spacing)
    {
        List<Vec3> basis; double nn; Vec3 cell;
        Basis(k, out basis, out nn, out cell);
        double a = spacing / nn;
        var sites = new List<Vec3>();
        int nz = k == LatticeKind.Hex ? 0 : 1;
        for (int i = -1; i <= 2; i++) for (int j = -1; j <= 2; j++) for (int l = -nz; l <= nz + (nz > 0 ? 1 : 0); l++)
            foreach (var b in basis) sites.Add(new Vec3((i * cell.X + b.X) * a, (j * cell.Y + b.Y) * a, (l * cell.Z + b.Z) * a));
        var rng = new Random(7);
        double worst = 0;
        for (int t = 0; t < 20000; t++)
        {
            var p = new Vec3(rng.NextDouble() * cell.X * a, rng.NextDouble() * cell.Y * a, k == LatticeKind.Hex ? 0 : rng.NextDouble() * cell.Z * a);
            double best = double.MaxValue;
            foreach (var q in sites) best = Math.Min(best, (p - q).LengthSq);
            worst = Math.Max(worst, best);
        }
        return Math.Sqrt(worst);
    }

    public static void Lattice(LatticeSettings set, out List<Vec3> centres, out List<double> radii)
    {
        List<Vec3> basis; double nn; Vec3 cell;
        Basis(set.Kind, out basis, out nn, out cell);
        double a = set.Spacing / nn;
        Vec3 X = set.X.Normalized(), Y = set.Y.Normalized(), Z = set.Z.Normalized();
        Func<Vec3, Vec3> world = q => set.Origin + X * q.X + Y * q.Y + Z * q.Z;
        Func<Vec3, bool> inside; double ext;
        if (set.Inside != null)
        {
            inside = set.Inside;
            ext = 0;
            foreach (var cnr in new[] { set.BoundsMin, set.BoundsMax, new Vec3(set.BoundsMin.X, set.BoundsMax.Y, set.BoundsMin.Z), new Vec3(set.BoundsMax.X, set.BoundsMin.Y, set.BoundsMax.Z) })
                ext = Math.Max(ext, (cnr - set.Origin).Length);
            ext += 2 * a;
        }
        else if (set.BallRadius > 0)
        {
            double R = set.BallRadius;
            inside = p => (p - set.Origin).LengthSq <= R * R;
            ext = R + 2 * a;
        }
        else
        {
            // Box of Count nearest-neighbour spacings per side, from Origin, in the lattice frame.
            double L = Math.Max(1, set.Count) * set.Spacing - 1e-9;
            inside = p => { var d = p - set.Origin; double u = Vec3.Dot(d, X), v = Vec3.Dot(d, Y), w = Vec3.Dot(d, Z); return u >= -1e-9 && v >= -1e-9 && w >= -1e-9 && u <= L && v <= L && w <= L; };
            ext = L * 1.8 + 2 * a;
        }
        int nx = (int)Math.Ceiling(ext / (a * cell.X)) + 1, ny = (int)Math.Ceiling(ext / (a * cell.Y)) + 1;
        int nz = set.Kind == LatticeKind.Hex ? 0 : (int)Math.Ceiling(ext / (a * cell.Z)) + 1;
        var rng = new Random(set.Seed);
        centres = new List<Vec3>(); radii = new List<double>();
        // Every site the same radius, large enough to close its cell (equal radii: the power diagram is the
        // Voronoi diagram whatever the radius, so the crystal is unchanged; the cluster just starts a bit inflated).
        double r = 1.04 * CoveringRadius(set.Kind, set.Spacing);
        for (int i = -nx; i <= nx; i++)
            for (int j = -ny; j <= ny; j++)
                for (int k = -nz; k <= nz; k++)
                    foreach (var b in basis)
                    {
                        var q = new Vec3((i * cell.X + b.X) * a, (j * cell.Y + b.Y) * a, (k * cell.Z + b.Z) * a);
                        var p = world(q);
                        if (!inside(p)) continue;
                        if (set.Jitter > 0)
                            p = p + new Vec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1) * (set.Jitter * set.Spacing);
                        centres.Add(p); radii.Add(r);
                    }
    }

    // Graded random scatter: a blue-noise fill of the region where each new bubble's radius is BaseRadius x field(p),
    // and neighbours sit about as far apart as bubbles in a foam (0.9 x the sum of their radii). Bridson-style:
    // new candidates around accepted ones, so the fill is dense without a lattice.
    public static void Scatter(Func<Vec3, bool> inside, Vec3 bmin, Vec3 bmax, double baseRadius, SizeField field, int seed, int maxCount,
                               out List<Vec3> centres, out List<double> radii)
    {
        var rng = new Random(seed);
        var cs = new List<Vec3>(); var rs = new List<double>();
        centres = cs; radii = rs;
        Func<Vec3, double> rad = p => baseRadius * (field != null ? field.Eval(p) : 1);
        double cellSz = baseRadius * Math.Max(0.2, field != null ? field.Min : 1);
        var grid = new Dictionary<long, List<int>>();
        Func<double, long> ci = x => (long)Math.Floor(x / cellSz);
        Func<long, long, long, long> key = (x, y, z) => unchecked((x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L));
        double rMax = baseRadius * (field != null ? field.Max : 1);
        Func<Vec3, double, bool> ok = (p, r) =>
        {
            int reach = (int)Math.Ceiling(0.9 * (r + rMax) / cellSz);
            long x0 = ci(p.X), y0 = ci(p.Y), z0 = ci(p.Z);
            for (long x = x0 - reach; x <= x0 + reach; x++)
                for (long y = y0 - reach; y <= y0 + reach; y++)
                    for (long z = z0 - reach; z <= z0 + reach; z++)
                    {
                        List<int> l; if (!grid.TryGetValue(key(x, y, z), out l)) continue;
                        foreach (int i in l)
                        {
                            double d = 0.9 * (r + rs[i]);
                            if ((cs[i] - p).LengthSq < d * d) return false;
                        }
                    }
            return true;
        };
        Action<Vec3, double> add = (p, r) =>
        {
            long k = key(ci(p.X), ci(p.Y), ci(p.Z)); List<int> l;
            if (!grid.TryGetValue(k, out l)) { l = new List<int>(); grid[k] = l; }
            l.Add(cs.Count); cs.Add(p); rs.Add(r);
        };
        // First sample: the inside point nearest the middle of the box.
        Vec3 mid = (bmin + bmax) * 0.5, first = mid;
        if (!inside(first))
        {
            double best = double.MaxValue;
            for (int t = 0; t < 4000; t++)
            {
                var q = new Vec3(bmin.X + (bmax.X - bmin.X) * rng.NextDouble(), bmin.Y + (bmax.Y - bmin.Y) * rng.NextDouble(), bmin.Z + (bmax.Z - bmin.Z) * rng.NextDouble());
                if (inside(q) && (q - mid).LengthSq < best) { best = (q - mid).LengthSq; first = q; }
            }
            if (best == double.MaxValue) return;
        }
        add(first, rad(first));
        var active = new List<int> { 0 };
        while (active.Count > 0 && cs.Count < maxCount)
        {
            int ai = rng.Next(active.Count), idx = active[ai];
            Vec3 p = cs[idx]; double rp = rs[idx];
            bool placed = false;
            for (int t = 0; t < 30 && !placed; t++)
            {
                Vec3 dir; do dir = new Vec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1); while (dir.LengthSq > 1 || dir.LengthSq < 1e-4);
                dir = dir.Normalized();
                // Guess the new radius from the parent, then evaluate the field where the candidate lands.
                double rq = rp;
                Vec3 q = p;
                for (int it = 0; it < 2; it++)
                {
                    double dist = 0.9 * (rp + rq) * (1 + 0.25 * rng.NextDouble());
                    q = p + dir * dist; rq = rad(q);
                }
                if (!inside(q) || !ok(q, rq)) continue;
                add(q, rq); active.Add(cs.Count - 1); placed = true;
            }
            if (!placed) active.RemoveAt(ai);
        }
    }
}

// A radius multiplier over space. Each part is optional; they multiply.
//   Attractors (points / polylines): Near at distance 0, blending to Far by distance Falloff (smoothstep).
//   Axis: AxisStart at AxisFrom, AxisEnd at AxisTo (linear along the segment, clamped).
//   Noise: exp(Noise x smooth value noise of wavelength NoiseScale), so 0.5 roughly halves / doubles.
// The result is clamped to [Min, Max].
public class SizeField
{
    public List<Vec3> Points = new List<Vec3>();
    public List<List<Vec3>> Polylines = new List<List<Vec3>>();
    public double Near = 0.4, Far = 1, Falloff = 5;
    public bool HasAxis; public Vec3 AxisFrom, AxisTo; public double AxisStart = 1, AxisEnd = 1;
    public double Noise = 0, NoiseScale = 5; public int Seed = 1;
    public double Min = 0.15, Max = 4;

    public bool HasAttractors { get { return Points.Count > 0 || Polylines.Count > 0; } }

    public double Eval(Vec3 p)
    {
        double s = 1;
        if (HasAttractors)
        {
            double d = double.MaxValue;
            foreach (var q in Points) d = Math.Min(d, (p - q).Length);
            foreach (var pl in Polylines)
                for (int i = 0; i + 1 < pl.Count; i++) d = Math.Min(d, SegDist(p, pl[i], pl[i + 1]));
            double t = Math.Max(0, Math.Min(1, d / Math.Max(1e-9, Falloff)));
            t = t * t * (3 - 2 * t);
            s *= Near + (Far - Near) * t;
        }
        if (HasAxis)
        {
            Vec3 ab = AxisTo - AxisFrom; double L2 = ab.LengthSq;
            double t = L2 > 1e-18 ? Math.Max(0, Math.Min(1, Vec3.Dot(p - AxisFrom, ab) / L2)) : 0;
            s *= AxisStart + (AxisEnd - AxisStart) * t;
        }
        if (Noise != 0) s *= Math.Exp(Noise * ValueNoise(p * (1 / Math.Max(1e-9, NoiseScale))));
        return Math.Max(Min, Math.Min(Max, s));
    }

    static double SegDist(Vec3 p, Vec3 a, Vec3 b)
    {
        Vec3 ab = b - a; double L2 = ab.LengthSq;
        double t = L2 > 1e-18 ? Math.Max(0, Math.Min(1, Vec3.Dot(p - a, ab) / L2)) : 0;
        return (p - (a + ab * t)).Length;
    }

    // Smooth value noise in [-1, 1] (lattice hash, quintic interpolation).
    double ValueNoise(Vec3 p)
    {
        int x0 = (int)Math.Floor(p.X), y0 = (int)Math.Floor(p.Y), z0 = (int)Math.Floor(p.Z);
        double fx = p.X - x0, fy = p.Y - y0, fz = p.Z - z0;
        Func<double, double> q = t => t * t * t * (t * (t * 6 - 15) + 10);
        double ux = q(fx), uy = q(fy), uz = q(fz);
        double r = 0;
        for (int dz = 0; dz <= 1; dz++)
            for (int dy = 0; dy <= 1; dy++)
                for (int dx = 0; dx <= 1; dx++)
                {
                    double w = (dx == 1 ? ux : 1 - ux) * (dy == 1 ? uy : 1 - uy) * (dz == 1 ? uz : 1 - uz);
                    r += w * Hash(x0 + dx, y0 + dy, z0 + dz);
                }
        return r;
    }

    double Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647 + Seed * 144665);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xffffff) / (double)0x800000 - 1;
        }
    }
}

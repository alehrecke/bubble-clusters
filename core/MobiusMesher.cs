using System;
using System.Collections.Generic;
using System.Linq;

// Builds a non-manifold film mesh from bubble spheres via the Möbius diagram
// (Saye 2015 "Voronoi interface" polygon chopping, on a Kuhn-split cubic grid).
//
// Phase functions: outside g0 = 0; bubble k: gk(x) = -(|x-ck|^2 - rk^2) / (2 rk).
// Each point belongs to the phase with the largest g. The bubble/outside interface is
// bubble k's sphere; the wall between two bubbles is the sphere of Glassner's
// 1/rC = 1/rB - 1/rA formula (exact when the spheres meet at 60 degrees).
// With 4+ bubbles it isn't exactly the equilibrium, which the solver then corrects.
public static class MobiusMesher
{
    // Kuhn split of a cube: corner index bits are x=1, y=2, z=4. Globally consistent.
    static readonly int[][] Tets =
    {
        new[] { 0, 1, 3, 7 }, new[] { 0, 1, 5, 7 }, new[] { 0, 2, 3, 7 },
        new[] { 0, 2, 6, 7 }, new[] { 0, 4, 5, 7 }, new[] { 0, 4, 6, 7 },
    };
    static readonly int[][] TetEdges =
    {
        new[] { 0, 1 }, new[] { 0, 2 }, new[] { 0, 3 }, new[] { 1, 2 }, new[] { 1, 3 }, new[] { 2, 3 },
    };

    class PVert
    {
        public Vec3 P;
        public double[] G; // phase values, aligned with the cube's phase list
    }

    public static Action<string, FoamMesh> Debug;
    // Per-stage timing hook (Rearranger's rebuild timing). ThreadStatic: rebuilds run on a background thread.
    [ThreadStatic] public static Action<string> StageTimer;

    public static FoamMesh Build(IList<Vec3> centers, IList<double> radii, double h, out string info, HostSurface host = null)
    {
        int n = centers.Count;
        Vec3 lo = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        Vec3 hi = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        for (int i = 0; i < n; i++)
        {
            double r = radii[i];
            Vec3 c = centers[i];
            lo = new Vec3(Math.Min(lo.X, c.X - r), Math.Min(lo.Y, c.Y - r), Math.Min(lo.Z, c.Z - r));
            hi = new Vec3(Math.Max(hi.X, c.X + r), Math.Max(hi.Y, c.Y + r), Math.Max(hi.Z, c.Z + r));
        }
        if (host != null)
        {
            // Bubbles that reach the host are cut by it; extend the box a little past the surface.
            var pad = new Vec3(2 * h, 2 * h, 2 * h);
            lo = lo - pad; hi = hi + pad;
        }
        return Build(BubbleFunctions.FromSpheres(centers, radii, host), lo, hi, h, out info, SizeScale(radii, h, 6));
    }

    // Per-region edge-size scale (index 0 = outside = 1):
    //   small bubbles: about `edgesPerRadius` edges across the radius, down to MinScale x h
    //   ordinary bubbles: the base edge h (CellSize means what it says)
    //   big bubbles (r > CoarsenEdgesPerRadius x h): coarser, r / (CoarsenEdgesPerRadius x h), up to MaxCoarsen x h
    // Big bubbles used to be capped at h, so their triangle count grew with r^2: an r 7 add at CellSize
    // 0.2 needed ~40k triangles and was refused (Alex's run 2026-10-16_011841). Coarsening is capped at 2x:
    // at 4x the border angles next to small bubbles scattered 6.7 deg (vs 2.9 uncoarsened, 3.6 at 2x;
    // harness `big double`). It starts only at 15 x h: starting at 6 x h coarsened ordinary bubbles too
    // and degraded the benchmarks (quad-bubble node scatter 4.4 -> 7.3 deg).
    public const double MinScale = 0.25, MaxCoarsen = 2, CoarsenEdgesPerRadius = 15;

    public static double ScaleFor(double r, double h, double edgesPerRadius)
    {
        if (!(edgesPerRadius > 0)) return 1;
        double fine = r / (edgesPerRadius * h);
        if (fine < 1) return Math.Max(MinScale, fine);
        return Math.Max(1, Math.Min(MaxCoarsen, r / (CoarsenEdgesPerRadius * h)));
    }

    public static double[] SizeScale(IList<double> radii, double h, double edgesPerRadius)
    {
        var sc = new double[radii.Count + 1];
        sc[0] = 1;
        for (int i = 0; i < radii.Count; i++) sc[i + 1] = ScaleFor(radii[i], h, edgesPerRadius);
        return sc;
    }

    // General form: each bubble k has f_k(x) = A|x|^2 + B.x + D (negative inside); phases are
    // g_k = -f_k and g_0 = 0 (outside). Walls f_i = f_j are still spheres. Used for rebuilds
    // with functions fitted to the current films (see Rearranger).
    // h is the target edge length. The grid is sampled at h * gridFactor: 1 gives the
    // cleanest start, and 1.5 is ~2x cheaper (the remesher then refines to h). remeshIters = cleanup rounds.
    public static FoamMesh Build(BubbleFunctions fn, Vec3 lo, Vec3 hi, double h, out string info, double[] regionScale = null,
                                 double gridFactor = 1, int remeshIters = 3)
    {
        double L = h;
        h = h * gridFactor;
        int n = fn.Count;
        var mesh = new FoamMesh { BubbleCount = n };
        // A host solid is phase n+1 inside the mesher; its triangles are relabelled FoamMesh.Solid below.
        int np = fn.Host != null ? n + 1 : n;
        Func<int, Vec3, double> phase = (b, x) => b == n + 1 ? fn.Host.Phase(x) : fn.Phase(b, x);
        lo = lo - new Vec3(2 * h, 2 * h, 2 * h);
        hi = hi + new Vec3(2 * h, 2 * h, 2 * h);
        // Offset the grid by an irrational fraction of h so nodes rarely sit exactly on symmetric interfaces.
        lo = lo - new Vec3(0.1234567 * h, 0.2345678 * h, 0.3456789 * h);
        int nx = (int)Math.Ceiling((hi.X - lo.X) / h) + 1;
        int ny = (int)Math.Ceiling((hi.Y - lo.Y) / h) + 1;
        int nz = (int)Math.Ceiling((hi.Z - lo.Z) / h) + 1;

        // Per grid node: winning phase and its lead over the runner-up. A cube is skipped only
        // when one phase wins all 8 corners by more than tau. Each tet then clips against every
        // phase that is within tau of winning at one of its corners. Any phase that wins
        // somewhere on a tet face is within tau at one of that face's corners, so two tets
        // sharing a face always clip it the same way and the mesh is watertight.
        double tau = 4 * h;
        int nn = nx * ny * nz;
        var owner = new int[nn];
        var lead = new double[nn];
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    Vec3 p = new Vec3(lo.X + i * h, lo.Y + j * h, lo.Z + k * h);
                    int best = 0; double bg = 0, second = double.MinValue;
                    for (int b = 1; b <= np; b++)
                    {
                        double g = phase(b, p);
                        if (g > bg) { second = bg; bg = g; best = b; }
                        else if (g > second) second = g;
                    }
                    int id = (k * ny + j) * nx + i;
                    owner[id] = best;
                    lead[id] = bg - second;
                }

        var weld = new Welder(h * 1e-7);
        var corner = new Vec3[8];
        var near = new List<int>[8];
        var allG = new double[8][];
        for (int c = 0; c < 8; c++) { near[c] = new List<int>(4); allG[c] = new double[np + 1]; }
        var nodeG = new double[nn][];   // per grid node, filled on first use
        var tp = new Vec3[4];
        var phases = new List<int>(6);

        for (int k = 0; k < nz - 1; k++)
            for (int j = 0; j < ny - 1; j++)
                for (int i = 0; i < nx - 1; i++)
                {
                    bool uniform = true;
                    int o0 = owner[(k * ny + j) * nx + i];
                    for (int c = 0; c < 8 && uniform; c++)
                    {
                        int id = ((k + ((c >> 2) & 1)) * ny + j + ((c >> 1) & 1)) * nx + i + (c & 1);
                        if (owner[id] != o0 || lead[id] <= tau) uniform = false;
                    }
                    if (uniform) continue;
                    for (int c = 0; c < 8; c++)
                    {
                        corner[c] = new Vec3(lo.X + (i + (c & 1)) * h, lo.Y + (j + ((c >> 1) & 1)) * h, lo.Z + (k + ((c >> 2) & 1)) * h);
                        // Each grid node's phases are computed once and shared by the (up to) 8 cubes around it.
                        int id = ((k + ((c >> 2) & 1)) * ny + j + ((c >> 1) & 1)) * nx + i + (c & 1);
                        double[] g = nodeG[id];
                        if (g == null)
                        {
                            g = new double[np + 1];
                            for (int b = 0; b <= np; b++) g[b] = phase(b, corner[c]);
                            nodeG[id] = g;
                        }
                        allG[c] = g;
                        double gmax = 0;
                        for (int b = 0; b <= np; b++) if (g[b] > gmax) gmax = g[b];
                        near[c].Clear();
                        for (int b = 0; b <= np; b++) if (g[b] >= gmax - tau) near[c].Add(b);
                    }
                    foreach (var tet in Tets)
                    {
                        phases.Clear();
                        for (int v = 0; v < 4; v++)
                            foreach (int b in near[tet[v]]) if (!phases.Contains(b)) phases.Add(b);
                        if (phases.Count < 2) continue;
                        phases.Sort();
                        var cg = new double[4][];
                        for (int v = 0; v < 4; v++)
                        {
                            tp[v] = corner[tet[v]];
                            cg[v] = new double[phases.Count];
                            for (int q = 0; q < phases.Count; q++) cg[v][q] = allG[tet[v]][phases[q]];
                        }
                        ChopTet(tp, cg, phases, weld, mesh);
                    }
                }

        mesh.V = weld.Points;
        int soup = mesh.T.Count;
        if (StageTimer != null) StageTimer("chop");
        if (fn.Host != null)
        {
            // Solid phase -> FoamMesh.Solid; dry solid (solid | outside) isn't meshed.
            var kept = new List<FoamTri>(mesh.T.Count);
            foreach (var t0 in mesh.T)
            {
                var t = t0;
                if (t.Neg == n + 1) t.Neg = FoamMesh.Solid;
                if (t.Pos == n + 1) t.Pos = FoamMesh.Solid;
                if ((t.Neg == FoamMesh.Solid && t.Pos == 0) || (t.Pos == FoamMesh.Solid && t.Neg == 0)) continue;
                kept.Add(t);
            }
            mesh.T = kept;
        }
        mesh.Clean();
        SnapToHost(mesh, fn.Host);
        if (Debug != null) Debug("soup", mesh);
        // Slivers go through the remesher's collapse (link condition, no new chords). The
        // simpler collapse used before created fold edges (a film meeting itself at a border).
        if (StageTimer != null) StageTimer("clean");
        int collapsed = Remesher.CollapseShort(mesh, 0.2 * h, 4.0 / 3.0 * L, regionScale);
        if (StageTimer != null) StageTimer("slivers");
        if (Debug != null) Debug("slivers", mesh);
        var rm = new Remesher.Result();
        for (int it = 0; it < remeshIters; it++)
        {
            rm.Splits += Remesher.SplitLong(mesh, 4.0 / 3.0 * L, true, regionScale);
            if (Debug != null) Debug("split", mesh);
            rm.Collapses += Remesher.CollapseShort(mesh, 0.8 * L, 4.0 / 3.0 * L, regionScale);
            if (Debug != null) Debug("collapse", mesh);
            rm.Flips += Remesher.FlipDelaunay(mesh);
            if (Debug != null) Debug("flip", mesh);
        }
        SnapToHost(mesh, fn.Host);
        if (StageTimer != null) StageTimer("remesh");
        var hist = Remesher.EdgeHistogram(mesh);
        info = "grid " + nx + "x" + ny + "x" + nz + ", soup " + soup + " tris, collapsed " + collapsed +
               " slivers, remesh " + rm.Splits + "/" + rm.Collapses + "/" + rm.Flips + " (split/collapse/flip) -> " +
               mesh.V.Count + " verts, " + mesh.T.Count + " tris; edges by #tris: open " + hist[1] +
               ", film " + hist[2] + ", border " + hist[3] + ", 4+ " + (hist[4] + hist[5]);
        return mesh;
    }

    // Footprint vertices (on a solid triangle) onto the host surface. The mesher's interface sits a
    // hair off it (steep but finite solid phase), and the remesher's midpoints cut corners on curved hosts.
    public static void SnapToHost(FoamMesh mesh, HostSurface host)
    {
        if (host == null) return;
        var on = new bool[mesh.V.Count];
        foreach (var t in mesh.T)
            if (FoamMesh.IsSolid(t)) on[t.A] = on[t.B] = on[t.C] = true;
        for (int v = 0; v < on.Length; v++) if (on[v]) mesh.V[v] = host.Project(mesh.V[v]);
    }

    static void ChopTet(Vec3[] corner, double[][] cg, List<int> phases, Welder weld, FoamMesh mesh)
    {
        int np = phases.Count;
        for (int a = 0; a < np; a++)
            for (int b = a + 1; b < np; b++)
            {
                var d = new double[4];
                bool pos = false, neg = false;
                for (int v = 0; v < 4; v++)
                {
                    d[v] = cg[v][a] - cg[v][b];
                    if (d[v] == 0) d[v] = 1e-14; // symbolic perturbation
                    if (d[v] > 0) pos = true; else neg = true;
                }
                if (!pos || !neg) continue;

                // Plane polygon d = 0 inside the tet.
                var poly = new List<PVert>(4);
                foreach (var e in TetEdges)
                {
                    double d0 = d[e[0]], d1 = d[e[1]];
                    if ((d0 > 0) == (d1 > 0)) continue;
                    double t = d0 / (d0 - d1);
                    int c0 = e[0], c1 = e[1];
                    var pv = new PVert { P = Vec3.Lerp(corner[c0], corner[c1], t), G = new double[np] };
                    for (int p = 0; p < np; p++) pv.G[p] = cg[c0][p] + (cg[c1][p] - cg[c0][p]) * t;
                    poly.Add(pv);
                }
                if (poly.Count == 4) OrderConvex(poly);

                // Keep only where phases a (= b) beat every other phase.
                for (int c = 0; c < np && poly.Count >= 3; c++)
                {
                    if (c == a || c == b) continue;
                    poly = Clip(poly, a, c);
                }
                if (poly.Count < 3) continue;

                // Orient: normal must point away from phase a's side (d > 0) toward b.
                int va = 0;
                for (int v = 1; v < 4; v++) if (d[v] > d[va]) va = v;
                Vec3 n = Vec3.Zero;
                for (int q = 1; q + 1 < poly.Count; q++)
                    n = n + Vec3.Cross(poly[q].P - poly[0].P, poly[q + 1].P - poly[0].P);
                bool flip = Vec3.Dot(n, corner[va] - poly[0].P) > 0;

                var idx = new int[poly.Count];
                for (int q = 0; q < poly.Count; q++) idx[q] = weld.Get(poly[q].P);
                for (int q = 1; q + 1 < poly.Count; q++)
                {
                    if (flip) mesh.T.Add(new FoamTri(idx[0], idx[q + 1], idx[q], phases[a], phases[b]));
                    else mesh.T.Add(new FoamTri(idx[0], idx[q], idx[q + 1], phases[a], phases[b]));
                }
            }
    }

    static void OrderConvex(List<PVert> poly)
    {
        Vec3 c = Vec3.Zero;
        foreach (var p in poly) c = c + p.P;
        c = c / poly.Count;
        Vec3 n = Vec3.Cross(poly[1].P - poly[0].P, poly[2].P - poly[0].P);
        if (n.LengthSq < 1e-300) n = Vec3.Cross(poly[2].P - poly[0].P, poly[3].P - poly[0].P);
        Vec3 u = (poly[0].P - c).Normalized();
        Vec3 w = Vec3.Cross(n.Normalized(), u);
        poly.Sort((p, q) => Math.Atan2(Vec3.Dot(p.P - c, w), Vec3.Dot(p.P - c, u))
                     .CompareTo(Math.Atan2(Vec3.Dot(q.P - c, w), Vec3.Dot(q.P - c, u))));
    }

    // Sutherland-Hodgman: keep G[a] - G[c] >= 0.
    static List<PVert> Clip(List<PVert> poly, int a, int c)
    {
        var res = new List<PVert>(poly.Count + 1);
        for (int q = 0; q < poly.Count; q++)
        {
            PVert p0 = poly[q], p1 = poly[(q + 1) % poly.Count];
            double e0 = p0.G[a] - p0.G[c], e1 = p1.G[a] - p1.G[c];
            if (e0 >= 0) res.Add(p0);
            if ((e0 >= 0) != (e1 >= 0))
            {
                double t = e0 / (e0 - e1);
                var pv = new PVert { P = Vec3.Lerp(p0.P, p1.P, t), G = new double[p0.G.Length] };
                for (int k = 0; k < pv.G.Length; k++) pv.G[k] = p0.G[k] + (p1.G[k] - p0.G[k]) * t;
                res.Add(pv);
            }
        }
        return res;
    }

    // Merge points closer than tol (spatial hash, checks neighbouring cells).
    class Welder
    {
        public readonly List<Vec3> Points = new List<Vec3>();
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>(LongKey.Comparer);
        readonly double tol, cell;
        public Welder(double tol) { this.tol = tol; cell = tol * 4; }

        static long Key(long x, long y, long z) { unchecked { return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L); } }

        public int Get(Vec3 p)
        {
            long x = (long)Math.Floor(p.X / cell), y = (long)Math.Floor(p.Y / cell), z = (long)Math.Floor(p.Z / cell);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        List<int> l;
                        if (!cells.TryGetValue(Key(x + dx, y + dy, z + dz), out l)) continue;
                        foreach (int i in l) if ((Points[i] - p).LengthSq <= tol * tol) return i;
                    }
            int id = Points.Count;
            Points.Add(p);
            long k = Key(x, y, z);
            List<int> list;
            if (!cells.TryGetValue(k, out list)) { list = new List<int>(2); cells[k] = list; }
            list.Add(id);
            return id;
        }
    }
}

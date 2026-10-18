using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Topology changes (Phase 3) by rebuilding: when the films say the cluster wants to
// rearrange, fit per-bubble functions to the current films, remesh from them (MobiusMesher),
// and carry on relaxing with the same target volumes. The rebuilt diagram picks a consistent
// topology near the current shape, which resolves a collapsing wedge or a vanishing border.
//
// Triggers (checked by FoamSolver every RearrangeEvery steps):
//   pinch  - along a border chain, two of its three films have folded together (mean smallest
//            film angle < PinchAngle): the bubble between them is being squeezed out (T1).
//   short  - a border between two nodes shorter than ShortBorder x edge length (T1).
public static class Rearranger
{
    public class Event
    {
        public string Kind;
        public int[] Regions;
        public double Value;
        public int[] Grow;      // contact: bubbles to enlarge before calibration so the pieces overlap
        public Vec3 Shift;      // landing: move bubble Regions[0] by this before calibration so it reaches into the host
        public string Detail;
        public Vec3 At;         // where it happens (centre of the border / contact point) ...
        public double Radius;   // ... and how far around it; ShapeTransfer keeps the rebuilt mesh there
        public override string ToString()
        {
            if (Kind == "contact" && Regions.Length == 2 && Regions[1] == FoamMesh.Solid)
                return "bubble " + Regions[0] + " " + Detail;
            if (Kind == "contact")
                return "contact between bubbles " + string.Join(" and ", Regions) + ": " + Detail;
            return string.Format(CultureInfo.InvariantCulture, "{0} at border {1} ({2:F1})", Kind, string.Join("-", Regions), Value);
        }
    }

    // prevLength: border lengths from the previous check (keyed by region triple), updated here.
    // Pass null to skip the shrinking test (diagnostics).
    public static List<Event> Detect(FoamSolver s, Dictionary<string, double> prevLength = null)
    {
        var m = s.Mesh;
        var events = new List<Event>();
        var seen = new Dictionary<string, double>();
        List<int> nodes;
        var chains = FoamAnalysis.BorderChains(m, out nodes);
        var et = m.EdgeTris();
        double L = s.TargetEdge;
        foreach (var c in chains)
        {
            if (c.Verts.Count < 2) continue;
            double len = 0;
            for (int i = 0; i + 1 < c.Verts.Count; i++) len += (m.V[c.Verts[i + 1]] - m.V[c.Verts[i]]).Length;
            int[] regions = null;
            double angSum = 0; int angN = 0;
            for (int i = 0; i + 1 < c.Verts.Count; i++)
            {
                int a = c.Verts[i], b = c.Verts[i + 1];
                List<int> tris;
                if (!et.TryGetValue(FoamMesh.EdgeKey(a, b), out tris) || tris.Count != 3) continue;
                if (regions == null)
                {
                    var rs = new SortedSet<int>();
                    foreach (int ti in tris) { rs.Add(m.T[ti].Neg); rs.Add(m.T[ti].Pos); }
                    regions = rs.ToArray();
                }
                Vec3 e = (m.V[b] - m.V[a]).Normalized(), mid = (m.V[a] + m.V[b]) * 0.5;
                var dirs = new Vec3[3];
                for (int k = 0; k < 3; k++)
                {
                    var t = m.T[tris[k]];
                    int o = t.A != a && t.A != b ? t.A : (t.B != a && t.B != b ? t.B : t.C);
                    Vec3 d = m.V[o] - mid;
                    dirs[k] = (d - e * Vec3.Dot(d, e)).Normalized();
                }
                double mn = Math.Min(Ang(dirs[0], dirs[1]), Math.Min(Ang(dirs[0], dirs[2]), Ang(dirs[1], dirs[2])));
                angSum += mn; angN++;
            }
            if (regions == null) continue;
            Vec3 at = Vec3.Zero;
            foreach (int v in c.Verts) at = at + m.V[v];
            at = at / c.Verts.Count;
            double rad = 0;
            foreach (int v in c.Verts) rad = Math.Max(rad, (m.V[v] - at).Length);
            if (angN > 0 && angSum / angN < s.S.PinchAngle)
                events.Add(new Event { Kind = "pinch", Regions = regions, Value = angSum / angN, At = at, Radius = rad });
            else if (!c.Closed)
            {
                // "Short" is relative to the local mesh size (small bubbles are meshed finer), and
                // only counts while the border is still shrinking. Tiny bubbles have short but
                // stable borders, and the old global test (0.5 x CellSize) rebuilt every ~150 steps
                // for 28 bubbles in Alex's run, costing them 30-55% volume each time.
                // Big bubbles are meshed coarser (scale > 1), so their borders' "short" threshold scales up too.
                double local = double.MaxValue;
                if (s.RegionScale != null)
                    foreach (int r in regions) if (r > 0 && r < s.RegionScale.Length) local = Math.Min(local, s.RegionScale[r]);
                if (local == double.MaxValue) local = 1;
                string key = string.Join("-", regions);
                double before;
                bool shrinking = prevLength == null || (prevLength.TryGetValue(key, out before) && len < 0.85 * before);
                if (prevLength != null) seen[key] = len;
                if (len < s.S.ShortBorder * L * local && shrinking)
                    events.Add(new Event { Kind = "short", Regions = regions, Value = len / (L * local), At = at, Radius = rad });
            }
        }
        events.AddRange(Contacts.Detect(s));
        if (prevLength != null)
        {
            prevLength.Clear();
            foreach (var kv in seen) prevLength[kv.Key] = kv.Value;
        }
        return events;
    }

    static double Ang(Vec3 a, Vec3 b)
    {
        return Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(a, b)))) * 180 / Math.PI;
    }

    // Least-squares fit of f_k = A|x|^2 + B.x + D to the current films. A_k is fixed from the
    // solved pressure, A = p / (4 tension): that's the value for a unit-gradient sphere function
    // (A = 1/2r, p = 2 tension / r). It keeps every bubble the right way out. Fitting A freely gave
    // interior bubbles (no outer film) negative A in the 15-bubble GH run: inside-out functions
    // that swallowed every other bubble in the rebuild. B and D (4 unknowns per bubble) come from:
    //   outer film of k:  f_k = 0 and grad f_k . n_out = 1   (zero set + scale)
    //   wall i|j:         f_i - f_j = 0
    //   weak prior:       f_k = sphere(centroid_k, 2/pressure_k) at 7 points around the centroid
    // Triangle centroids weighted by area.
    public static BubbleFunctions Fit(FoamSolver s, double priorWeight)
    {
        var m = s.Mesh;
        int n = m.BubbleCount, N = 4 * n;
        var M = new double[N, N];
        var rhs = new double[N];
        var row = new double[N];
        var idx = new List<int>(10);

        // Region centroids (volume-weighted) and sizes for the prior.
        var cen = new Vec3[n + 1];
        var vol = new double[n + 1];
        double totalArea = 0;
        foreach (var t in m.T)
        {
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            double v = Vec3.Dot(a, Vec3.Cross(b, c)) / 6.0;
            Vec3 tc = (a + b + c) / 4.0; // tet (origin, a, b, c) centroid
            if (t.Neg > 0) { vol[t.Neg] += v; cen[t.Neg] = cen[t.Neg] + tc * v; }
            if (t.Pos > 0) { vol[t.Pos] -= v; cen[t.Pos] = cen[t.Pos] - tc * v; }
            totalArea += m.TriArea(t);
        }

        // Bubbles with a real outer film: A from pressure (that film is a sphere of radius 2/p).
        // Interior bubbles (walls only, or almost): A from size, 1/(2 r_eff). Their pressure can be
        // far off while squeezed mid-rearrangement. In Alex's live run a tiny interior bubble
        // (r 0.27) had p 1.52 → "radius 1.3", and the rebuild gave it 18x its volume.
        var outerArea = new double[n + 1];
        var allArea = new double[n + 1];
        foreach (var t in m.T)
        {
            double a = m.TriArea(t);
            if (t.Neg > 0) { allArea[t.Neg] += a; if (t.Pos == 0) outerArea[t.Neg] += a; }
            if (t.Pos > 0) { allArea[t.Pos] += a; if (t.Neg == 0) outerArea[t.Pos] += a; }
        }
        // A = 1/(2R). R comes from geometry, not pressure: a sphere fitted to the bubble's own outer
        // film (at rest that film is exactly a sphere), else its equal-volume radius. Clamped to
        // [0.5, 3] x that radius. Pressure was fragile while bubbles inflate or get squeezed: in
        // Alex's CellSize-0.1 run a bubble with p 0.71 became a radius-2.8 "sphere" that leaked out of
        // the rebuild box, so every rebuild had 740 open edges.
        var outerPts = new List<Vec3>[n + 1];
        for (int k = 1; k <= n; k++) outerPts[k] = new List<Vec3>();
        foreach (var t in m.T)
        {
            if ((t.Neg == 0) == (t.Pos == 0)) continue;
            int k = t.Neg == 0 ? t.Pos : t.Neg;
            outerPts[k].Add((m.V[t.A] + m.V[t.B] + m.V[t.C]) / 3.0);
        }
        Afix = new double[n + 1];
        for (int k = 1; k <= n; k++)
        {
            double rv = vol[k] > 0 ? Math.Pow(3 * vol[k] / (4 * Math.PI), 1.0 / 3) : 1;
            bool interior = outerArea[k] < 0.1 * allArea[k];
            double R = rv;
            Vec3 fc; double fr;
            if (!(interior && s.S.InteriorFitBySize) && outerPts[k].Count >= 8 && FoamAnalysis.FitSphere(outerPts[k], out fc, out fr) && fr > 0)
                R = fr;
            R = Math.Max(0.5 * rv, Math.Min(3 * rv, R));
            Afix[k] = 1 / (2 * R);
        }

        foreach (var t in m.T)
        {
            if (FoamMesh.IsSolid(t)) continue;   // footprints: the host function decides where they are
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            Vec3 nn = Vec3.Cross(b - a, c - a);
            double area = 0.5 * nn.Length;
            if (area <= 0) continue;
            Vec3 nrm = nn.Normalized();               // points from Neg into Pos
            Vec3 x = (a + b + c) / 3.0;
            if (t.Neg == 0 || t.Pos == 0)
            {
                int k = t.Neg == 0 ? t.Pos : t.Neg;
                Vec3 nout = t.Neg == k ? nrm : -nrm;
                // f_k(x) = 0
                Clear(row, idx); Put(row, idx, k, x.LengthSq, x, 1, 1);
                Accumulate(M, rhs, row, idx, 0, area);
                // grad f_k . n_out = 1: 2A (x.n) + B.n = 1
                Clear(row, idx); Put(row, idx, k, 2 * Vec3.Dot(x, nout), nout, 0, 1);
                Accumulate(M, rhs, row, idx, 1, area);
            }
            else
            {
                Clear(row, idx);
                Put(row, idx, t.Neg, x.LengthSq, x, 1, 1);
                Put(row, idx, t.Pos, x.LengthSq, x, 1, -1);
                Accumulate(M, rhs, row, idx, 0, area);
            }
        }

        // Prior: keeps interior bubbles (walls only) and the overall gauge well posed.
        double w = priorWeight * totalArea / Math.Max(1, n) / 7.0;
        for (int k = 1; k <= n; k++)
        {
            if (!(vol[k] > 0)) continue;
            Vec3 c0 = cen[k] / vol[k];
            double r0 = s.Pressure[k] > 1e-9 ? 2 * s.S.Tension / s.Pressure[k] : Math.Pow(3 * vol[k] / (4 * Math.PI), 1.0 / 3);
            var dirs = new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(-1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1), new Vec3(0, 0, -1) };
            double rv = Math.Pow(3 * vol[k] / (4 * Math.PI), 1.0 / 3);
            foreach (var d in dirs)
            {
                Vec3 x = c0 + d * rv;
                double f0 = ((x - c0).LengthSq - r0 * r0) / (2 * r0);
                Clear(row, idx); Put(row, idx, k, x.LengthSq, x, 1, 1);
                Accumulate(M, rhs, row, idx, f0, w);
            }
        }

        var sol = LinAlg.Solve(M, rhs);
        var fn = new BubbleFunctions(n) { Host = s.Host };
        for (int k = 0; k < n; k++)
        {
            fn.A[k] = Afix[k + 1];
            fn.B[k] = new Vec3(sol[4 * k], sol[4 * k + 1], sol[4 * k + 2]);
            fn.D[k] = sol[4 * k + 3];
            // A bubble with no volume (dead) must never claim space in a rebuild.
            if (!(vol[k + 1] > 0)) { fn.A[k] = 0; fn.B[k] = Vec3.Zero; fn.D[k] = 1e9; }
        }
        return fn;
    }

    [ThreadStatic] static double[] Afix;
    [ThreadStatic] static double known;

    static void Clear(double[] row, List<int> idx)
    {
        foreach (int i in idx) row[i] = 0;
        idx.Clear();
        known = 0;
    }

    // Adds sign * [x.X, x.Y, x.Z, d] for bubble k (1-based) to the sparse row; the fixed
    // A_k * q part goes into `known` and is moved to the right-hand side.
    static void Put(double[] row, List<int> idx, int k, double q, Vec3 x, double d, double sign)
    {
        int o = 4 * (k - 1);
        known += sign * q * Afix[k];
        double[] vals = { x.X, x.Y, x.Z, d };
        for (int i = 0; i < 4; i++)
        {
            if (!idx.Contains(o + i)) idx.Add(o + i);
            row[o + i] += sign * vals[i];
        }
    }

    static void Accumulate(double[,] M, double[] rhs, double[] row, List<int> idx, double target, double weight)
    {
        target -= known;
        foreach (int i in idx)
        {
            rhs[i] += weight * row[i] * target;
            foreach (int j in idx) M[i, j] += weight * row[i] * row[j];
        }
    }

    // A rebuild split in two: Prepare (main thread: fit + snapshot, ~40 ms) and Run (pure
    // geometry, no shared state, so it can run on a background thread while the solver keeps
    // relaxing). The caller applies Mesh with FoamSolver.ReplaceMesh when Fail is null.
    public class RebuildJob
    {
        public BubbleFunctions Fn;
        public Vec3 Lo, Hi;
        public double H, GridFactor;
        public int RemeshIters;
        public double[] Scale, CurrentVolume;
        public string What;                  // the events that triggered it
        public double AddRadius;             // > 0: this job adds a bubble (the last region) that grows to this radius
        public Vec3 AddAt;
        public double AddR0;                 // seed radius
        public int Step;                     // solver step when prepared
        public FoamMesh Mesh;                // result
        public FoamMesh Candidate;           // the built mesh even if refused (diagnostics)
        public string Info, Fail;
        public long Ms;
        public string Timing = "";           // per stage, for the log

        public string Calibration;
        public List<int> Grow = new List<int>();   // bubbles enlarged by ~one grid cell before calibration (contacts)
        public List<KeyValuePair<int, Vec3>> Shift = new List<KeyValuePair<int, Vec3>>(); // bubbles moved before calibration (landing)
        public FoamMesh Old;                 // snapshot for ShapeTransfer (null = off)
        public List<Vec3> ZoneAt = new List<Vec3>();
        public List<double> ZoneR = new List<double>();
        public ShapeTransfer.Stats Transfer;   // bubbles enlarged by ~one grid cell before calibration (contacts)

        public void Run()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long tCalV = 0, tBuildV = 0, tTransferV = 0;
            var stageV = new System.Text.StringBuilder();
            try
            {
                // For an add, calibrate the existing bubbles first, then (re)place the seed: calibrating
                // with the seed present let the neighbours squeeze it out ("new bubble got no volume").
                int existing = AddRadius > 0 ? Fn.Count - 1 : Fn.Count;
                // Landing: a bubble hovering just above the host isn't cut by it, so the rebuild gave it
                // no footprint and it "landed" again and again (Alex's hand-mesh run 2026-10-16_002904).
                // Translate its function so its lowest point is a grid cell inside the solid; calibration
                // then refills the volume the solid takes, which keeps it pressed on.
                // f(x - d) = A|x|^2 + (B - 2A d).x + (A|d|^2 - B.d + D).
                foreach (var kv in Shift)
                {
                    int i = kv.Key - 1;
                    if (i < 0 || i >= existing) continue;
                    Vec3 d = kv.Value;
                    Fn.D[i] += Fn.A[i] * d.LengthSq - Vec3.Dot(Fn.B[i], d);
                    Fn.B[i] = Fn.B[i] - d * (2 * Fn.A[i]);
                }
                // Contact: push the smaller piece's boundaries out by about one grid cell so the
                // pieces overlap and the diagram makes a wall between them.
                foreach (int k in Grow)
                {
                    if (k < 1 || k > existing) continue;
                    double r = Math.Pow(3 * Math.Max(CurrentVolume[k], 1e-12) / (4 * Math.PI), 1.0 / 3);
                    Fn.D[k - 1] -= H * GridFactor * Math.Max(0.2, 2 * Fn.A[k - 1] * r);
                }
                Calibration = CalibrateVolumes(Fn, Lo, Hi, H * GridFactor / 1.5, CurrentVolume, existing);
                tCalV = sw.ElapsedMilliseconds;
                if (AddRadius > 0) PlaceSeed(Fn, AddAt, AddR0);
                string info;
                var stage = stageV;
                long tStage = sw.ElapsedMilliseconds;
                MobiusMesher.StageTimer = name => { long now = sw.ElapsedMilliseconds; stage.Append(" ").Append(name).Append(" ").Append(now - tStage); tStage = now; };
                var mesh = MobiusMesher.Build(Fn, Lo, Hi, H, out info, Scale, GridFactor, RemeshIters);
                MobiusMesher.StageTimer = null;
                tBuildV = sw.ElapsedMilliseconds;
                Info = info;
                if (Old != null && ZoneAt.Count > 0)
                {
                    double g = H * GridFactor;
                    Transfer = ShapeTransfer.Apply(mesh, Old, ZoneAt, ZoneR, 2 * g, 3 * g, 3 * g, 0.75 * g);
                }
                // Compare with the volumes at the snapshot, not the targets: early on bubbles
                // are still inflating toward their targets.
                var vol = mesh.Volumes();
                for (int k = 1; k <= mesh.BubbleCount && Fail == null; k++)
                    if (k >= CurrentVolume.Length) { if (!(vol[k] > 0)) Fail = "the new bubble got no volume"; }
                    else if (!(CurrentVolume[k] > 0)) { } // already dead: nothing to lose
                    // A bubble that comes out undersized is fine: FoamSolver.Finish re-inflates it
                    // through the volume ramp. Refusing below 50% (as before) silently dropped every
                    // add in Alex's 15-bubble live runs: squeezed small bubbles fit poorly (32-46%).
                    // Only a bubble that all but vanishes is refused.
                    else if (!(vol[k] > 0.05 * CurrentVolume[k]))
                        Fail = "bubble " + k + " would vanish (" + (vol[k] / CurrentVolume[k]).ToString("P0", CultureInfo.InvariantCulture) + " of its volume)";
                tTransferV = sw.ElapsedMilliseconds;
                var hist = Remesher.EdgeHistogram(mesh);
                if (Fail == null && (hist[1] > 0 || hist[4] + hist[5] > 0))
                    Fail = "rebuilt mesh not watertight (" + hist[1] + " open edges, " + (hist[4] + hist[5]) + " edges with 4+ films)";
                Candidate = mesh;
                if (Fail == null) Mesh = mesh;
            }
            catch (Exception e) { Fail = e.GetType().Name + ": " + e.Message; }
            Ms = sw.ElapsedMilliseconds;
            Timing = string.Format(CultureInfo.InvariantCulture, "calibrate {0} / mesh {1} ({2}) / transfer {3} / checks {4} ms",
                                   tCalV, tBuildV - tCalV, stageV.ToString().Trim(), tTransferV - tBuildV, Ms - tTransferV);
        }
    }

    // A rebuild that also seeds a new bubble at `at`: a small sphere function (radius r0)
    // shifted so that it beats every existing bubble right at `at`. Placed at a four-way node,
    // it appears as a tiny bubble wedged between its four neighbours, then inflates.
    public static RebuildJob PrepareAdd(FoamSolver s, Vec3 at, double r0, double radius)
    {
        var job = Prepare(s, "add bubble " + (s.Mesh.BubbleCount + 1) + " at " + at);
        var old = job.Fn;
        int n = old.Count;
        var fn = new BubbleFunctions(n + 1) { Host = old.Host };
        Array.Copy(old.A, fn.A, n); Array.Copy(old.B, fn.B, n); Array.Copy(old.D, fn.D, n);
        job.Fn = fn;
        // Nudge the seed off the node: centred exactly where four bubbles already tie, it produced a
        // non-generic edge where four films meet ("edges with 4+ films").
        var jr = new Random(s.Steps * 7919 + n);
        at = at + new Vec3(jr.NextDouble() - 0.5, jr.NextDouble() - 0.5, jr.NextDouble() - 0.5) * (0.2 * r0);
        job.AddAt = at;
        PlaceSeed(fn, at, r0);
        job.AddR0 = r0;
        if (job.Scale != null)
        {
            var sc = new double[job.Scale.Length + 1];
            Array.Copy(job.Scale, sc, job.Scale.Length);
            sc[sc.Length - 1] = MobiusMesher.ScaleFor(radius, s.TargetEdge, s.S.EdgesPerRadius);
            job.Scale = sc;
        }
        job.AddRadius = radius;
        job.AddAt = at;
        // A seed outside the current foam (AddAt in open space) must be inside the rebuild box.
        var reach = new Vec3(3 * r0 + 2 * job.H, 3 * r0 + 2 * job.H, 3 * r0 + 2 * job.H);
        Vec3 lo = at - reach, hi = at + reach;
        job.Lo = new Vec3(Math.Min(job.Lo.X, lo.X), Math.Min(job.Lo.Y, lo.Y), Math.Min(job.Lo.Z, lo.Z));
        job.Hi = new Vec3(Math.Max(job.Hi.X, hi.X), Math.Max(job.Hi.Y, hi.Y), Math.Max(job.Hi.Z, hi.Z));
        if (job.Old != null) { job.ZoneAt.Add(at); job.ZoneR.Add(r0); }
        return job;
    }

    // Shift each bubble's offset D so the region its function claims has the right volume.
    // The fit matches the films but nothing pins down how much space a function claims away from
    // them. A squeezed interior bubble (r 0.27 in Alex's live run) claimed 15-18x its volume, and
    // a neighbour got 31%. Volumes are measured by sampling the phase winner on a grid (no
    // meshing). Lowering f_k by d grows region k by about d |grad f| times its surface area.
    // Iterated, damped, each shift capped at half the bubble radius.
    // skip: a bubble to leave alone (a seed for an add), or -1.
    // count: only the first `count` bubbles take part (a seed beyond them is ignored).
    public static string CalibrateVolumes(BubbleFunctions fn, Vec3 lo, Vec3 hi, double cell, double[] target, int count)
    {
        int n = count, skip = -1;
        int nx = Math.Max(2, (int)Math.Ceiling((hi.X - lo.X) / cell)), ny = Math.Max(2, (int)Math.Ceiling((hi.Y - lo.Y) / cell)), nz = Math.Max(2, (int)Math.Ceiling((hi.Z - lo.Z) / cell));
        double cv = cell * cell * cell;
        var vol = new double[n + 1];
        // Samples inside a host solid belong to no bubble (computed once: the solid doesn't move).
        bool[] solid = null;
        if (fn.Host != null)
        {
            solid = new bool[nx * ny * nz];
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                        solid[(k * ny + j) * nx + i] = fn.Host.Height(new Vec3(lo.X + (i + 0.5) * cell, lo.Y + (j + 0.5) * cell, lo.Z + (k + 0.5) * cell)) < 0;
        }
        double worstBefore = 0, worstAfter = 0;
        for (int it = 0; it < 16; it++)
        {
            Array.Clear(vol, 0, vol.Length);
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        if (solid != null && solid[(k * ny + j) * nx + i]) continue;
                        double x = lo.X + (i + 0.5) * cell, y = lo.Y + (j + 0.5) * cell, z = lo.Z + (k + 0.5) * cell;
                        double r2 = x * x + y * y + z * z;
                        int best = 0; double bf = 0; // outside: f = 0, bubble wins where f < its rivals
                        for (int b = 0; b < n; b++)
                        {
                            double f = fn.A[b] * r2 + fn.B[b].X * x + fn.B[b].Y * y + fn.B[b].Z * z + fn.D[b];
                            if (f < bf) { bf = f; best = b + 1; }
                        }
                        vol[best] += cv;
                    }
            double worst = 0;
            for (int b = 1; b <= n; b++)
            {
                if (b == skip || b >= target.Length || !(target[b] > 0)) continue;
                double rel = Math.Abs(vol[b] - target[b]) / target[b];
                worst = Math.Max(worst, rel);
            }
            if (it == 0) worstBefore = worst;
            worstAfter = worst;
            if (worst < 0.03) break;
            for (int b = 1; b <= n; b++)
            {
                if (b == skip || b >= target.Length || !(target[b] > 0)) continue;
                // Work in radius, not volume: move the boundary inward by (current equivalent radius
                // - target radius). Exact for a sphere-like region and stable when a region starts
                // 100x too big (the old volume-linearised step capped at r/2 couldn't catch up).
                double r = Math.Pow(3 * target[b] / (4 * Math.PI), 1.0 / 3);
                double rc = Math.Pow(3 * Math.Max(vol[b], 0.01 * target[b]) / (4 * Math.PI), 1.0 / 3);
                double dist = 0.8 * (rc - r);                                  // > 0: too big, move inward
                dist = Math.Max(-0.5 * Math.Max(r, rc), Math.Min(0.5 * Math.Max(r, rc), dist));
                // Raising f by delta moves the boundary by delta/|grad f|, and |grad f| ~ 2 A rc on a
                // sphere-like function at its boundary.
                double gradScale = Math.Max(0.2, 2 * fn.A[b - 1] * rc);
                fn.D[b - 1] += dist * gradScale;
            }
        }
        return string.Format(CultureInfo.InvariantCulture, "volume calibration: worst error {0:P0} -> {1:P0}", worstBefore, worstAfter);
    }

    // The last function becomes a sphere of radius r0 at `at`, shifted to beat every other
    // bubble (and the outside air, f = 0) right at `at`.
    public static void PlaceSeed(BubbleFunctions fn, Vec3 at, double r0)
    {
        int n = fn.Count - 1;
        double m = 0;
        for (int k = 1; k <= n; k++) m = Math.Min(m, fn.F(k, at));
        fn.A[n] = 1 / (2 * r0);
        fn.B[n] = at * (-1 / r0);
        // Extra -r0: neighbours' functions fall roughly linearly (|grad f| ~ 1) away from `at` while
        // the seed rises quadratically, so beating them only at `at` gave a region of radius ~0.4 r0
        // (sub-grid, near-empty, and it produced a 4-film edge). With the shift the seed wins out to ~r0.
        fn.D[n] = (at.LengthSq - r0 * r0) / (2 * r0) + m - r0;
    }

    public static RebuildJob Prepare(FoamSolver s, string what, List<Event> events = null)
    {
        var m = s.Mesh;
        Vec3 lo = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        Vec3 hi = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var p in m.V)
        {
            lo = new Vec3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
            hi = new Vec3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
        }
        double h = s.TargetEdge;
        var pad = new Vec3(2 * h, 2 * h, 2 * h);
        return new RebuildJob
        {
            Fn = Fit(s, s.S.FitPrior),
            Lo = lo - pad, Hi = hi + pad, H = h,
            GridFactor = s.S.RebuildGridFactor, RemeshIters = s.S.RebuildRemeshIters,
            Scale = s.RegionScale == null ? null : (double[])s.RegionScale.Clone(),
            CurrentVolume = m.Volumes(),
            What = what, Step = s.Steps,
            Old = s.S.TransferShape ? m.Clone() : null,
            ZoneAt = events == null ? new List<Vec3>() : events.Select(e => e.At).ToList(),
            ZoneR = events == null ? new List<double>() : events.Select(e => e.Radius).ToList(),
        };
    }
}

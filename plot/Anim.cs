using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

// Animation (Alex, 2026-10-29): a bubble cluster forms (bubbles blown in one by one, inflating), relaxes, then every
// cell is frozen into a rigid solid (its settled shape) and dropped onto a floor, where the cells tumble like stones
// and settle. The sequence plays forward and then in reverse back to the blank sheet, so it loops; output as MP4 and
// GIF. Every frame is also kept as a plotter SVG (fixed camera and framing) for a flip book later.
//   anim --seed N [--count 24] [--az deg] [--el deg] [--fps 24]      -> editions/anim_<seed>/
// Draft style: lines only (visible borders/edges + silhouettes, back edges faint), fast to render.
// Rigid bodies: each cell is a convex k-DOP (support planes in 50 directions) for collisions, sampled surface points
// as contact candidates, isotropic inertia of the equal-volume sphere, sequential impulses with friction. Cells start
// sharing walls, so each pair's starting overlap is allowed and shrinks as they separate.
public static partial class Edition
{
    struct Quat
    {
        public double W, X, Y, Z;
        public Quat(double w, double x, double y, double z) { W = w; X = x; Y = y; Z = z; }
        public static Quat Identity { get { return new Quat(1, 0, 0, 0); } }
        public Vec3 Rotate(Vec3 v)
        {
            var u = new Vec3(X, Y, Z);
            var t = Vec3.Cross(u, v) * 2;
            return v + t * W + Vec3.Cross(u, t);
        }
        public Vec3 InvRotate(Vec3 v) { return new Quat(W, -X, -Y, -Z).Rotate(v); }
        public Quat Integrate(Vec3 w, double dt)
        {
            double h = 0.5 * dt;
            var q = new Quat(W - h * (w.X * X + w.Y * Y + w.Z * Z), X + h * (w.X * W + w.Y * Z - w.Z * Y),
                             Y + h * (w.Y * W + w.Z * X - w.X * Z), Z + h * (w.Z * W + w.X * Y - w.Y * X));
            double n = Math.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            return new Quat(q.W / n, q.X / n, q.Y / n, q.Z / n);
        }
    }

    class Body
    {
        public int Id;
        public List<Vec3> LV = new List<Vec3>();          // local vertices (about the centroid)
        public List<int[]> Tris = new List<int[]>();      // outward
        public List<List<Vec3>> Creases = new List<List<Vec3>>();   // local polylines (former Plateau borders)
        public double[] H;                                 // k-DOP support values per direction
        public List<Vec3> Samples = new List<Vec3>();     // local contact candidates
        public double Mass, InvMass, InvI, R;
        public Vec3 X, V, W; public Quat Q = Quat.Identity;
    }

    class AnimFrame
    {
        public FoamMesh Foam;                              // foam phase (null in the rigid phase)
        public List<Vec3> Pos; public List<Quat> Rot;     // rigid phase
        public int FloorSteps;                             // floor draw-in progress (0..)
    }

    static List<Vec3> Fibonacci(int n)
    {
        var d = new List<Vec3>();
        double ga = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            double z = 1 - 2 * (i + 0.5) / n, r = Math.Sqrt(1 - z * z), a = ga * i;
            d.Add(new Vec3(r * Math.Cos(a), r * Math.Sin(a), z));
        }
        return d;
    }

    static FoamMesh CopyMesh(FoamMesh m)
    {
        var c = new FoamMesh { BubbleCount = m.BubbleCount };
        c.V.AddRange(m.V); c.T.AddRange(m.T);
        return c;
    }

    public static void Animate(int seed, int count, double azDeg, double elDeg, int fps, string dir, Action<string> log, string style = "lines", int from = -1, int to = -1)
    {
        Directory.CreateDirectory(dir);
        var rng = new Random(seed);
        var ci = CultureInfo.InvariantCulture;

        // ---------- 1. foam: blow the bubbles in one by one, outward from the middle ----------
        var r = new List<double>(); var c = new List<Vec3>();
        for (int i = 0; i < count; i++) r.Add(0.4 + 1.0 * Math.Pow(rng.NextDouble(), 1.8));
        double R0 = 0.9 * Math.Pow(count, 1.0 / 3) * r.Average();
        for (int i = 0; i < count; i++)
        {
            Vec3 p; do p = new Vec3(rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1); while (p.LengthSq > 1);
            c.Add(p * R0);
        }
        ClusterPacker.Pack(c, r, 1000, 0.01);
        var mid0 = c.Aggregate(Vec3.Zero, (a, b) => a + b) / count;
        var order = Enumerable.Range(0, count).OrderBy(i => (c[i] - mid0).Length).ToList();
        c = order.Select(i => c[i]).ToList(); r = order.Select(i => r[i]).ToList();
        double area = r.Sum(x => 4 * Math.PI * x * x);
        double h = Math.Max(r.Min() / 5, Math.Sqrt(area / (0.433 * 50000)));
        string info;
        double start = Math.Max(0.5 * r[0], 2.5 * h);
        var s = new FoamSolver(MobiusMesher.Build(new List<Vec3> { c[0] }, new List<double> { start }, h, out info),
                               new SolverSettings { Rearrange = true, EdgeLength = h });
        s.SetTargetRadii(new List<double> { start });
        var desired = new List<double> { r[0] };
        s.SetDesiredRadii(desired);
        var frames = new List<AnimFrame>();
        int every = 18, perAdd = 108;
        Action<int> run = steps =>
        {
            for (int i = 0; i < steps; i++)
            {
                s.Step(); s.RearrangeLog.Clear();
                if (s.Steps % every == 0) frames.Add(new AnimFrame { Foam = CopyMesh(s.Mesh) });
            }
        };
        var sw = Stopwatch.StartNew();
        run(perAdd);
        for (int i = 1; i < count; i++)
        {
            double r0 = Math.Max(0.5 * r[i], 2.5 * h);
            s.RequestAddBatch(new List<Vec3> { c[i] }, new List<double> { r0 }, "blow " + i);
            desired.Add(r[i]);
            // The new bubble's id is BubbleCount + 1 once the add lands; resize after it does.
            int before = s.Mesh.BubbleCount, guard = 0;
            while (s.Mesh.BubbleCount == before && guard++ < 400) { s.Step(); s.RearrangeLog.Clear(); if (s.Steps % every == 0) frames.Add(new AnimFrame { Foam = CopyMesh(s.Mesh) }); }
            var dr = new List<double>();
            for (int k = 1; k <= s.Mesh.BubbleCount; k++) dr.Add(k - 1 < desired.Count ? desired[k - 1] : r0);
            s.SetDesiredRadii(dr);
            run(perAdd);
            log(string.Format(ci, "  blown {0}/{1}: {2} bubbles, {3} tris, {4} frames, {5:F0} s", i + 1, count, s.Mesh.BubbleCount, s.Mesh.T.Count, frames.Count, sw.Elapsed.TotalSeconds));
        }
        run(every * 72);                                     // relax (3 s at 24 fps)
        Settle(s, log);
        frames.Add(new AnimFrame { Foam = CopyMesh(s.Mesh) });
        int foamFrames = frames.Count;

        // ---------- 2. freeze the cells into rigid bodies ----------
        var m = s.Mesh;
        var vol = m.Volumes();
        var dirs = Fibonacci(50);
        var bodies = new List<Body>();
        for (int k = 1; k <= m.BubbleCount; k++)
        {
            if (vol[k] <= 1e-9 || s.IsDead(k)) continue;
            var b = new Body { Id = k };
            var map = new Dictionary<int, int>();
            var film = new List<int>();
            foreach (var t in m.T)
            {
                if (FoamMesh.IsSolid(t) || (t.Neg != k && t.Pos != k)) continue;
                int[] vs = t.Neg == k ? new[] { t.A, t.B, t.C } : new[] { t.A, t.C, t.B };
                var tri = new int[3];
                for (int j = 0; j < 3; j++) { int li; if (!map.TryGetValue(vs[j], out li)) { li = b.LV.Count; map[vs[j]] = li; b.LV.Add(m.V[vs[j]]); } tri[j] = li; }
                b.Tris.Add(tri); film.Add(t.Neg == k ? t.Pos : t.Neg);
            }
            // Centroid (volume) and local coordinates.
            double V = 0; var mom = Vec3.Zero;
            foreach (var tr in b.Tris)
            {
                double v6 = Vec3.Dot(b.LV[tr[0]], Vec3.Cross(b.LV[tr[1]], b.LV[tr[2]])) / 6.0;
                V += v6; mom = mom + (b.LV[tr[0]] + b.LV[tr[1]] + b.LV[tr[2]]) * (v6 / 4);
            }
            var cen = mom / V;
            for (int j = 0; j < b.LV.Count; j++) b.LV[j] = b.LV[j] - cen;
            b.X = cen; b.Mass = V; b.InvMass = 1 / V; b.R = Math.Pow(3 * V / (4 * Math.PI), 1.0 / 3);
            b.InvI = 1 / (0.4 * V * b.R * b.R * 1.15);
            b.H = dirs.Select(d => b.LV.Max(p => Vec3.Dot(d, p))).ToArray();
            int stepS = Math.Max(1, b.LV.Count / 140);
            for (int j = 0; j < b.LV.Count; j += stepS) b.Samples.Add(b.LV[j]);
            foreach (var d in dirs) b.Samples.Add(b.LV.OrderByDescending(p => Vec3.Dot(d, p)).First());
            // Creases: edges between triangles of different films.
            var edgeFilm = new Dictionary<long, int>(LongKey.Comparer);
            var segs = new List<int[]>();
            for (int ti = 0; ti < b.Tris.Count; ti++)
                for (int ei = 0; ei < 3; ei++)
                {
                    int a = b.Tris[ti][ei], bb = b.Tris[ti][(ei + 1) % 3];
                    long key = FoamMesh.EdgeKey(a, bb);
                    int f0;
                    if (edgeFilm.TryGetValue(key, out f0)) { if (f0 != film[ti]) segs.Add(new[] { a, bb }); }
                    else edgeFilm[key] = film[ti];
                }
            b.Creases = ChainSegments(segs).Select(l => l.Select(i => b.LV[i]).ToList()).ToList();
            bodies.Add(b);
        }
        double zMin = m.V.Min(p => p.Z), zMax = m.V.Max(p => p.Z);
        double diam = Math.Max(zMax - zMin, Math.Max(m.V.Max(p => p.X) - m.V.Min(p => p.X), m.V.Max(p => p.Y) - m.V.Min(p => p.Y)));
        double floorZ = zMin - 0.35 * diam;
        var drop = new Vec3((m.V.Max(p => p.X) + m.V.Min(p => p.X)) / 2, (m.V.Max(p => p.Y) + m.V.Min(p => p.Y)) / 2, floorZ);
        log(string.Format(ci, "frozen {0} cells; floor {1:F2} below the cluster", bodies.Count, 0.35 * diam));

        // ---------- 3. drop them ----------
        double gap = 0.35 * diam, g = 5.5 * gap, dt = 1.0 / (fps * 25);
        var allow = new Dictionary<long, double>();
        Func<Body, Body, double> overlap = (A, B) =>
        {
            double mx = 0;
            foreach (var sp in A.Samples)
            {
                var pw = A.X + A.Q.Rotate(sp); var pl = B.Q.InvRotate(pw - B.X);
                double depth = double.MaxValue;
                for (int di = 0; di < dirs.Count; di++) depth = Math.Min(depth, B.H[di] - Vec3.Dot(dirs[di], pl));
                if (depth > mx) mx = depth;
            }
            return mx;
        };
        for (int i = 0; i < bodies.Count; i++)
            for (int j = i + 1; j < bodies.Count; j++)
                if ((bodies[i].X - bodies[j].X).Length < 2.6 * (bodies[i].R + bodies[j].R))
                    allow[((long)i << 32) | (uint)j] = Math.Max(overlap(bodies[i], bodies[j]), overlap(bodies[j], bodies[i])) + 1e-4;
        var clusterC = bodies.Aggregate(Vec3.Zero, (a, b) => a + b.X * b.Mass) / bodies.Sum(b => b.Mass);
        double bowl = 0.06 / diam;
        var jr = new Random(seed * 3 + 1);
        foreach (var b in bodies) b.W = new Vec3(jr.NextDouble() - 0.5, jr.NextDouble() - 0.5, jr.NextDouble() - 0.5) * 0.15;
        int still = 0, rigidFrames = 0;
        double maxR = bodies.Max(b => b.R) * 2.2;
        while (rigidFrames < fps * 10 && still < fps)
        {
            for (int sub = 0; sub < 25; sub++)
            {
                // Round 2 (Alex: the switch to the drop was "a little sudden and unnatural"): time since the freeze;
                // gravity eases in over 1.2 s while the cells drift apart (a smooth outward bump over the first 0.9 s),
                // and damping rises after 5 s so the pile comes to rest.
                double tt = (rigidFrames + sub / 25.0) / fps;
                double ge = tt < 1.2 ? Smoothstep(tt / 1.2) : 1;
                double push = tt < 0.9 ? Math.Sin(Math.PI * tt / 0.9) : 0;
                double damp = tt > 5 ? Math.Min(1.6, 0.3 + (tt - 5) * 0.35) : 0.3;   // round 3: lighter (the end was slow)
                foreach (var b in bodies)
                {
                    var outv = b.X - clusterC; double ol = outv.Length;
                    var acc = new Vec3(0, 0, -g * ge) + (ol > 1e-9 ? outv * (0.9 * gap * push / Math.Max(ol, 0.3 * diam)) : Vec3.Zero);
                    b.V = b.V + acc * dt; b.V = b.V * (1 - damp * dt); b.W = b.W * (1 - (1 + 3 * damp) * dt);
                }
                // Contacts: (body index A, body index B or -1 for the floor, point, normal pushing A, depth).
                var contacts = new List<Tuple<int, int, Vec3, Vec3, double>>();
                for (int i = 0; i < bodies.Count; i++)
                {
                    var A = bodies[i];
                    foreach (var sp in A.Samples)
                    {
                        var pw = A.X + A.Q.Rotate(sp);
                        // The (undrawn) floor is a very shallow bowl, so stones roll back toward the middle and stay in frame.
                        double dx = pw.X - drop.X, dy = pw.Y - drop.Y, fz = floorZ + bowl * (dx * dx + dy * dy);
                        if (pw.Z < fz) contacts.Add(Tuple.Create(i, -1, pw, new Vec3(-2 * bowl * dx, -2 * bowl * dy, 1).Normalized(), fz - pw.Z));
                    }
                    for (int j = 0; j < bodies.Count; j++)
                    {
                        if (j == i) continue;
                        var B = bodies[j];
                        if ((A.X - B.X).Length > A.R * 2.2 + B.R * 2.2) continue;
                        long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                        double al; allow.TryGetValue(key, out al);
                        double worst = 0;
                        foreach (var sp in A.Samples)
                        {
                            var pw = A.X + A.Q.Rotate(sp); var pl = B.Q.InvRotate(pw - B.X);
                            double depth = double.MaxValue; int dmin = 0;
                            for (int di = 0; di < dirs.Count; di++) { double dd = B.H[di] - Vec3.Dot(dirs[di], pl); if (dd < depth) { depth = dd; dmin = di; } }
                            if (depth > worst) worst = depth;
                            if (depth > al) contacts.Add(Tuple.Create(i, j, pw, B.Q.Rotate(dirs[dmin]), depth - al));
                        }
                        if (al > 0 && worst < al) allow[key] = Math.Max(worst, 0);   // they separated a little: tighten
                    }
                }
                var pairN = new Dictionary<long, int>();
                foreach (var ct in contacts) { long pk = ((long)ct.Item1 << 32) | (uint)(ct.Item2 + 1); int cn; pairN.TryGetValue(pk, out cn); pairN[pk] = cn + 1; }
                Func<int, int, int> cnt = (a, bb) => pairN[((long)a << 32) | (uint)(bb + 1)];
                for (int it = 0; it < 6; it++)
                    foreach (var ct in contacts)
                    {
                        var A = bodies[ct.Item1]; var B = ct.Item2 >= 0 ? bodies[ct.Item2] : null;
                        var n = ct.Item4; var p = ct.Item3;
                        var ra = p - A.X; var rb = B != null ? p - B.X : Vec3.Zero;
                        var va = A.V + Vec3.Cross(A.W, ra); var vb = B != null ? B.V + Vec3.Cross(B.W, rb) : Vec3.Zero;
                        var vrel = va - vb; double vn = Vec3.Dot(vrel, n);
                        if (vn >= 0) continue;
                        double km = A.InvMass + A.InvI * Vec3.Cross(ra, n).LengthSq + (B != null ? B.InvMass + B.InvI * Vec3.Cross(rb, n).LengthSq : 0);
                        double jn = -(1 + 0.08) * vn / km / Math.Max(1, cnt(ct.Item1, ct.Item2) * 0.5);
                        var J = n * jn;
                        var vt = vrel - n * vn; double vtl = vt.Length;
                        if (vtl > 1e-9)
                        {
                            var tdir = vt / vtl;
                            double kt = A.InvMass + A.InvI * Vec3.Cross(ra, tdir).LengthSq + (B != null ? B.InvMass + B.InvI * Vec3.Cross(rb, tdir).LengthSq : 0);
                            double jt = Math.Min(vtl / kt, 0.85 * jn);
                            J = J - tdir * jt;
                        }
                        A.V = A.V + J * A.InvMass; A.W = A.W + Vec3.Cross(ra, J) * A.InvI;
                        if (B != null) { B.V = B.V - J * B.InvMass; B.W = B.W - Vec3.Cross(rb, J) * B.InvI; }
                    }
                // Position correction (split by inverse mass), then integrate.
                foreach (var ct in contacts)
                {
                    var A = bodies[ct.Item1]; var B = ct.Item2 >= 0 ? bodies[ct.Item2] : null;
                    double corr = 0.25 * ct.Item5 / Math.Max(1, cnt(ct.Item1, ct.Item2));
                    double wa = B != null ? A.InvMass / (A.InvMass + B.InvMass) : 1;
                    A.X = A.X + ct.Item4 * (corr * wa);
                    if (B != null) B.X = B.X - ct.Item4 * (corr * (1 - wa));
                }
                foreach (var b in bodies) { b.X = b.X + b.V * dt; b.Q = b.Q.Integrate(b.W, dt); }
            }
            frames.Add(new AnimFrame { Pos = bodies.Select(b => b.X).ToList(), Rot = bodies.Select(b => b.Q).ToList(), FloorSteps = rigidFrames + 1 });
            rigidFrames++;
            double ke = bodies.Max(b => b.V.Length + b.W.Length * b.R);
            still = rigidFrames > fps && ke < 0.02 * gap ? still + 1 : 0;
            if (rigidFrames % fps == 0) log(string.Format(ci, "  drop: {0} frames, max speed {1:F3}, {2:F0} s", rigidFrames, ke, sw.Elapsed.TotalSeconds));
        }
        log(string.Format(ci, "simulated: {0} foam frames + {1} rigid frames", foamFrames, rigidFrames));

        // ---------- 4. render ----------
        var view = MakeView(seed, azDeg, elDeg);
        double floorHalf = 1.25 * diam;
        var floorCorners = new[] { new Vec3(-1, -1, 0), new Vec3(1, -1, 0), new Vec3(1, 1, 0), new Vec3(-1, 1, 0) }
            .Select(q => drop + new Vec3(q.X * floorHalf, q.Y * floorHalf, 0)).ToList();
        var fit = new List<Vec3>();
        foreach (var f in frames)
        {
            if (f.Foam != null) { fit.Add(new Vec3(f.Foam.V.Min(p => p.X), f.Foam.V.Min(p => p.Y), f.Foam.V.Min(p => p.Z))); fit.Add(new Vec3(f.Foam.V.Max(p => p.X), f.Foam.V.Max(p => p.Y), f.Foam.V.Max(p => p.Z))); }
            else for (int i = 0; i < bodies.Count; i++) { fit.Add(f.Pos[i] + new Vec3(bodies[i].R, bodies[i].R, bodies[i].R) * 1.3); fit.Add(f.Pos[i] - new Vec3(bodies[i].R, bodies[i].R, bodies[i].R) * 1.3); }
        }
        var e = new EditionSettings { SheetW = 160, SheetH = 120, Seed = seed };
        string fdir = Path.Combine(dir, "frames"), sdir = Path.Combine(dir, "svg");
        Directory.CreateDirectory(fdir); Directory.CreateDirectory(sdir);
        bool mist = style == "mist";
        var weights = mist ? new Dictionary<string, double> { { "inner", 0.6 }, { "innerborders", 0.55 }, { "ground", 0.85 } }
                           : new Dictionary<string, double> { { "innerborders", 0.35 }, { "floor", 0.5 } };
        StippleCtx cx = null;
        if (mist)
        {
            // Camera scale is fixed (same fit every frame): measure it once, then build the ranked dot sets.
            var probe = Scene.Surface(frames[foamFrames - 1].Foam, 0);
            probe.SetCamera(view.Forward, 0.05 * e.SheetW, 0.05 * e.SheetH, 0.9 * e.SheetW, 0.9 * e.SheetH, fit);
            cx = new StippleCtx
            {
                Skin = RankedField(e.SheetW, e.SheetH, 0.30, 4.5, seed + 1),
                Inner = RankedField(e.SheetW, e.SheetH, 0.375, 4.5, seed + 2),
                Ground = RankedField(e.SheetW, e.SheetH, 0.32, 4.5, seed + 3),
                BodyDots = bodies.Select((b, i) => RankedSurface(b.LV, b.Tris, 0.30 / probe.S, seed * 100 + i)).ToList(),
                FloorZ = floorZ,
                KeyLight = (probe.R * -0.72 + probe.U * 0.5 + probe.F * -0.48).Normalized(),
                ShadowLight = ((probe.R * -0.72 + probe.U * 0.5 + probe.F * -0.48).Normalized() + new Vec3(0, 0, 2.2)).Normalized(),
            };
            log(string.Format(ci, "dot sets: skin {0}, inner {1}, ground {2}, stones {3}", cx.Skin.Count, cx.Inner.Count, cx.Ground.Count, cx.BodyDots.Sum(l => l.Count)));
        }
        int fiFrom = from >= 0 ? from : 0, fiTo = to >= 0 ? Math.Min(to, frames.Count - 1) : frames.Count - 1;
        for (int fi = fiFrom; fi <= fiTo; fi++)
        {
            var f = frames[fi];
            FoamMesh fm;
            List<List<Vec3>> creases = null;
            if (f.Foam != null) fm = f.Foam;
            else
            {
                fm = new FoamMesh { BubbleCount = bodies.Count };
                creases = new List<List<Vec3>>();
                for (int i = 0; i < bodies.Count; i++)
                {
                    var b = bodies[i]; int off = fm.V.Count;
                    foreach (var lv in b.LV) fm.V.Add(f.Pos[i] + f.Rot[i].Rotate(lv));
                    foreach (var t in b.Tris) fm.T.Add(new FoamTri(off + t[0], off + t[1], off + t[2], i + 1, 0));
                    foreach (var cl in b.Creases) creases.Add(cl.Select(lv => f.Pos[i] + f.Rot[i].Rotate(lv)).ToList());
                }
            }
            var p = new Plot { Width = e.SheetW, Height = e.SheetH, PenWidth = 0.3 };
            var sc = Scene.Surface(fm, 0);
            sc.SetCamera(view.Forward, 0.05 * p.Width, 0.05 * p.Height, 0.9 * p.Width, 0.9 * p.Height, fit);
            Light(sc);
            sc.Rasterize(e.RasterPxPerMm, p.Width, p.Height);
            if (mist)
            {
                Lighting.Setup(sc, cx.KeyLight, new LightSettings { AoDirections = 24, ShadowRes = 1000 });
                int fadeN = fps * 3 / 4;
                double foamFade = f.Foam != null ? Math.Min(1, Math.Max(0, (foamFrames - 1 - fi) / (double)fadeN)) : 0;
                double bodyMix = f.Foam != null ? 0 : Math.Min(1, (fi - foamFrames + 1) / (double)(fps / 2));
                StippleFrame(p, sc, fm, cx, h, f.Foam != null, creases, f.Pos, f.Rot, foamFade, bodyMix, fi);
            }
            else if (f.Foam != null)
            {
                var borders = BorderClean.Clean(fm, h, new BorderCleanSettings { Spacing = h * 0.5, IncludeHost = false });
                var outer = new List<List<Vec3>>(); var all = new List<List<Vec3>>();
                foreach (var bd in borders)
                {
                    var pts = new List<Vec3>(bd.Points);
                    if (bd.Closed && pts.Count > 0) pts.Add(pts[0]);
                    all.Add(pts);
                    if (bd.Bubbles.Contains(0)) outer.Add(pts);
                }
                p.Layer("outline").AddRange(Hatcher.VisibleRuns(sc, outer, 4 / (sc.S * sc.Res)));
                p.Layer("innerborders").AddRange(Hatcher.VisibleRuns(sc, all, 1.2 / sc.S, 0.6, true).Select(Smooth2));
            }
            else
            {
                p.Layer("outline").AddRange(Hatcher.VisibleRuns(sc, creases, 4 / (sc.S * sc.Res)));
                p.Layer("innerborders").AddRange(Hatcher.VisibleRuns(sc, creases, 1.2 / sc.S, 0.6, true));
                // No floor drawn (round 2): the ground will come from shadows in the final style.
            }
            if (!mist && sc.Tris.Count > 0) foreach (var run2 in Hatcher.VisibleRuns(sc, sc.Silhouettes(), 1.0 / sc.S, 0.6)) p.Layer("outline").Add(Smooth2(run2));
            SheetIO.Optimize(p);
            SheetIO.WritePng(p, Path.Combine(fdir, string.Format("f{0:0000}.png", fi)), 8, weights);
            SheetIO.WriteSvg(p, Path.Combine(sdir, string.Format("f{0:0000}.svg", fi)), "Bubble animation " + seed + " frame " + fi);
            if (fi % 40 == 0) log(string.Format(ci, "  rendered {0}/{1} ({2:F0} s)", fi, frames.Count, sw.Elapsed.TotalSeconds));
        }
        var blank = new Plot { Width = e.SheetW, Height = e.SheetH };
        SheetIO.WritePng(blank, Path.Combine(fdir, "blank.png"), 8, weights);
        Func<string, int> ffr = a =>
        {
            var psi = new ProcessStartInfo("ffmpeg", a) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            var pr = Process.Start(psi); pr.StandardError.ReadToEnd(); pr.WaitForExit(); return pr.ExitCode;
        };
        if (from >= 0 || to >= 0)
        {
            // A test segment: just these frames, forward, as an MP4.
            string tmp = Path.Combine(dir, "segment"); if (Directory.Exists(tmp)) Directory.Delete(tmp, true); Directory.CreateDirectory(tmp);
            for (int fi = fiFrom; fi <= fiTo; fi++) File.Copy(Path.Combine(fdir, string.Format("f{0:0000}.png", fi)), Path.Combine(tmp, string.Format("{0:00000}.png", fi - fiFrom)));
            string seg = Path.Combine(dir, string.Format("segment_{0}-{1}_{2}.mp4", fiFrom, fiTo, style));
            int xs = ffr(string.Format(ci, "-y -framerate {0} -i \"{1}\" -c:v libx264 -pix_fmt yuv420p -crf 18 \"{2}\"", fps, Path.Combine(tmp, "%05d.png"), seg));
            log("segment " + fiFrom + "-" + fiTo + " (foam frames end at " + (foamFrames - 1) + "): " + seg + " exit " + xs);
            return;
        }

        // ---------- 5. sequence: blank, forward, hold, reverse, blank (loops) ----------
        string qdir = Path.Combine(dir, "seq");
        if (Directory.Exists(qdir)) Directory.Delete(qdir, true);
        Directory.CreateDirectory(qdir);
        var seq = new List<string>();
        for (int i = 0; i < fps / 2; i++) seq.Add("blank.png");
        for (int i = 0; i < frames.Count; i++) seq.Add(string.Format("f{0:0000}.png", i));
        for (int i = 0; i < fps; i++) seq.Add(string.Format("f{0:0000}.png", frames.Count - 1));
        for (int i = frames.Count - 2; i >= 0; i--) seq.Add(string.Format("f{0:0000}.png", i));
        for (int i = 0; i < seq.Count; i++) File.Copy(Path.Combine(fdir, seq[i]), Path.Combine(qdir, string.Format("{0:00000}.png", i)), true);
        Func<string, int> ff = a =>
        {
            var psi = new ProcessStartInfo("ffmpeg", a) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            var pr = Process.Start(psi); pr.StandardError.ReadToEnd(); pr.WaitForExit(); return pr.ExitCode;
        };
        string mp4 = Path.Combine(dir, "anim_" + seed + ".mp4"), gif = Path.Combine(dir, "anim_" + seed + ".gif");
        int x1 = ff(string.Format(ci, "-y -framerate {0} -i \"{1}\" -c:v libx264 -pix_fmt yuv420p -crf 18 \"{2}\"", fps, Path.Combine(qdir, "%05d.png"), mp4));
        int x2 = ff(string.Format(ci, "-y -framerate {0} -i \"{1}\" -vf \"fps=12,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse\" -loop 0 \"{2}\"", fps, Path.Combine(qdir, "%05d.png"), gif));
        log(string.Format(ci, "sequence {0} frames ({1:F1} s); mp4 exit {2}, gif exit {3}", seq.Count, seq.Count / (double)fps, x1, x2));
    }

    static double Smoothstep(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }

    static int ContactsOf(List<Tuple<int, int, Vec3, Vec3, double>> cs, int a, int b)
    {
        int n = 0; foreach (var c in cs) if (c.Item1 == a && c.Item2 == b) n++; return n;
    }

    static List<List<int>> ChainSegments(List<int[]> segs)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (var sg in segs)
        {
            List<int> l;
            if (!adj.TryGetValue(sg[0], out l)) adj[sg[0]] = l = new List<int>(); l.Add(sg[1]);
            if (!adj.TryGetValue(sg[1], out l)) adj[sg[1]] = l = new List<int>(); l.Add(sg[0]);
        }
        var used = new HashSet<long>();
        var res = new List<List<int>>();
        foreach (var st in adj.Keys.OrderBy(k => adj[k].Count == 2 ? 1 : 0))
            foreach (var nb in adj[st])
            {
                if (used.Contains(FoamMesh.EdgeKey(st, nb))) continue;
                var line = new List<int> { st }; int prev = st, cur = nb;
                used.Add(FoamMesh.EdgeKey(st, nb));
                while (true)
                {
                    line.Add(cur);
                    if (adj[cur].Count != 2) break;
                    int nx = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
                    if (used.Contains(FoamMesh.EdgeKey(cur, nx))) break;
                    used.Add(FoamMesh.EdgeKey(cur, nx)); prev = cur; cur = nx;
                }
                res.Add(line);
            }
        return res;
    }
}

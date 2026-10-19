using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

// Headless runs of the bubble core.
//   dotnet run -- bench            benchmarks with known answers (double/triple/quad + a random cluster)
//   dotnet run -- obj <file.obj>   report on a recorded film mesh (e.g. from a GH run folder)
//   dotnet run -- replay <runDir>  re-run a GH recording from its inputs.txt
public static class Program
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "bench";
        if (cmd == "obj")
        {
            var m = FoamAnalysis.FromObj(File.ReadAllText(args[1]));
            var s = new FoamSolver(m, null);
            s.Area = m.TotalArea();
            Console.WriteLine(FoamAnalysis.Report(s));
            return 0;
        }
        if (cmd == "debug")
        {
            var clock = Stopwatch.StartNew();
            MobiusMesher.Debug = (stage, mm) =>
            {
                Console.Write(string.Format(CI, "[{0,6} ms] ", clock.ElapsedMilliseconds));
                clock.Restart();
                var h = Remesher.EdgeHistogram(mm);
                int folds = 0;
                foreach (var kv in mm.EdgeTris())
                    if (kv.Value.Count >= 3 && kv.Value.Select(ti => FoamMesh.EdgeKey(mm.T[ti].Neg, mm.T[ti].Pos)).Distinct().Count() < kv.Value.Count) folds++;
                Console.Write("folds " + folds + "  ");
                var vol = mm.Volumes();
                Console.WriteLine(string.Format(CI, "{0,-9} tris {1,6} open {2,4} film {3,6} border {4,4} 4+ {5,3}  vol {6:F4} {7:F4} area {8:F4}",
                    stage, mm.T.Count, h[1], h[2], h[3], h[4] + h[5], vol[1], vol.Length > 2 ? vol[2] : 0, mm.TotalArea()));
            };
            var rnd = new Random(7);
            var c = new List<Vec3>(); var r = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                c.Add(new Vec3(rnd.NextDouble() * 2.4, rnd.NextDouble() * 2.4, rnd.NextDouble() * 1.6));
                r.Add(0.5 + rnd.NextDouble() * 0.6);
            }
            ClusterPacker.Pack(c, r, 1000, 0.01);
            string info;
            clock.Restart();
            MobiusMesher.Build(c, r, 0.08, out info);
            return 0;
        }
        if (cmd == "tune")
        {
            double rA = 1, rB = 0.7, d = Math.Sqrt(1 + 0.49 - 0.7) * 0.8;
            foreach (var mom in new[] { 0.0, 0.8, 0.9, 0.95 })
            {
                string info;
                var mesh = MobiusMesher.Build(new List<Vec3> { new Vec3(0, 0, 0), new Vec3(d, 0, 0) }, new List<double> { rA, rB }, 0.1, out info);
                var s = new FoamSolver(mesh, new SolverSettings { Momentum = mom });
                var sw = Stopwatch.StartNew();
                Console.Write("momentum " + mom + ":");
                for (int i = 1; i <= 4000; i++)
                {
                    s.Step();
                    if (i == 250 || i == 500 || i == 1000 || i == 2000 || i == 4000)
                    {
                        Vec3 c; double fa, fb, fc;
                        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 1), out c, out fa);
                        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 2), out c, out fb);
                        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 1, 2), out c, out fc);
                        double g = fa * fb / (fa - fb);
                        Console.Write(string.Format(CI, "  [{0}] wall err {1:P1} area {2:F5}", i, Math.Abs(fc - g) / g, s.Area));
                    }
                }
                Console.WriteLine(string.Format(CI, "  ({0} ms)", sw.ElapsedMilliseconds));
            }
            return 0;
        }
        if (cmd == "borders")
        {
            double a = 0.62;
            var c = new List<Vec3> { new Vec3(a, a, a), new Vec3(a, -a, -a), new Vec3(-a, a, -a), new Vec3(-a, -a, a) };
            string info;
            var m = MobiusMesher.Build(c, new List<double> { 1, 1, 1, 1 }, 0.12, out info);
            Console.WriteLine(info);
            var kinds = new Dictionary<string, int>();
            foreach (var kv in m.EdgeTris())
            {
                if (kv.Value.Count < 3) continue;
                var pairs = kv.Value.Select(ti => Math.Min(m.T[ti].Neg, m.T[ti].Pos) + "" + Math.Max(m.T[ti].Neg, m.T[ti].Pos)).OrderBy(x => x);
                string k = string.Join(",", pairs);
                kinds[k] = kinds.ContainsKey(k) ? kinds[k] + 1 : 1;
            }
            foreach (var kv in kinds.OrderByDescending(x => x.Value)) Console.WriteLine(kv.Key + "  " + kv.Value);
            var vr = m.VertexRegions();
            Console.WriteLine("vertices touching 4+ regions: " + vr.Count(r => r.Count >= 4) + ", 5 regions: " + vr.Count(r => r.Count >= 5));
            return 0;
        }
        if (cmd == "trace")
        {
            var c = new List<Vec3>(); var r = new List<double>(); double h = 0.1;
            foreach (var line in File.ReadAllLines(Path.Combine(args[1], "inputs.txt")))
            {
                var p = line.Split(' ');
                if (p[0] == "cell") h = double.Parse(p[1], CI);
                if (p[0] == "bubble") { c.Add(new Vec3(double.Parse(p[1], CI), double.Parse(p[2], CI), double.Parse(p[3], CI))); r.Add(double.Parse(p[4], CI)); }
            }
            string info;
            var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info), new SolverSettings { AsyncRearrange = Environment.GetEnvironmentVariable("ASYNC") != null });
            s.SetTargetRadii(r);
            double worstStepMs = 0; int worstStep = 0;
            var total = Stopwatch.StartNew();
            for (int i = 1; i <= int.Parse(args[2]); i++)
            {
                var stepClock = Stopwatch.StartNew();
                s.Step();
                if (stepClock.Elapsed.TotalMilliseconds > worstStepMs) { worstStepMs = stepClock.Elapsed.TotalMilliseconds; worstStep = i; }
                if (stepClock.Elapsed.TotalMilliseconds > 250)
                    Console.WriteLine(string.Format(CI, "   SLOW step {0}: {1:F0} ms{2}{3}{4}", i, stepClock.Elapsed.TotalMilliseconds,
                        s.LastRemesh != null ? " [remesh]" : "", (i - 1) % s.S.MaintainEvery == 0 ? " [maintain]" : "",
                        (i - 1) % s.S.RearrangeEvery == 0 ? " [rearrange check]" : ""));
                foreach (var l in s.RearrangeLog) Console.WriteLine("   REARRANGE " + l);
                s.RearrangeLog.Clear();
                string rm = s.LastRemesh != null ? string.Format(" REMESH s{0}/c{1}/f{2}", s.LastRemesh.Splits, s.LastRemesh.Collapses, s.LastRemesh.Flips) : "";
                s.LastRemesh = null;
                if (i > int.Parse(args[3]))
                    Console.WriteLine(string.Format(CI, "{0} area {1:F6} resid {2:F3} tris {3}{4}  max dev {5:F1}", i, s.Area, s.Residual, s.Mesh.T.Count, rm, FoamAnalysis.BorderAngles(s.Mesh).MaxAbsDev));
            }
            Console.WriteLine(string.Format(CI, "total {0:F1} s, slowest step {1:F0} ms (step {2}), rearrangements {3}", total.Elapsed.TotalSeconds, worstStepMs, worstStep, s.Rearrangements));
            return 0;
        }
        if (cmd == "worst")
        {
            var c = new List<Vec3>(); var r = new List<double>(); double h = 0.1;
            foreach (var line in File.ReadAllLines(Path.Combine(args[1], "inputs.txt")))
            {
                var p = line.Split(' ');
                if (p[0] == "cell") h = double.Parse(p[1], CI);
                if (p[0] == "bubble") { c.Add(new Vec3(double.Parse(p[1], CI), double.Parse(p[2], CI), double.Parse(p[3], CI))); r.Add(double.Parse(p[4], CI)); }
            }
            string info;
            var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info), null);
            s.SetTargetRadii(r);
            int n = int.Parse(args[2]);
            for (int i = 0; i < n; i++) s.Step();
            var m = s.Mesh;
            Console.WriteLine(FoamAnalysis.Report(s));
            foreach (var ev in Rearranger.Detect(s)) Console.WriteLine("  event now: " + ev);
            {
                int folds = 0;
                foreach (var kv in m.EdgeTris())
                {
                    if (kv.Value.Count < 3) continue;
                    var pairs = kv.Value.Select(ti => FoamMesh.EdgeKey(m.T[ti].Neg, m.T[ti].Pos)).Distinct().Count();
                    if (pairs < kv.Value.Count) folds++;
                }
                Console.WriteLine("  fold edges (border edge with a repeated film): " + folds);
            }
            var et = m.EdgeTris();
            {
                var key = FoamMesh.EdgeKey(0, 1);
                Console.WriteLine("edge 308-319 tris: " + (et.ContainsKey(key) ? string.Join(",", et[key].Select(ti => ti + "(" + m.T[ti].A + "," + m.T[ti].B + "," + m.T[ti].C + " " + m.T[ti].Neg + "|" + m.T[ti].Pos + ")")) : "none"));
                foreach (var kv in et) if ((int)(kv.Key >> 32) == 318 || (int)(kv.Key & 0xffffffff) == 318)
                    Console.WriteLine("  edge at 318: " + (kv.Key >> 32) + "-" + (kv.Key & 0xffffffff) + " ntris " + kv.Value.Count);
            }
            for (int rep = 0; rep < 3; rep++)
            {
                double worst = 0; long wk = 0;
                foreach (var kv in et)
                {
                    if (kv.Value.Count != 3) continue;
                    int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                    Vec3 e = (m.V[b] - m.V[a]).Normalized(); Vec3 mid = (m.V[a] + m.V[b]) * 0.5;
                    var dirs = kv.Value.Select(ti => { var t = m.T[ti]; int o = t.A != a && t.A != b ? t.A : (t.B != a && t.B != b ? t.B : t.C); Vec3 d = m.V[o] - mid; return (d - e * Vec3.Dot(d, e)).Normalized(); }).ToList();
                    double dev = 0;
                    for (int x = 0; x < 3; x++) for (int y = x + 1; y < 3; y++) dev = Math.Max(dev, Math.Abs(Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(dirs[x], dirs[y])))) * 180 / Math.PI - 120));
                    if (dev > worst) { worst = dev; wk = kv.Key; }
                }
                int wa = (int)(wk >> 32), wb = (int)(wk & 0xffffffff);
                Console.WriteLine(string.Format(CI, "step {0}: worst border edge {1}-{2} dev {3:F1}, len {4:F4} (L {5:F4}), classes {6},{7}",
                    s.Steps, wa, wb, worst, (m.V[wa] - m.V[wb]).Length, s.EdgeLength, s.VertexClass(wa), s.VertexClass(wb)));
                foreach (int ti in et[wk])
                {
                    var t = m.T[ti];
                    int o = t.A != wa && t.A != wb ? t.A : (t.B != wa && t.B != wb ? t.B : t.C);
                    Console.WriteLine(string.Format(CI, "   tri {0} films {1}|{2} area {3:E2} opp v{4} class {5} dist to edge {6:F4}",
                        ti, t.Neg, t.Pos, m.TriArea(t), o, s.VertexClass(o), Vec3.Cross(m.V[o] - m.V[wa], (m.V[wb] - m.V[wa]).Normalized()).Length));
                }
                s.Step();
            }
            return 0;
        }
        if (cmd == "perf")
        {
            var c = new List<Vec3>(); var r = new List<double>(); double h = 0.1;
            foreach (var line in File.ReadAllLines(Path.Combine(args[1], "inputs.txt")))
            {
                var p = line.Split(' ');
                if (p[0] == "cell") h = double.Parse(p[1], CI);
                if (p[0] == "bubble") { c.Add(new Vec3(double.Parse(p[1], CI), double.Parse(p[2], CI), double.Parse(p[3], CI))); r.Add(double.Parse(p[4], CI)); }
            }
            string info;
            var sw = Stopwatch.StartNew();
            var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info), new SolverSettings { RemeshEvery = Environment.GetEnvironmentVariable("NOREMESH") != null ? 0 : 10 });
            s.SetTargetRadii(r);
            Console.WriteLine("build " + sw.ElapsedMilliseconds + " ms, " + s.Mesh.T.Count + " tris");
            int n = args.Length > 2 ? int.Parse(args[2]) : 100;
            s.Step();
            sw.Restart();
            for (int i = 0; i < n; i++) s.Step();
            Console.WriteLine(string.Format(CI, "{0} steps: {1:F2} ms/step, final area {2:F6}", n, sw.ElapsedMilliseconds / (double)n, s.Area));
            sw.Restart();
            for (int i = 0; i < 20; i++) s.CorrectVolumes();
            Console.WriteLine(string.Format(CI, "  CorrectVolumes {0:F2} ms", sw.ElapsedMilliseconds / 20.0));
            sw.Restart();
            for (int i = 0; i < 20; i++) { var a = s.Mesh.TotalArea(); var v = s.Mesh.Volumes(); }
            Console.WriteLine(string.Format(CI, "  area+volumes {0:F2} ms", sw.ElapsedMilliseconds / 20.0));
            sw.Restart();
            for (int i = 0; i < 20; i++) s.NeedsRemesh();
            Console.WriteLine(string.Format(CI, "  NeedsRemesh {0:F2} ms", sw.ElapsedMilliseconds / 20.0));
            sw.Restart();
            for (int i = 0; i < 5; i++) FoamAnalysis.Report(s);
            Console.WriteLine(string.Format(CI, "  Report {0:F1} ms", sw.Elapsed.TotalMilliseconds / 5));
            sw.Restart();
            List<int> nds;
            for (int i = 0; i < 5; i++) FoamAnalysis.BorderChains(s.Mesh, out nds);
            Console.WriteLine(string.Format(CI, "  BorderChains {0:F1} ms", sw.Elapsed.TotalMilliseconds / 5));
            sw.Restart();
            s.Remesh();
            Console.WriteLine(string.Format(CI, "  Remesh (full) {0:F0} ms", sw.Elapsed.TotalMilliseconds));
            sw.Restart();
            s.Maintain();
            Console.WriteLine(string.Format(CI, "  Maintain {0:F0} ms", sw.Elapsed.TotalMilliseconds));
            sw.Restart();
            for (int i = 0; i < 5; i++) Remesher.FlipDelaunay(s.Mesh);
            Console.WriteLine(string.Format(CI, "  FlipDelaunay {0:F2} ms", sw.ElapsedMilliseconds / 5.0));
            return 0;
        }
        if (cmd == "remeshperf")
        {
            // Building blocks of the remesher on a relaxed fixture mesh (where rebuild time goes).
            List<double> rr;
            var s = LoadFixture(args[1], out rr, new SolverSettings { Rearrange = false });
            for (int i = 0; i < 100; i++) s.Step();
            var m = s.Mesh;
            Console.WriteLine("mesh " + m.V.Count + " verts, " + m.T.Count + " tris");
            Action<string, Action> time = (label, a) =>
            {
                a(); var sw = Stopwatch.StartNew(); for (int i = 0; i < 5; i++) a();
                Console.WriteLine(string.Format(CI, "  {0,-28} {1,8:F1} ms", label, sw.Elapsed.TotalMilliseconds / 5));
            };
            time("EdgeTris", () => m.EdgeTris());
            time("VertexRegions", () => m.VertexRegions());
            time("BorderVertices", () => Remesher.BorderVertices(m, m.EdgeTris()));
            time("Clean", () => m.Clone().Clean());
            double L = s.TargetEdge;
            time("SplitLong (1 pass, copy)", () => Remesher.SplitLong(m.Clone(), 4.0 / 3.0 * L, true, s.RegionScale, 1));
            time("CollapseShort (1 pass, copy)", () => Remesher.CollapseShort(m.Clone(), 0.8 * L, 4.0 / 3.0 * L, s.RegionScale, 1));
            time("FlipDelaunay (1 pass, copy)", () => Remesher.FlipDelaunay(m.Clone(), 1));
            time("Remesher.Run (1 it, copy)", () => Remesher.Run(m.Clone(), L, 1, s.RegionScale, 6));
            return 0;
        }
        if (cmd == "fit")
        {
            var c = new List<Vec3>(); var r = new List<double>(); double h = 0.1;
            foreach (var line in File.ReadAllLines(Path.Combine(args[1], "inputs.txt")))
            {
                var p = line.Split(' ');
                if (p[0] == "cell") h = double.Parse(p[1], CI);
                if (p[0] == "bubble") { c.Add(new Vec3(double.Parse(p[1], CI), double.Parse(p[2], CI), double.Parse(p[3], CI))); r.Add(double.Parse(p[4], CI)); }
            }
            string info;
            var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info), new SolverSettings { Rearrange = false });
            s.SetTargetRadii(r);
            int n = int.Parse(args[2]);
            for (int i = 0; i < n; i++) s.Step();
            var fn = Rearranger.Fit(s, s.S.FitPrior);
            var m = s.Mesh;
            // Per bubble: fitted function value on its own outer film (should be ~0) and at its centroid (should be < 0).
            for (int k = 1; k <= m.BubbleCount; k++)
            {
                double outerRms = 0; int no = 0; double wallRms = 0; int nw = 0;
                Vec3 cen = Vec3.Zero; int nc = 0;
                foreach (var t in m.T)
                {
                    if (t.Neg != k && t.Pos != k) continue;
                    Vec3 x = (m.V[t.A] + m.V[t.B] + m.V[t.C]) / 3;
                    cen = cen + x; nc++;
                    int other = t.Neg == k ? t.Pos : t.Neg;
                    if (other == 0) { double f = fn.F(k, x); outerRms += f * f; no++; }
                    else { double f = fn.F(k, x) - fn.F(other, x); wallRms += f * f; nw++; }
                }
                cen = cen / nc;
                Console.WriteLine(string.Format(CI, "bubble {0,2}: A {1,8:F3} |B| {2,7:F3} D {3,8:F3}  outer rms {4:F4} (n{5}) wall rms {6:F4} (n{7})  f(centroid) {8:F3}  p {9:F2}",
                    k, fn.A[k - 1], fn.B[k - 1].Length, fn.D[k - 1], Math.Sqrt(outerRms / Math.Max(1, no)), no, Math.Sqrt(wallRms / Math.Max(1, nw)), nw, fn.F(k, cen), s.Pressure[k]));
            }
            var lo = new Vec3(-5, -5, -5); var hi = new Vec3(5, 5, 5);
            foreach (var p in m.V) { }
            string inf2;
            Vec3 l2 = new Vec3(m.V.Min(p => p.X), m.V.Min(p => p.Y), m.V.Min(p => p.Z)), h2 = new Vec3(m.V.Max(p => p.X), m.V.Max(p => p.Y), m.V.Max(p => p.Z));
            var rebuilt = MobiusMesher.Build(fn, l2 - new Vec3(0.2, 0.2, 0.2), h2 + new Vec3(0.2, 0.2, 0.2), s.TargetEdge, out inf2);
            var v0 = m.Volumes(); var v1 = rebuilt.Volumes();
            Console.WriteLine("rebuilt volumes / current: " + string.Join(" ", Enumerable.Range(1, m.BubbleCount).Select(k => (v1[k] / v0[k]).ToString("F2", CI))));
            return 0;
        }
        if (cmd == "rebuildperf")
        {
            var c = new List<Vec3>(); var r = new List<double>(); double h = 0.1;
            foreach (var line in File.ReadAllLines(Path.Combine(args[1], "inputs.txt")))
            {
                var p = line.Split(' ');
                if (p[0] == "cell") h = double.Parse(p[1], CI);
                if (p[0] == "bubble") { c.Add(new Vec3(double.Parse(p[1], CI), double.Parse(p[2], CI), double.Parse(p[3], CI))); r.Add(double.Parse(p[4], CI)); }
            }
            string info;
            var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info), new SolverSettings { Rearrange = false });
            s.SetTargetRadii(r);
            for (int i = 0; i < int.Parse(args[2]); i++) s.Step();
            Console.WriteLine("mesh " + s.Mesh.T.Count + " tris");
            var sw = Stopwatch.StartNew();
            var fn = Rearranger.Fit(s, s.S.FitPrior);
            Console.WriteLine("fit " + sw.ElapsedMilliseconds + " ms");
            var clock = Stopwatch.StartNew();
            MobiusMesher.Debug = (stage, mm) => { Console.WriteLine(string.Format(CI, "  {0,-9} {1,6} ms  tris {2}", stage, clock.ElapsedMilliseconds, mm.T.Count)); clock.Restart(); };
            var m = s.Mesh;
            Vec3 lo = new Vec3(m.V.Min(p => p.X), m.V.Min(p => p.Y), m.V.Min(p => p.Z)), hi = new Vec3(m.V.Max(p => p.X), m.V.Max(p => p.Y), m.V.Max(p => p.Z));
            sw.Restart(); clock.Restart();
            var pad = new Vec3(2 * s.TargetEdge, 2 * s.TargetEdge, 2 * s.TargetEdge);
            double gf = args.Length > 3 ? double.Parse(args[3], CI) : 1;
            int its = args.Length > 4 ? int.Parse(args[4]) : 3;
            var mesh = MobiusMesher.Build(fn, lo - pad, hi + pad, s.TargetEdge, out info, s.RegionScale, gf, its);
            Console.WriteLine("build total " + sw.ElapsedMilliseconds + " ms: " + info);
            // Quality of the rebuilt mesh, then relax it a little and report again.
            var s2 = new FoamSolver(mesh, new SolverSettings { Rearrange = false });
            s2.SetTargetRadii(r);
            for (int i = 0; i < 150; i++) s2.Step();
            Console.WriteLine("after 150 steps: " + FoamAnalysis.Report(s2).Replace('\n', ' '));
            return 0;
        }
        if (cmd == "boxleak") { BoxLeak(args[1], int.Parse(args[2])); return 0; }
        if (cmd == "replaylive") { ReplayLive(args[1], args.Length > 2 ? int.Parse(args[2]) : 3000); return 0; }
        if (cmd == "live")
        {
            string which = args.Length > 1 ? args[1] : "all";
            if (which == "all" || which == "resize") LiveResize();
            if (which == "all" || which == "pull") LivePull();
            if (which == "all" || which == "add") LiveAdd();
            if (which == "addfix") AddFix();
            if (which == "addmany") AddMany();
            if (which == "all" || which == "pop") LivePop();
            if (which == "all" || which == "raft") LiveRaft();
            return 0;
        }
        if (cmd == "host") { HostTests(args.Length > 1 ? args[1] : "all"); return 0; }
        if (cmd == "big") { BigTests(args.Length > 1 ? args[1] : "all"); return 0; }
        if (cmd == "rebuilddebug") { RebuildDebug(args[1], int.Parse(args[2]), int.Parse(args[3])); return 0; }
        if (cmd == "failrepro") { FailRepro(args[1], int.Parse(args[2]), args.Length > 3 ? args[3] : "any"); return 0; }
        if (cmd == "gravity")
        {
            // A small bubble stuck high on the flank of a dome on a plane. gravity <g> [stepsOn] [stepsOff]
            double g = double.Parse(args[1], CI);
            int on = args.Length > 2 ? int.Parse(args[2]) : 2000, off = args.Length > 3 ? int.Parse(args[3]) : 1000;
            var plane = new PlaneHost(Vec3.Zero, new Vec3(0, 0, 1));
            var c = new List<Vec3> { new Vec3(0, 0, 0.2), new Vec3(1.15, 0, 1.25) };
            var r = new List<double> { 1.0, 0.5 };
            string info;
            var s = new FoamSolver(MobiusMesher.Build(c, r, 0.1, out info, plane), new SolverSettings()) { Host = plane };
            s.SetTargetRadii(r);
            Run(s, 600, "settle, no gravity");
            Func<string> where = () =>
            {
                var cen = s.Centroids();
                bool[] onHost; var piece = Contacts.Pieces(s.Mesh, out onHost);
                bool foot = s.Mesh.T.Any(t => FoamMesh.IsSolid(t) && (t.Neg == 2 || t.Pos == 2));
                return string.Format(CI, "small bubble centroid z {0:F3}{1}", cen[2].Z, foot ? " (touching the plane)" : "");
            };
            Console.WriteLine("  " + where());
            s.S.Gravity = g;
            for (int i = 0; i < on; i += 500) Run(s, 500, "gravity " + g);
            Console.WriteLine("  with gravity: " + where());
            s.S.Gravity = 0;
            for (int i = 0; i < off; i += 500) Run(s, 500, "gravity off");
            Console.WriteLine("  after gravity off: " + where() + "; " + FoamAnalysis.ContactAngles(s.Mesh));
            return 0;
        }
        if (cmd == "clean")
        {
            // Border cleanup on a recorded mesh: clean <mesh.obj> <cellSize> [iterations] [arcTol]
            var m = FoamAnalysis.FromObj(File.ReadAllText(args[1]));
            double cell = double.Parse(args[2], CI);
            var set = new BorderCleanSettings { Iterations = args.Length > 3 ? int.Parse(args[3]) : 12, ArcTolerance = args.Length > 4 ? double.Parse(args[4], CI) : 0.5 };
            var sw = Stopwatch.StartNew();
            var cb = BorderClean.Clean(m, cell, set);
            Console.WriteLine(string.Format(CI, "{0} borders in {1} ms; arcs {2} ({3:P0}); host outlines {4}", cb.Count, sw.ElapsedMilliseconds,
                cb.Count(b => b.IsArc), cb.Count(b => b.IsArc) / (double)Math.Max(1, cb.Count), cb.Count(b => b.OnHost)));
            Console.WriteLine(string.Format(CI, "mean turning per vertex: raw {0:F2} deg -> clean {1:F2} deg", cb.Average(b => b.RawTurning), cb.Average(b => b.CleanTurning)));
            var devs = cb.Where(b => !double.IsNaN(b.ArcDeviation)).Select(b => b.ArcDeviation / cell).OrderBy(x => x).ToList();
            if (devs.Count > 0) Console.WriteLine(string.Format(CI, "arc deviation / CellSize: median {0:F2}, 90% {1:F2}, max {2:F2}", devs[devs.Count / 2], devs[(int)(devs.Count * 0.9)], devs[devs.Count - 1]));
            // Node continuity: all borders ending at a node end on the same point.
            var ends = new Dictionary<int, List<Vec3>>();
            foreach (var b in cb.Where(b => !b.Closed))
            {
                if (!ends.ContainsKey(b.StartNode)) ends[b.StartNode] = new List<Vec3>();
                if (!ends.ContainsKey(b.EndNode)) ends[b.EndNode] = new List<Vec3>();
                ends[b.StartNode].Add(b.Points[0]); ends[b.EndNode].Add(b.Points[b.Points.Count - 1]);
            }
            double gap = ends.Values.Max(l => l.Max(q => (q - l[0]).Length));
            Console.WriteLine(string.Format(CI, "nodes {0}; largest gap between border ends at a node {1:G2}", ends.Count, gap));
            return 0;
        }
        if (cmd == "stream")
        {
            // Stream bed: 3 domes on a plane, bubbles spawned upstream drift along +x and collect.
            //   stream [steps] [seed] [flow] [sink]
            int steps = args.Length > 1 ? int.Parse(args[1]) : 6000, seed = args.Length > 2 ? int.Parse(args[2]) : 1;
            var plane = new PlaneHost(Vec3.Zero, new Vec3(0, 0, 1));
            var c = new List<Vec3> { new Vec3(0, 0, 0.3), new Vec3(1.4, 0.6, 0.3), new Vec3(0.8, -1.2, 0.3) };
            var r = new List<double> { 0.8, 0.7, 0.6 };
            string info;
            var s = new FoamSolver(MobiusMesher.Build(c, r, 0.12, out info, plane),
                new SolverSettings { AsyncRearrange = true, SimulatedDelay = int.Parse(Environment.GetEnvironmentVariable("DELAY") ?? "60") }) { Host = plane };
            s.SetTargetRadii(r);
            var set = new StreamSettings
            {
                Source = new Vec3(-6, 0, 1.0), Flow = new Vec3(args.Length > 3 ? double.Parse(args[3], CI) : 0.5, 0, 0),
                Spread = 1.5, Every = 80, RMin = 0.3, RMax = 0.6, Sink = args.Length > 4 ? double.Parse(args[4], CI) : 0.25, Exit = 10, MaxFree = 6,
            };
            var rng = new Random(seed); var st = new StreamBed.State();
            var sw = Stopwatch.StartNew();
            int refused = 0, rebuilt = 0; string last = "";
            for (int i = 0; i < steps; i++)
            {
                if (i % 25 == 0) last = StreamBed.Tick(s, set, rng, st);   // GH: once per tick (~25-50 steps)
                s.Step();
                foreach (var l in s.RearrangeLog)
                {
                    if (l.Contains("refused")) { refused++; Console.WriteLine("   " + (l.Length > 220 ? l.Substring(0, 220) : l)); }
                    if (l.Contains("-> rebuilt")) rebuilt++;
                }
                s.RearrangeLog.Clear();
                if ((i + 1) % 1000 == 0)
                {
                    var rep = FoamAnalysis.Report(s).Split('\n');
                    Console.WriteLine(string.Format(CI, "  step {0}: {1}; rebuilds {2}, refused {3}, tris {4}, {5:F0} s | {6}", s.Steps, last, rebuilt, refused, s.Mesh.T.Count, sw.Elapsed.TotalSeconds, rep[2]));
                }
            }
            var vol = s.Mesh.Volumes();
            var live = Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => !s.IsDead(k) && vol[k] > 0).ToList();
            Console.WriteLine("  volume/desired (live): " + string.Join(" ", live.Select(k => (vol[k] / s.DesiredVolume[k]).ToString("F2", CI))));
            return 0;
        }
        if (cmd == "gen")
        {
            // Border Detail generator: batches on a fixture, synchronous (deterministic), then relax.
            //   gen <fixture> [seed] [batches] [where]
            string fx = args.Length > 1 ? args[1] : "fixtures/pack15_gh";
            int seed = args.Length > 2 ? int.Parse(args[2]) : 1, batches = args.Length > 3 ? int.Parse(args[3]) : 4;
            List<double> r;
            var s = LoadFixture(fx, out r, new SolverSettings());
            Run(s, 400, "settle, " + s.Mesh.BubbleCount + " bubbles");
            var set = new BorderDetailSettings { PerBatch = 6, Where = args.Length > 4 ? int.Parse(args[4]) : 0 };
            var rng = new Random(seed);
            int asked = 0;
            for (int b = 0; b < batches; b++)
            {
                var picks = BorderDetail.Choose(s, set, rng);
                asked += picks.Count;
                Console.WriteLine(string.Format(CI, "  batch {0}: {1} seeds, radii {2}", b, picks.Count, string.Join(" ", picks.Select(p => p.Radius.ToString("F2", CI)))));
                s.RequestAddBatch(picks.Select(p => p.At).ToList(), picks.Select(p => p.Radius).ToList(), "border detail");
                Run(s, 300, "after batch " + b + ", bubbles " + s.Mesh.BubbleCount + " (" + s.DeadBubbles.Count + " dead)");
            }
            Run(s, 600, "relax");
            Run(s, 600, "relax");
            var vol = s.Mesh.Volumes();
            int alive = Enumerable.Range(1, s.Mesh.BubbleCount).Count(k => !s.IsDead(k));
            double worst = Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => !s.IsDead(k)).Max(k => Math.Abs(vol[k] / s.DesiredVolume[k] - 1));
            Console.WriteLine(string.Format(CI, "seed {0}: asked {1}, bubbles alive {2} (started {3}), worst volume error {4:P2}, area {5:F4}, tris {6}",
                seed, asked, alive, r.Count, worst, s.Area, s.Mesh.T.Count));
            return 0;
        }
        if (cmd == "budget")
        {
            // Triangle budget of a recorded run's cluster with the current meshing, and what an add would cost.
            List<double> r;
            var s = LoadFixture(args[1], out r, new SolverSettings { Rearrange = false });
            for (int i = 0; i < 400; i++) s.Step();
            double h = s.TargetEdge;
            Console.WriteLine(string.Format(CI, "cluster: {0} bubbles, {1} tris (CellSize {2:G3})", s.Mesh.BubbleCount, s.Mesh.T.Count, h));
            foreach (double rad in new[] { 3.0, 5.27, 7.26, 10, 15, 20 })
            {
                double e = h * MobiusMesher.ScaleFor(rad, h, s.S.EdgesPerRadius);
                Console.WriteLine(string.Format(CI, "  add r {0,5:G3}: edge {1:G3}, about {2:N0} tris", rad, e, 4 * Math.PI * rad * rad / (0.433 * e * e)));
            }
            return 0;
        }
        if (cmd == "replay") { Replay(args[1], args.Length > 2 ? int.Parse(args[2]) : 2000); return 0; }
        string only = args.Length > 1 ? args[1] : null;
        if (only == null || only == "double") DoubleBubble(1.0, 0.7, 1.0);
        if (only == null || only == "double") DoubleBubble(1.0, 0.7, 0.8);
        if (only == null || only == "triple") Triple();
        if (only == null || only == "quad") Quad();
        if (only == null || only == "random") RandomCluster(12, 7);
        return 0;
    }

    static FoamSolver Relax(List<Vec3> c, List<double> r, double h, int maxSteps, SolverSettings set = null, bool sphereVolumes = false)
    {
        int remeshes = 0;
        string info;
        var sw = Stopwatch.StartNew();
        var mesh = MobiusMesher.Build(c, r, h, out info);
        Console.WriteLine("  mesh: " + info + " (" + sw.ElapsedMilliseconds + " ms)");
        var s = new FoamSolver(mesh, set);
        if (sphereVolumes) s.SetTargetRadii(r);
        s.Area = mesh.TotalArea();
        {
            var deg = new Dictionary<int, int>();
            foreach (int nd in s.Nodes)
            {
                int d = 0;
                foreach (var kv in mesh.EdgeTris())
                    if (kv.Value.Count >= 3 && ((int)(kv.Key >> 32) == nd || (int)(kv.Key & 0xffffffff) == nd)) d++;
                deg[d] = deg.ContainsKey(d) ? deg[d] + 1 : 1;
            }
            var edgeKinds = Remesher.EdgeHistogram(mesh);
            Console.WriteLine("  node border-degree histogram: " + string.Join(", ", deg.OrderBy(k => k.Key).Select(k => "deg" + k.Key + "=" + k.Value)) +
                              "; regions per vertex max " + mesh.VertexRegions().Max(l => l.Count));
        }
        Console.WriteLine("  initial: border " + FoamAnalysis.BorderAngles(mesh) + " | node " + FoamAnalysis.NodeAngles(mesh, 2));
        sw.Restart();
        double lastArea = double.MaxValue;
        for (int i = 0; i < maxSteps; i++)
        {
            int before = s.Mesh.T.Count;
            s.Step();
            if (s.LastRemesh != null) { remeshes++; s.LastRemesh = null; }
            if ((i + 1) % 200 == 0)
            {
                Console.WriteLine(string.Format(CI, "  step {0}: area {1:F6} residual {2:G3} volErr {3:E2}  border {4}",
                    s.Steps, s.Area, s.Residual, s.MaxVolumeError, FoamAnalysis.BorderAngles(s.Mesh)));
                if (Math.Abs(lastArea - s.Area) < 2e-5 * s.Area) break;
                lastArea = s.Area;
            }
        }
        Console.WriteLine(string.Format(CI, "  {0} steps in {1} ms ({2:F2} ms/step), {3} remeshes", s.Steps, sw.ElapsedMilliseconds,
                                        sw.ElapsedMilliseconds / (double)Math.Max(1, s.Steps), remeshes));
        Console.WriteLine("  " + FoamAnalysis.Report(s).Replace("\n", "\n  "));
        return s;
    }

    static FoamSolver LoadFixture(string dir, out List<double> radii, SolverSettings set = null)
    {
        var c = new List<Vec3>(); var r = new List<double>(); double h = 0.1;
        HostSurface host = null;
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "inputs.txt")))
        {
            var p = line.Split(' ');
            if (p[0] == "cell") h = double.Parse(p[1], CI);
            if (p[0] == "bubble") { c.Add(new Vec3(double.Parse(p[1], CI), double.Parse(p[2], CI), double.Parse(p[3], CI))); r.Add(double.Parse(p[4], CI)); }
            if (p[0] == "host" && p[1] == "plane")
                host = new PlaneHost(new Vec3(double.Parse(p[2], CI), double.Parse(p[3], CI), double.Parse(p[4], CI)),
                                     new Vec3(double.Parse(p[5], CI), double.Parse(p[6], CI), double.Parse(p[7], CI)));
        }
        // CLAMP=1: apply the GH build's radius floor (1.5 x CellSize).
        if (Environment.GetEnvironmentVariable("CLAMP") != null) r = r.Select(x => Math.Max(x, 1.5 * h)).ToList();
        string info;
        var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info, host),
                               set ?? new SolverSettings { AsyncRearrange = Environment.GetEnvironmentVariable("ASYNC") != null }) { Host = host };
        if (host != null) Console.WriteLine("  host: plane");
        s.SetTargetRadii(r);
        if (Environment.GetEnvironmentVariable("NOTRANSFER") != null) s.S.TransferShape = false; // A/B against ShapeTransfer
        if (s.DeadBubbles.Count > 0) Console.WriteLine("  dead bubbles (no volume at build): " + string.Join(", ", s.DeadBubbles));
        radii = r;
        return s;
    }

    static void Run(FoamSolver s, int n, string label)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
        {
            s.Step();
            foreach (var l in s.RearrangeLog) Console.WriteLine("   " + l);
            s.RearrangeLog.Clear();
        }
        var rep = FoamAnalysis.Report(s).Split('\n');
        Console.WriteLine(string.Format(CI, "  {0}: {1} steps, {2:F1} s | {3} | {4} | {5}", label, n, sw.Elapsed.TotalSeconds, rep[1], rep[2], rep[3]));
    }

    static void LiveResize()
    {
        Console.WriteLine("\n== live resize: double bubble, B 0.7 -> 0.9 ==");
        List<double> r;
        var s = LoadFixture("fixtures/double_gh", out r);
        Run(s, 300, "settle");
        s.SetDesiredRadii(new List<double> { 1.0, 0.9 });
        Run(s, 800, "after resize");
        var vol = s.Mesh.Volumes();
        Console.WriteLine(string.Format(CI, "  volume B {0:F4} vs desired {1:F4}", vol[2], 4.0 / 3 * Math.PI * 0.729));
        Vec3 c; double fa, fb, fc;
        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 1), out c, out fa);
        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 2), out c, out fb);
        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 1, 2), out c, out fc);
        double g = fa * fb / Math.Abs(fa - fb);
        Console.WriteLine(string.Format(CI, "  wall {0:F3} vs Glassner {1:F3} ({2:P1})", fc, g, Math.Abs(fc - g) / g));
    }

    static void LivePull()
    {
        Console.WriteLine("\n== live pull: pack10, bubble 1 pulled +2 in x ==");
        List<double> r;
        var s = LoadFixture("fixtures/pack10_gh", out r);
        Run(s, 300, "settle");
        var c0 = s.Centroids()[1];
        s.S.Pull = 1;
        s.PullOn[1] = true;
        s.PullTarget[1] = c0 + new Vec3(2, 0, 0);
        int rounds = Environment.GetEnvironmentVariable("ROUNDS") != null ? int.Parse(Environment.GetEnvironmentVariable("ROUNDS")) : 4;
        for (int round = 0; round < rounds; round++)
        {
            Run(s, 250, "pull round " + round);
            var c = s.Centroids()[1];
            Console.WriteLine(string.Format(CI, "    bubble 1 centroid moved {0:F3} toward target (of 2.0)", c.X - c0.X));
        }
    }

    // GH run 2026-10-15_005230: adds refused ("bubble 9 would shrink to 0-46%"). Rebuild the live
    // state (inputs + last radii event), then measure per-bubble survival of a rebuild per grid factor.
    static void AddFix()
    {
        List<double> r;
        var s = LoadFixture("fixtures/live15_gh", out r, new SolverSettings { Rearrange = false });
        var live = new List<double> { 1.3, 0.719, 0.271, 1.52, 1.53, 0.472, 0.578, 0.611, 0.443, 1.53, 1.43, 1.53, 1.14, 0.453, 1.51 };
        s.SetDesiredRadii(live);
        Run(s, 600, "settle with live radii");
        if (Environment.GetEnvironmentVariable("GRIDS") == null)
        {
            // Full scenario in GH mode: background rebuilds, then add a bubble and let it settle.
            s.S.Rearrange = true; s.S.AsyncRearrange = true;
            Run(s, 300, "with rearrangement");
            s.RequestAdd(1.0, 1);
            int before = s.Mesh.BubbleCount;
            for (int round = 0; round < 6; round++) Run(s, 250, "after add, round " + round + ", bubbles " + s.Mesh.BubbleCount);
            if (s.Mesh.BubbleCount > before)
            {
                var vol = s.Mesh.Volumes();
                Console.WriteLine(string.Format(CI, "  new bubble volume {0:F3} vs desired {1:F3}", vol[s.Mesh.BubbleCount], s.DesiredVolume[s.Mesh.BubbleCount]));
                var worst = Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => vol[k] / s.DesiredVolume[k]).Min();
                Console.WriteLine(string.Format(CI, "  smallest volume / desired over all bubbles: {0:P1}", worst));
            }
            return;
        }
        {
            var vol = s.Mesh.Volumes();
            var fn = Rearranger.Fit(s, s.S.FitPrior);
            var m = s.Mesh;
            for (int k = 1; k <= m.BubbleCount; k++)
            {
                double outer = 0, wall = 0; int no = 0, nw = 0; Vec3 cen = Vec3.Zero; int nc = 0;
                foreach (var t in m.T)
                {
                    if (t.Neg != k && t.Pos != k) continue;
                    Vec3 x = (m.V[t.A] + m.V[t.B] + m.V[t.C]) / 3; cen = cen + x; nc++;
                    int o = t.Neg == k ? t.Pos : t.Neg;
                    if (o == 0) { double f = fn.F(k, x); outer += f * f; no++; } else { double f = fn.F(k, x) - fn.F(o, x); wall += f * f; nw++; }
                }
                Console.WriteLine(string.Format(CI, "  b{0,2}: vol {1,7:F4} desired {2,7:F4} p {3,6:F2}  A {4,6:F3}  outer rms {5:F4} (n{6}) wall rms {7:F4} (n{8}) f(cen) {9:F3}",
                    k, vol[k], s.DesiredVolume[k], s.Pressure[k], fn.A[k - 1], Math.Sqrt(outer / Math.Max(1, no)), no, Math.Sqrt(wall / Math.Max(1, nw)), nw, fn.F(k, cen / nc)));
            }
        }
        foreach (double gf in new[] { 1.5 })
        {
            s.S.RebuildGridFactor = gf;
            var job = Rearranger.Prepare(s, "test");
            job.Run();
            var v = job.Mesh != null ? job.Mesh.Volumes() : null;
            string ratios = v == null ? job.Fail : string.Join(" ", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => (v[k] / job.CurrentVolume[k]).ToString("F2", CI)));
            Console.WriteLine(string.Format(CI, "  grid {0}: {1} ms, {2} | {3}", gf, job.Ms, job.Mesh != null ? job.Mesh.T.Count + " tris" : "", ratios));
            Console.WriteLine("     " + job.Calibration);
            var add = Rearranger.PrepareAdd(s, s.Mesh.V[s.Nodes[0]], 1.25 * s.TargetEdge * s.S.RebuildGridFactor, 1.0); // as StartAdd
            add.Run();
            Console.WriteLine("     add: " + (add.Fail ?? "ok, new bubble volume " + add.Mesh.Volumes()[s.Mesh.BubbleCount + 1].ToString("G3", CI)));
            if (add.Candidate != null)
                foreach (var kv in add.Candidate.EdgeTris())
                {
                    if (kv.Value.Count < 4) continue;
                    var cm = add.Candidate;
                    int ea = (int)(kv.Key >> 32), eb = (int)(kv.Key & 0xffffffff);
                    Console.WriteLine("     4+ edge " + ea + "-" + eb + " len " + (cm.V[ea] - cm.V[eb]).Length.ToString("G3", CI) + " films: " +
                        string.Join(", ", kv.Value.Select(ti => cm.T[ti].Neg + "|" + cm.T[ti].Pos + " area " + cm.TriArea(cm.T[ti]).ToString("G2", CI))) +
                        "; seed at distance " + (cm.V[ea] - add.AddAt).Length.ToString("G3", CI) + ", seed r0 " + add.AddR0.ToString("G3", CI) +
                        ", new bubble volume " + cm.Volumes()[cm.BubbleCount].ToString("G3", CI));
                }
        }
    }

    // GH run 2026-10-15_011442: adds of r 0.8, 1.4, 0.4 worked; later 0.4/0.3/0.1 failed ("no volume").
    static void AddMany()
    {
        List<double> r;
        var s = LoadFixture("fixtures/live15_gh", out r, new SolverSettings { AsyncRearrange = true });
        var live = new List<double> { 1.3, 0.719, 0.271, 1.52, 1.53, 0.472, 0.578, 0.611, 0.443, 1.53, 1.03, 1.53, 1.14, 0.453, 1.51 };
        s.SetDesiredRadii(live);
        Run(s, 300, "settle");
        foreach (double rad in new[] { 0.8, 1.4, 0.4, 0.4, 0.4, 0.1, 0.3, 0.3 })
        {
            s.RequestAdd(rad, 1);
            Run(s, 50, "requested r " + rad);
        }
        for (int round = 0; round < 12 && (s.PendingAdds > 0 || s.RebuildPending); round++) Run(s, 250, "draining queue, bubbles " + s.Mesh.BubbleCount + ", queued " + s.PendingAdds);
        Run(s, 500, "settle, bubbles " + s.Mesh.BubbleCount);
        var vol = s.Mesh.Volumes();
        Console.WriteLine("  bubbles " + s.Mesh.BubbleCount + " (want 23); volume/desired: " +
            string.Join(" ", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => (vol[k] / s.DesiredVolume[k]).ToString("F2", CI))));
    }

    // Replay a GH recording including its live actions (adds, radii changes, drags) at the
    // recorded solver steps, in background-rebuild mode like GH. Rebuild timing differs from GH,
    // so it's a faithful scenario, not a bit-exact replay.
    //   dotnet run -- replaylive <runDir> [steps]
    static void ReplayLive(string dir, int steps)
    {
        List<double> r;
        var s = LoadFixture(dir, out r, new SolverSettings { AsyncRearrange = true, SimulatedDelay = int.Parse(Environment.GetEnvironmentVariable("DELAY") ?? "60") });
        var adds = new SortedDictionary<int, List<double>>();
        var addAts = new Dictionary<int, List<Vec3>>();              // parallel to adds; NaN X = no point
        var gathers = new SortedDictionary<int, double>();
        var radii = new SortedDictionary<int, List<double>>();
        var pops = new SortedDictionary<int, List<Vec3>>();
        var batches = new SortedDictionary<int, List<Tuple<string, List<Vec3>, List<double>>>>();
        var streams = new SortedDictionary<int, string>();   // step -> stream generator settings ("" = off)
        var pulls = new SortedDictionary<int, KeyValuePair<double, Dictionary<int, Vec3>>>();
        var stepRx = new System.Text.RegularExpressions.Regex(@"^step (\d+): (.*)$");
        foreach (var line in File.ReadLines(Path.Combine(dir, "log.jsonl")))
        {
            if (!line.Contains("\"type\":\"tick\"")) continue;
            int tickStep = int.Parse(System.Text.RegularExpressions.Regex.Match(line, "\"step\":(\\d+)").Groups[1].Value);
            foreach (System.Text.RegularExpressions.Match em in System.Text.RegularExpressions.Regex.Matches(line, "\"(step \\d+: [^\"]*)\""))
            {
                var m = stepRx.Match(em.Groups[1].Value);
                if (!m.Success) continue;
                int st = int.Parse(m.Groups[1].Value);
                string what = m.Groups[2].Value;
                var am = System.Text.RegularExpressions.Regex.Match(what, @"^add requested \(r ([0-9.eE+-]+)\)");
                if (am.Success)
                {
                    if (!adds.ContainsKey(st)) { adds[st] = new List<double>(); addAts[st] = new List<Vec3>(); }
                    adds[st].Add(double.Parse(am.Groups[1].Value, CI));
                    var atm = System.Text.RegularExpressions.Regex.Match(what, @"\) at (\S+) (\S+) (\S+)");
                    addAts[st].Add(atm.Success ? new Vec3(double.Parse(atm.Groups[1].Value, CI), double.Parse(atm.Groups[2].Value, CI), double.Parse(atm.Groups[3].Value, CI))
                                               : new Vec3(double.NaN, 0, 0));
                }
                if (what.StartsWith("gather -> "))
                {
                    gathers[st] = double.Parse(what.Substring(10), CI);
                }
                if (what.StartsWith("stream generator seed ")) streams[st] = what.Substring("stream generator ".Length);
                if (what == "stream generator off") streams[st] = "";
                // Generator batches: "batch of N requested by LABEL: x y z r R; x y z r R; ..."
                var bm = System.Text.RegularExpressions.Regex.Match(what, @"^batch of \d+ requested by ([^:]*): (.*)$");
                if (bm.Success)
                {
                    var pts = new List<Vec3>(); var rs = new List<double>();
                    foreach (var part in bm.Groups[2].Value.Split(';'))
                    {
                        var f = part.Trim().Split(' ');
                        if (f.Length < 5) continue;
                        pts.Add(new Vec3(double.Parse(f[0], CI), double.Parse(f[1], CI), double.Parse(f[2], CI)));
                        rs.Add(double.Parse(f[4], CI));
                    }
                    if (!batches.ContainsKey(st)) batches[st] = new List<Tuple<string, List<Vec3>, List<double>>>();
                    batches[st].Add(Tuple.Create(bm.Groups[1].Value, pts, rs));
                }
                // An add requested by a generator script (Foam.Add) replays like an AddAt add.
                var gm = System.Text.RegularExpressions.Regex.Match(what, @"^add requested by a generator \(r ([0-9.eE+-]+)\) at (\S+) (\S+) (\S+)");
                if (gm.Success)
                {
                    if (!adds.ContainsKey(st)) { adds[st] = new List<double>(); addAts[st] = new List<Vec3>(); }
                    adds[st].Add(double.Parse(gm.Groups[1].Value, CI));
                    addAts[st].Add(new Vec3(double.Parse(gm.Groups[2].Value, CI), double.Parse(gm.Groups[3].Value, CI), double.Parse(gm.Groups[4].Value, CI)));
                }
                var pm2 = System.Text.RegularExpressions.Regex.Match(what, @"^pop requested(?: by a generator)? at (\S+) (\S+) (\S+)");
                if (pm2.Success)
                {
                    if (!pops.ContainsKey(st)) pops[st] = new List<Vec3>();
                    pops[st].Add(new Vec3(double.Parse(pm2.Groups[1].Value, CI), double.Parse(pm2.Groups[2].Value, CI), double.Parse(pm2.Groups[3].Value, CI)));
                }
                if (what.StartsWith("radii -> "))
                    radii[st] = what.Substring(9).Split(' ').Select(x => double.Parse(x, CI)).ToList();
            }
            var pm = System.Text.RegularExpressions.Regex.Match(line, "\"pull\":([0-9.eE+-]+),\"pullTargets\":\\{([^}]*)\\}");
            if (pm.Success)
            {
                var targets = new Dictionary<int, Vec3>();
                foreach (System.Text.RegularExpressions.Match tm in System.Text.RegularExpressions.Regex.Matches(pm.Groups[2].Value, "\"(\\d+)\":\\[([^,]+),([^,]+),([^\\]]+)\\]"))
                    targets[int.Parse(tm.Groups[1].Value)] = new Vec3(double.Parse(tm.Groups[2].Value, CI), double.Parse(tm.Groups[3].Value, CI), double.Parse(tm.Groups[4].Value, CI));
                pulls[tickStep] = new KeyValuePair<double, Dictionary<int, Vec3>>(double.Parse(pm.Groups[1].Value, CI), targets);
            }
        }
        Console.WriteLine(string.Format("replaylive: {0} bubbles, {1} add events, {2} radii events, {3} pops, {4} ticks with pull info",
            r.Count, adds.Values.Sum(l => l.Count), radii.Count, pops.Values.Sum(l => l.Count), pulls.Count));
        var sw = Stopwatch.StartNew();
        int rebuilds = 0;
        for (int i = 0; i < steps; i++)
        {
            int st = s.Steps;
            List<double> a;
            if (adds.TryGetValue(st, out a))
                for (int ai = 0; ai < a.Count; ai++)
                {
                    var at = addAts[st][ai];
                    if (double.IsNaN(at.X)) s.RequestAdd(a[ai], 1); else s.RequestAdd(a[ai], 1, true, at);
                }
            double gv; if (gathers.TryGetValue(st, out gv)) s.S.Gather = gv;
            if (s.S.Gather > 0 && st % 10 == 0)
            {
                // GH's default target (GatherTo not connected): the cluster's volume-weighted centre.
                var gc = s.Centroids(); var gvol = s.Mesh.Volumes(); Vec3 sum = Vec3.Zero; double vs = 0;
                for (int k = 1; k < gc.Length; k++) if (gvol[k] > 0) { sum = sum + gc[k] * gvol[k]; vs += gvol[k]; }
                if (vs > 0) s.S.GatherTo = sum / vs;
            }
            List<double> rr; if (radii.TryGetValue(st, out rr)) s.SetDesiredRadii(rr);
            List<Vec3> pp; if (pops.TryGetValue(st, out pp)) foreach (var p in pp) s.RequestPop(0, p);
            string sg;
            if (streams.TryGetValue(st, out sg))
            {
                if (sg == "") { streamSet = null; s.ExtForce = null; }
                else
                {
                    // "seed S source x y z flow x y z spread a every n rmin a rmax b sink c exit d maxfree m"
                    var f = sg.Split(' ');
                    Func<int, double> d = fi => double.Parse(f[fi], CI);
                    streamRng = new Random(int.Parse(f[1]));
                    streamState = new StreamBed.State();
                    streamSet = new StreamSettings
                    {
                        Source = new Vec3(d(3), d(4), d(5)), Flow = new Vec3(d(7), d(8), d(9)), Spread = d(11), Every = int.Parse(f[13]),
                        RMin = d(15), RMax = d(17), Sink = d(19), Exit = d(21), MaxFree = int.Parse(f[23]),
                    };
                }
            }
            if (streamSet != null && st % 25 == 0) StreamBed.Tick(s, streamSet, streamRng, streamState);
            List<Tuple<string, List<Vec3>, List<double>>> bb;
            if (batches.TryGetValue(st, out bb)) foreach (var bt in bb) s.RequestAddBatch(bt.Item2, bt.Item3, bt.Item1);
            KeyValuePair<double, Dictionary<int, Vec3>> pv;
            if (pulls.TryGetValue(st, out pv))
            {
                s.S.Pull = pv.Key;
                for (int k = 1; k < s.PullOn.Length; k++) s.PullOn[k] = pv.Value.ContainsKey(k);
                foreach (var kv in pv.Value) if (kv.Key < s.PullTarget.Length) s.PullTarget[kv.Key] = kv.Value;
            }
            s.Step();
            if (ReplayStop != null && ReplayStop(s)) { foreach (var l in s.RearrangeLog) Console.WriteLine("   " + l); s.RearrangeLog.Clear(); break; }
            foreach (var l in s.RearrangeLog)
            {
                if (l.Contains("rebuilt")) rebuilds++;
                Console.WriteLine("   " + (l.Length > 200 && Environment.GetEnvironmentVariable("LONG") == null ? l.Substring(0, 200) + "..." : l));
            }
            s.RearrangeLog.Clear();
            if ((i + 1) % 250 == 0)
            {
                var rep = FoamAnalysis.Report(s).Split('\n');
                Console.WriteLine(string.Format(CI, "  step {0}: {1} bubbles, {2} rebuilds so far, {3:F0} s | {4} | {5} | {6}",
                    s.Steps, s.Mesh.BubbleCount, rebuilds, sw.Elapsed.TotalSeconds, rep[1], rep[2], rep[3]));
            }
        }
        var vol = s.Mesh.Volumes();
        Console.WriteLine("  volume/desired: " + string.Join(" ", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => (vol[k] / s.DesiredVolume[k]).ToString("F2", CI))));
        LastReplay = s;
    }

    static FoamSolver LastReplay;
    static StreamSettings streamSet; static Random streamRng; static StreamBed.State streamState;
    static Func<FoamSolver, bool> ReplayStop;

    // Replay until the first refused rebuild (optionally of a given kind), then re-run exactly that rebuild
    // synchronously, with the swallowed-bubble rescue off and on, and describe the failure.
    static void FailRepro(string dir, int steps, string kind)
    {
        ReplayStop = sv => sv.LastFailedJob != null && (kind == "any" || sv.LastFailedJob.Fail.Contains(kind));
        // NORESCUE=1: replay with the swallowed-bubble rescue off (to reach the original failure).
        Rearranger.RescueSwallowed = Environment.GetEnvironmentVariable("NORESCUE") == null;
        ReplayLive(dir, steps);
        ReplayStop = null;
        var s = LastReplay;
        var job = s.LastFailedJob;
        if (job == null) { Console.WriteLine("no refused rebuild in " + steps + " steps"); return; }
        Console.WriteLine("\nrefused at step " + job.Step + ": " + job.What + "\n  -> " + job.Fail + "\n  " + job.Calibration);
        var iters = (Environment.GetEnvironmentVariable("CALIT") ?? "16").Split(',').Select(int.Parse).ToArray();
        foreach (int itc in iters)
        foreach (bool rescue in new[] { false, true })
        {
            Rearranger.RescueSwallowed = rescue;
            Rearranger.CalibrateIterations = itc;
            Console.Write("\n[calibration iterations " + itc + "]");
            var j = job.Retry();
            j.Run();
            Console.WriteLine(string.Format(CI, "\nrerun, rescue {0}: {1}; {2}; {3}", rescue ? "on" : "off", j.Fail ?? "OK", j.Calibration, j.Timing));
            var m = j.Candidate;
            if (m == null) continue;
            var v = m.Volumes();
            Console.WriteLine("  volume vs snapshot: " + string.Join(" ", Enumerable.Range(1, Math.Min(m.BubbleCount, j.CurrentVolume.Length - 1)).Select(k => k + ":" + (v[k] / j.CurrentVolume[k]).ToString("F2", CI))));
            int shown = 0;
            foreach (var kv in m.EdgeTris())
            {
                if (kv.Value.Count != 1 && kv.Value.Count < 4) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                Vec3 mid = (m.V[a] + m.V[b]) * 0.5;
                Console.WriteLine(string.Format(CI, "  {0} edge at ({1:F2}, {2:F2}, {3:F2}){4}: films {5}", kv.Value.Count == 1 ? "open" : "4+",
                    mid.X, mid.Y, mid.Z, s.Host != null ? string.Format(CI, " height {0:G3}", s.Host.Height(mid)) : "",
                    string.Join(", ", kv.Value.Select(ti => m.T[ti].Neg + "|" + m.T[ti].Pos))));
                if (++shown >= 6) break;
            }
        }
        Rearranger.RescueSwallowed = true;
    }

    // Replay a run to `steps`, then run one rebuild by hand and report what happens to bubble k at each stage.
    static void RebuildDebug(string dir, int steps, int k)
    {
        ReplayLive(dir, steps);
        var s = LastReplay;
        while (s.RebuildPending) s.Step();
        var m = s.Mesh;
        var vol = m.Volumes();
        double outer = 0, foot = 0; var walls = new Dictionary<int, double>();
        foreach (var t in m.T)
        {
            if (t.Neg != k && t.Pos != k) continue;
            int o = t.Neg == k ? t.Pos : t.Neg;
            double a = m.TriArea(t);
            if (o == 0) outer += a; else if (o == FoamMesh.Solid) foot += a; else { double w; walls.TryGetValue(o, out w); walls[o] = w + a; }
        }
        Console.WriteLine(string.Format(CI, "\nbubble {0}: volume {1:G4} (desired {2:G4}), pressure {3:G4}; outer film {4:G3}, footprint {5:G3}, walls: {6}",
            k, vol[k], s.DesiredVolume[k], s.Pressure[k], outer, foot, string.Join(", ", walls.Select(kv => kv.Key + ":" + kv.Value.ToString("G3", CI)))));
        Console.WriteLine(string.Format(CI, "edge {0:G3}, rebuild grid {1:G3}, region scale {2:G3}", s.TargetEdge, s.TargetEdge * s.S.RebuildGridFactor, s.RegionScale[k]));
        var job = Rearranger.Prepare(s, "debug");
        var fn = job.Fn;
        var cen = s.Centroids()[k];
        Console.WriteLine(string.Format(CI, "fit: A {0:G4} (sphere radius {1:G4}), f_k at centroid {2:G4}; rival f at centroid: {3}",
            fn.A[k - 1], 1 / (2 * fn.A[k - 1]), fn.F(k, cen),
            string.Join(", ", Enumerable.Range(1, fn.Count).Where(j => j != k && fn.F(j, cen) < 0).Select(j => j + ":" + fn.F(j, cen).ToString("G3", CI)))));
        job.Run();
        Console.WriteLine("calibration: " + job.Calibration + "; fail: " + (job.Fail ?? "none") + "; " + job.Timing);
        Console.WriteLine(string.Format(CI, "after calibration: f_k at centroid {0:G4}, A {1:G4}", fn.F(k, cen), fn.A[k - 1]));
        if (job.Candidate != null)
        {
            var v2 = job.Candidate.Volumes();
            Console.WriteLine(string.Format(CI, "rebuilt volume of {0}: {1:G4}", k, v2[k]));
        }
    }

    // Which bubbles' functions win on the faces of the rebuild box (a region leaking out of the
    // box gets cut there → open edges)? Before and after volume calibration.
    static void BoxLeak(string dir, int steps)
    {
        List<double> r;
        var s = LoadFixture(dir, out r, new SolverSettings { Rearrange = false });
        for (int i = 0; i < steps; i++) s.Step();
        var job = Rearranger.Prepare(s, "boxleak");
        Action<string> report = label =>
        {
            var hits = new Dictionary<int, int>();
            int n = 40;
            for (int a = 0; a <= n; a++)
                for (int b = 0; b <= n; b++)
                    for (int face = 0; face < 6; face++)
                    {
                        double u = a / (double)n, v = b / (double)n;
                        double x = job.Lo.X + (job.Hi.X - job.Lo.X) * (face == 0 ? 0 : face == 1 ? 1 : u);
                        double y = job.Lo.Y + (job.Hi.Y - job.Lo.Y) * (face == 2 ? 0 : face == 3 ? 1 : (face < 2 ? u : v));
                        double z = job.Lo.Z + (job.Hi.Z - job.Lo.Z) * (face == 4 ? 0 : face == 5 ? 1 : v);
                        var p = new Vec3(x, y, z);
                        int best = 0; double bf = 0;
                        for (int k = 1; k <= job.Fn.Count; k++) { double f = job.Fn.F(k, p); if (f < bf) { bf = f; best = k; } }
                        if (best > 0) hits[best] = hits.ContainsKey(best) ? hits[best] + 1 : 1;
                    }
            Console.WriteLine("  " + label + ": bubbles winning on the box faces: " +
                (hits.Count == 0 ? "none" : string.Join(", ", hits.Select(kv => kv.Key + " (" + kv.Value + " samples, A " + job.Fn.A[kv.Key - 1].ToString("F3", CI) + ")"))));
        };
        report("after fit");
        Console.WriteLine("  " + Rearranger.CalibrateVolumes(job.Fn, job.Lo, job.Hi, job.H * job.GridFactor / 1.5, job.CurrentVolume, job.Fn.Count));
        report("after calibration");
    }

    // Pop an outer bubble (air escapes), then an interior one (merges with a neighbour), then one by
    // point. Every other bubble must keep its volume exactly and the foam must stay valid.
    static void LivePop()
    {
        string fx = Environment.GetEnvironmentVariable("FIXTURE") ?? "fixtures/pack15_gh";
        Console.WriteLine("\n== live pop: " + fx + " ==");
        List<double> r;
        var s = LoadFixture(fx, out r, new SolverSettings { AsyncRearrange = Environment.GetEnvironmentVariable("ASYNC") != null });
        Run(s, 600, "settle");
        Func<int, bool> interior = k => !s.Mesh.T.Any(t => (t.Neg == k && t.Pos == 0) || (t.Pos == k && t.Neg == 0)) && s.Mesh.T.Any(t => t.Neg == k || t.Pos == k);
        int outerK = Enumerable.Range(1, s.Mesh.BubbleCount).First(k => !interior(k) && !s.IsDead(k));
        var inner = Enumerable.Range(1, s.Mesh.BubbleCount).Where(interior).ToList();
        Console.WriteLine("  interior bubbles: " + (inner.Count == 0 ? "none" : string.Join(", ", inner)));
        var pops = new List<KeyValuePair<int, Vec3>> { new KeyValuePair<int, Vec3>(outerK, Vec3.Zero) };
        pops.Add(new KeyValuePair<int, Vec3>(-1, Vec3.Zero)); // -1: the first bubble that is interior at that moment
        pops.Add(new KeyValuePair<int, Vec3>(0, s.Centroids()[s.Mesh.BubbleCount])); // by point: the last bubble's centroid
        foreach (var p in pops)
        {
            int pk = p.Key;
            if (pk < 0)
            {
                inner = Enumerable.Range(1, s.Mesh.BubbleCount).Where(interior).ToList();
                if (inner.Count == 0) { Console.WriteLine("  no interior bubble left"); continue; }
                pk = inner[0];
            }
            s.RequestPop(pk, p.Value);
            Run(s, 1, "pop " + (pk > 0 ? "bubble " + pk : "at point"));
            var hist = Remesher.EdgeHistogram(s.Mesh);
            Console.WriteLine("  edges: open " + hist[1] + ", 4+ films " + (hist[4] + hist[5]) + "; dead " + string.Join(",", s.DeadBubbles));
            for (int round = 0; round < 4; round++) Run(s, 300, "relax round " + round);
            var vol = s.Mesh.Volumes();
            double worst = 0; int wk = 0;
            for (int k = 1; k <= s.Mesh.BubbleCount; k++)
            {
                if (s.IsDead(k)) continue;
                double e = Math.Abs(vol[k] - s.DesiredVolume[k]) / s.DesiredVolume[k];
                if (e > worst) { worst = e; wk = k; }
            }
            Console.WriteLine(string.Format(CI, "  worst volume vs desired: bubble {0} {1:P2}", wk, worst));
        }
    }

    // Alex's "bubbles on a plane, added one at a time": three bubbles on z = 0, then bubbles added
    // at points in the open around them (AddAt), with Gather pulling everything toward the origin.
    // Every bubble should join one piece, keep its volume, and the raft should stay near the plane.
    // Last: an AddAt inside the foam, which seeds at the nearest border.
    static void LiveRaft()
    {
        Console.WriteLine("\n== live raft: AddAt in the open + Gather ==");
        double h = 0.12;
        var c = new List<Vec3> { new Vec3(-0.75, 0, 0), new Vec3(0.75, 0, 0), new Vec3(0, 1.2, 0) };
        var r = new List<double> { 0.8, 0.8, 0.8 };
        string info;
        var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info), new SolverSettings { AsyncRearrange = Environment.GetEnvironmentVariable("ASYNC") != null });
        if (Environment.GetEnvironmentVariable("NOTRANSFER") != null) s.S.TransferShape = false;
        s.SetTargetRadii(r);
        s.S.Gather = double.Parse(Environment.GetEnvironmentVariable("GATHER") ?? "0.5", CI);
        s.S.GatherTo = Vec3.Zero;
        Run(s, 300, "settle 3");
        var spots = new[] { new Vec3(2.6, 0.6, 0), new Vec3(-2.6, 0.8, 0), new Vec3(0.2, -2.4, 0), new Vec3(1.8, 2.6, 0), new Vec3(-1.6, 2.8, 0), new Vec3(-2.2, -1.8, 0) };
        foreach (var p in spots)
        {
            s.RequestAdd(0.7, 1, true, p);
            for (int round = 0; round < 8; round++)
            {
                Run(s, 200, "added at " + p + ", bubbles " + s.Mesh.BubbleCount + ", pieces " + Pieces(s));
                if (s.PendingAdds == 0 && !s.RebuildPending && Pieces(s) == 1 && round >= 2) break;
            }
        }
        Console.WriteLine(string.Format(CI, "  before the inside add: max |z| of centroids {0:F3}", s.Centroids().Skip(1).Max(q => Math.Abs(q.Z))));
        s.RequestAdd(0.4, 1, true, new Vec3(0, 0.4, 0.1)); // inside the foam
        for (int round = 0; round < 4; round++) Run(s, 250, "inside add, bubbles " + s.Mesh.BubbleCount);
        s.S.Gather = 0;
        for (int round = 0; round < 4; round++) Run(s, 250, "Gather off, relaxing");
        var vol = s.Mesh.Volumes();
        double zmax = s.Centroids().Skip(1).Max(q => Math.Abs(q.Z));
        Console.WriteLine(string.Format(CI, "  bubbles {0} (want 10), pieces {1}, max |z| of centroids {2:F3}; volume/desired: {3}",
            s.Mesh.BubbleCount, Pieces(s), zmax, string.Join(" ", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => (vol[k] / s.DesiredVolume[k]).ToString("F2", CI)))));
    }

    static int Pieces(FoamSolver s)
    {
        var m = s.Mesh;
        var parent = Enumerable.Range(0, m.BubbleCount + 1).ToArray();
        Func<int, int> find = null;
        find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
        var alive = new bool[m.BubbleCount + 1];
        foreach (var t in m.T)
        {
            if (t.Neg > 0) alive[t.Neg] = true;
            if (t.Pos > 0) alive[t.Pos] = true;
            if (t.Neg > 0 && t.Pos > 0) parent[find(t.Neg)] = find(t.Pos);
        }
        return Enumerable.Range(1, m.BubbleCount).Where(k => alive[k]).Select(find).Distinct().Count();
    }

    // Phase 5: bubbles resting on a host surface.
    static void HostTests(string which)
    {
        var plane = new PlaneHost(Vec3.Zero, new Vec3(0, 0, 1));
        Func<List<Vec3>, List<double>, double, HostSurface, bool, FoamSolver> make = (c, r, h, host, async) =>
        {
            string info;
            var mesh = MobiusMesher.Build(c, r, h, out info, host);
            Console.WriteLine("  mesh: " + info);
            var s = new FoamSolver(mesh, new SolverSettings { AsyncRearrange = async }) { Host = host };
            s.SetTargetRadii(r);
            return s;
        };
        if (which == "all" || which == "dome")
        {
            Console.WriteLine("\n== host: one bubble (r 1) on a plane -> hemisphere R = 2^(1/3) = 1.2599, p = 2/R = 1.5874 ==");
            var s = make(new List<Vec3> { new Vec3(0, 0, 0.3) }, new List<double> { 1.0 }, 0.1, plane, false);
            Run(s, 600, "relax");
            Vec3 c; double R;
            FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 1), out c, out R);
            Console.WriteLine(string.Format(CI, "  dome radius {0:F4} (want 1.2599), centre z {1:F4} (want 0), pressure {2:F4} (want 2/R = {3:F4}), volume {4:F4} (want {5:F4})",
                R, c.Z, s.Pressure[1], 2 / R, s.Mesh.Volumes()[1], 4.0 / 3 * Math.PI));
            Console.WriteLine("  " + FoamAnalysis.ContactAngles(s.Mesh));
        }
        if (which == "all" || which == "hover")
        {
            Console.WriteLine("\n== host: a bubble hovering 0.03 above the plane (inside the landing distance, not touching) -> one landing, then a dome ==");
            var s = make(new List<Vec3> { new Vec3(0, 0, 1.03) }, new List<double> { 1.0 }, 0.1, plane, false);
            int landings = 0;
            for (int i = 0; i < 800; i++)
            {
                s.Step();
                foreach (var l in s.RearrangeLog) { if (l.Contains("lands on")) landings++; Console.WriteLine("   " + (l.Length > 160 ? l.Substring(0, 160) + "..." : l)); }
                s.RearrangeLog.Clear();
            }
            Vec3 c; double R;
            FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 1), out c, out R);
            Console.WriteLine(string.Format(CI, "  landings {0} (want 1), on host {1}, dome radius {2:F4} (want 1.2599), centre z {3:F4}, volume {4:F4} (want {5:F4})",
                landings, OnHost(s), R, c.Z, s.Mesh.Volumes()[1], 4.0 / 3 * Math.PI));
        }
        if (which == "all" || which == "double")
        {
            Console.WriteLine("\n== host: two bubbles (r 1, 0.8) on a plane ==");
            var s = make(new List<Vec3> { new Vec3(-0.6, 0, 0.2), new Vec3(0.7, 0, 0.2) }, new List<double> { 1.0, 0.8 }, 0.1, plane, false);
            Run(s, 800, "relax");
            Console.WriteLine("  contact " + FoamAnalysis.ContactAngles(s.Mesh));
        }
        if (which == "all" || which == "raft")
        {
            Console.WriteLine("\n== host: raft. 3 bubbles on the plane, 5 dropped above it, Gather toward a point on the plane ==");
            var c = new List<Vec3> { new Vec3(-0.8, 0, 0.2), new Vec3(0.8, 0, 0.2), new Vec3(0, 1.3, 0.2),
                                     new Vec3(2.8, 0.5, 1.5), new Vec3(-2.8, 1.0, 1.2), new Vec3(0.3, -2.8, 1.8), new Vec3(2.2, 2.9, 1.0), new Vec3(-2.0, -2.2, 1.4) };
            var r = Enumerable.Repeat(0.7, c.Count).ToList();
            var s = make(c, r, 0.12, plane, Environment.GetEnvironmentVariable("ASYNC") != null);
            s.S.Gather = 0.5; s.S.GatherTo = new Vec3(0, 0.3, 0);
            for (int round = 0; round < 14; round++)
            {
                Run(s, 250, "gather round " + round + ", pieces " + Pieces(s) + ", on host " + OnHost(s) + "/" + c.Count);
                if (round >= 4 && Pieces(s) == 1 && OnHost(s) == c.Count && !s.RebuildPending) break;
            }
            s.S.Gather = 0;
            for (int round = 0; round < 3; round++) Run(s, 250, "Gather off");
            var vol = s.Mesh.Volumes();
            double ztop = s.Mesh.V.Max(p => p.Z);
            Console.WriteLine(string.Format(CI, "  pieces {0}, on host {1}/{2}, highest point z {3:F3} (a one-layer raft of r-0.7 bubbles ~ 1.1); volume/desired: {4}",
                Pieces(s), OnHost(s), c.Count, ztop, string.Join(" ", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => (vol[k] / s.DesiredVolume[k]).ToString("F2", CI)))));
            Console.WriteLine("  contact " + FoamAnalysis.ContactAngles(s.Mesh));
        }
        if (which == "all" || which == "ball")
        {
            Console.WriteLine("\n== host: 4 bubbles (r 0.8) on a ball of radius 3 (curved host) ==");
            var ball = new SphereHost(Vec3.Zero, 3);
            var dirs = new[] { new Vec3(0, 0, 1), new Vec3(0.45, 0, 0.89), new Vec3(-0.22, 0.39, 0.89), new Vec3(-0.22, -0.39, 0.89) };
            var c = dirs.Select(d => d.Normalized() * 3.2).ToList();
            var r = Enumerable.Repeat(0.8, c.Count).ToList();
            var s = make(c, r, 0.1, ball, false);
            Run(s, 800, "relax");
            double off = s.Mesh.T.Where(FoamMesh.IsSolid).SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().Max(v => Math.Abs(ball.Height(s.Mesh.V[v])));
            Console.WriteLine(string.Format(CI, "  footprint vertices off the ball by at most {0:G2}; contact {1}", off, FoamAnalysis.ContactAngles(s.Mesh)));
            var vol = s.Mesh.Volumes();
            Console.WriteLine("  volume/desired: " + string.Join(" ", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => (vol[k] / s.DesiredVolume[k]).ToString("F3", CI))));
        }
    }

    // Big bubbles are meshed coarser (MobiusMesher.ScaleFor up to 4x CellSize).
    static void BigTests(string which)
    {
        if (which == "all" || which == "double")
        {
            Console.WriteLine("\n== big: double bubble r 3 + r 0.6 at CellSize 0.1 (big one meshed 4x coarser) ==");
            var c = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(3.0, 0, 0) };
            var r = new List<double> { 3.0, 0.6 };
            var s = Relax(c, r, 0.1, 1200, new SolverSettings { Smooth = double.Parse(Environment.GetEnvironmentVariable("SMOOTH") ?? "0.3", CI) }, true);
            int extra = int.Parse(Environment.GetEnvironmentVariable("EXTRA") ?? "0");
            for (int done = 0; done < extra; done += 500) Run(s, 500, "extra relax");
            Vec3 cc; double fa, fb, fw;
            FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 1), out cc, out fa);
            FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 2), out cc, out fb);
            FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 1, 2), out cc, out fw);
            double g = fa * fb / Math.Abs(fa - fb);
            Console.WriteLine(string.Format(CI, "  tris {0}; outer radii {1:F4} / {2:F4}; wall {3:F4} vs Glassner {4:F4} ({5:P2}); pressures {6:F4} vs 2/r {7:F4}, {8:F4} vs {9:F4}",
                s.Mesh.T.Count, fa, fb, fw, g, Math.Abs(fw - g) / g, s.Pressure[1], 2 / fa, s.Pressure[2], 2 / fb));
            // Per border edge: worst angle deviation and the height of the triangle on each film (0|1 big outer, 0|2 small outer, 1|2 wall).
            var m = s.Mesh;
            var rows = new List<Tuple<double, string>>();
            foreach (var kv in m.EdgeTris())
            {
                if (kv.Value.Count != 3) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                Vec3 e = (m.V[b] - m.V[a]).Normalized(), mid = (m.V[a] + m.V[b]) * 0.5;
                var dirs = new List<Vec3>(); var info2 = new List<string>();
                foreach (int ti in kv.Value)
                {
                    var t = m.T[ti];
                    int o = t.A != a && t.A != b ? t.A : (t.B != a && t.B != b ? t.B : t.C);
                    Vec3 d = m.V[o] - mid; Vec3 dp = d - e * Vec3.Dot(d, e);
                    dirs.Add(dp.Normalized());
                    info2.Add(string.Format(CI, "{0}|{1} h{2:F3}", Math.Min(t.Neg, t.Pos), Math.Max(t.Neg, t.Pos), dp.Length));
                }
                double worst = 0;
                for (int i = 0; i < 3; i++) for (int j = i + 1; j < 3; j++)
                    worst = Math.Max(worst, Math.Abs(Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(dirs[i], dirs[j])))) * 180 / Math.PI - 120));
                rows.Add(Tuple.Create(worst, string.Format(CI, "edge {0:F3}: ", (m.V[b] - m.V[a]).Length) + string.Join(", ", info2.OrderBy(x => x))));
            }
            rows.Sort((x, y) => y.Item1.CompareTo(x.Item1));
            Console.WriteLine(string.Format(CI, "  border edges {0}; > 10 deg off: {1}; worst five:", rows.Count, rows.Count(x => x.Item1 > 10)));
            foreach (var row in rows.Take(5)) Console.WriteLine(string.Format(CI, "    {0,5:F1} deg  {1}", row.Item1, row.Item2));
            Console.WriteLine("    median: " + rows[rows.Count / 2].Item2 + string.Format(CI, " ({0:F1} deg)", rows[rows.Count / 2].Item1));
        }
        if (which == "all" || which == "add")
        {
            Console.WriteLine("\n== big: Alex's cluster (big_gh) + an r 7.07 bubble dropped next to it (refused in GH before) ==");
            List<double> r;
            var s = LoadFixture("fixtures/big_gh", out r, new SolverSettings { AsyncRearrange = true });
            Run(s, 400, "settle, tris " + s.Mesh.T.Count);
            s.S.Gather = 0.5; s.S.GatherTo = new Vec3(0, -1.5, 0);
            s.RequestAdd(7.07, 1, true, new Vec3(-2.7, -1.4, 9.0));
            for (int round = 0; round < 12; round++) Run(s, 250, "after r 7.07 add, round " + round + ", tris " + s.Mesh.T.Count + ", pieces " + Pieces(s));
            s.S.Gather = 0;
            for (int round = 0; round < 4; round++) Run(s, 250, "Gather off, tris " + s.Mesh.T.Count);
            var vol = s.Mesh.Volumes();
            Console.WriteLine("  bubbles " + s.Mesh.BubbleCount + ", pieces " + Pieces(s) + ", tris " + s.Mesh.T.Count + "; volume/desired: " +
                string.Join(" ", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => (vol[k] / s.DesiredVolume[k]).ToString("F2", CI))));
        }
    }

    static int OnHost(FoamSolver s)
    {
        var on = new HashSet<int>();
        foreach (var t in s.Mesh.T) if (FoamMesh.IsSolid(t)) on.Add(t.Neg == FoamMesh.Solid ? t.Pos : t.Neg);
        return on.Count;
    }

    static void LiveAdd()
    {
        Console.WriteLine("\n== live add: pack10 + bubble r 0.6 at an interior node ==");
        List<double> r;
        var s = LoadFixture("fixtures/pack10_gh", out r);
        Run(s, 300, "settle");
        s.RequestAdd(0.6, 1);
        Run(s, 1, "add");
        Console.WriteLine("  bubbles now " + s.Mesh.BubbleCount);
        Run(s, 400, "inflating");
        Run(s, 400, "settling");
        var vol = s.Mesh.Volumes();
        int k = s.Mesh.BubbleCount;
        Console.WriteLine(string.Format(CI, "  new bubble volume {0:F4} vs desired {1:F4}, pressure {2:F2}", vol[k], 4.0 / 3 * Math.PI * 0.216, s.Pressure[k]));
    }

    static void DoubleBubble(double rA, double rB, double spacing)
    {
        double d = Math.Sqrt(rA * rA + rB * rB - rA * rB) * spacing;
        Console.WriteLine(string.Format(CI, "\n== Double bubble rA={0} rB={1}, start spacing {2}x Glassner ==", rA, rB, spacing));
        var s = Relax(new List<Vec3> { new Vec3(0, 0, 0), new Vec3(d, 0, 0) }, new List<double> { rA, rB }, rB / 7, 3000);
        Vec3 c; double fa, fb, fc;
        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 1), out c, out fa);
        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 0, 2), out c, out fb);
        FoamAnalysis.FitSphere(FoamAnalysis.PairVertices(s.Mesh, 1, 2), out c, out fc);
        Console.WriteLine(string.Format(CI, "  fitted radii: outer A {0:F4}, outer B {1:F4}, wall {2:F4}", fa, fb, fc));
        Console.WriteLine(string.Format(CI, "  Glassner wall from fitted A,B: {0:F4} (error {1:P2})", fa * fb / (fa - fb), Math.Abs(fc - fa * fb / (fa - fb)) / fc));
        Console.WriteLine(string.Format(CI, "  pressure A {0:F4} vs 2/rA {1:F4}; B {2:F4} vs 2/rB {3:F4}", s.Pressure[1], 2 / fa, s.Pressure[2], 2 / fb));
    }

    static void Triple()
    {
        Console.WriteLine("\n== Triple bubble r = 1, 0.85, 0.7 (triangle placement) ==");
        var c = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1.0, 0, 0), new Vec3(0.45, 0.85, 0) };
        Relax(c, new List<double> { 1.0, 0.85, 0.7 }, 0.07, 4000);
    }

    static void Quad()
    {
        Console.WriteLine("\n== Quadruple bubble, equal, tetrahedral placement ==");
        double a = 1 / (2 * Math.Sqrt(2)); // tetra edge = r: equal spheres meeting at 60 degrees
        var c = new List<Vec3> { new Vec3(a, a, a), new Vec3(a, -a, -a), new Vec3(-a, a, -a), new Vec3(-a, -a, a) };
        Relax(c, new List<double> { 1, 1, 1, 1 }, 0.1, 4000);
    }

    static void RandomCluster(int n, int seed)
    {
        Console.WriteLine("\n== Random cluster n=" + n + " seed=" + seed + " ==");
        var rnd = new Random(seed);
        var c = new List<Vec3>(); var r = new List<double>();
        for (int i = 0; i < n; i++)
        {
            c.Add(new Vec3(rnd.NextDouble() * 2.4, rnd.NextDouble() * 2.4, rnd.NextDouble() * 1.6));
            r.Add(0.5 + rnd.NextDouble() * 0.6);
        }
        double worst = ClusterPacker.Pack(c, r, 1000, 0.01);
        Console.WriteLine(string.Format(CI, "  packed: worst touching-pair spacing error {0:P1}", worst));
        Relax(c, r, 0.08, 1000);
    }

    // inputs.txt: "cell <h>" then one "bubble x y z r" line per bubble.
    static void Replay(string dir, int steps)
    {
        var c = new List<Vec3>(); var r = new List<double>(); double h = 0.1;
        foreach (var line in File.ReadAllLines(Path.Combine(dir, "inputs.txt")))
        {
            var p = line.Split(' ');
            if (p[0] == "cell") h = double.Parse(p[1], CI);
            if (p[0] == "bubble")
            {
                c.Add(new Vec3(double.Parse(p[1], CI), double.Parse(p[2], CI), double.Parse(p[3], CI)));
                r.Add(double.Parse(p[4], CI));
            }
        }
        Console.WriteLine("Replaying " + c.Count + " bubbles, cell " + h);
        var s = Relax(c, r, h, steps, null, true);
        string outPath = Path.Combine(dir, "replay_result.obj");
        File.WriteAllText(outPath, FoamAnalysis.ToObj(s.Mesh));
        Console.WriteLine("  wrote " + outPath);
    }
}

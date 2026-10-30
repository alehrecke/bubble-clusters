using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

// Stream mode for the animation (Alex, 2026-10-29: "bubbles in the stream, hitting and gathering and popping too,
// before collapsing"). A few bubbles start the cluster; new bubbles are spawned upstream and carried in by a flow
// (StreamBed, no host: the largest piece is the cluster), they hit it and join through the solver's contact
// rebuilds; the source swings slowly around the cluster so they arrive from every side; now and then a cell of the
// cluster pops (its film breaks, it merges with a neighbour). Then the stream stops, stragglers drift in, the foam
// relaxes, and the animation carries on as before (freeze, drop, reverse).
public static partial class Edition
{
    static FoamSolver StreamFoam(int seed, Random rng, List<AnimFrame> frames, out double h, int steps, Action<string> log, Stopwatch sw)
    {
        var ci = CultureInfo.InvariantCulture;
        var r = new List<double> { 0.95, 0.8, 0.85, 0.65, 0.7 };
        var c = new List<Vec3>();
        for (int i = 0; i < r.Count; i++) c.Add(new Vec3(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * 0.8);
        ClusterPacker.Pack(c, r, 800, 0.02);
        h = 0.1;
        string info;
        var s = new FoamSolver(MobiusMesher.Build(c, r, h, out info), new SolverSettings { Rearrange = true, EdgeLength = h });
        s.SetTargetRadii(r);
        s.S.Gather = 0.15; s.S.GatherTo = Vec3.Zero;
        var set = new StreamSettings { Spread = 1.6, Every = 60, RMin = 0.35, RMax = 0.95, Sink = 0, MaxFree = 7 };
        var st = new StreamBed.State();
        double D = 6.5, phase = rng.NextDouble() * 2 * Math.PI;
        int every = 12, pops = 0, nextPop = steps / 4;
        Func<Func<int, bool>> mainPiece = () =>
        {
            bool[] oh; var piece = Contacts.Pieces(s.Mesh, out oh);
            var size = new Dictionary<int, int>();
            for (int k = 1; k < piece.Length; k++) if (piece[k] > 0) { int n; size.TryGetValue(piece[k], out n); size[piece[k]] = n + 1; }
            int big = size.Count == 0 ? -1 : size.OrderByDescending(kv => kv.Value).First().Key;
            return k => k < piece.Length && piece[k] == big;
        };
        Action<int> runCapture = n =>
        {
            for (int i = 0; i < n; i++)
            {
                s.Step(); s.RearrangeLog.Clear();
                if (s.Steps % every == 0) frames.Add(Capture(s.Mesh, mainPiece()));
            }
        };
        for (int i = 0; i < steps; i++)
        {
            if (i % 25 == 0)
            {
                double th = phase + 2 * Math.PI * 0.75 * i / steps, el = 0.55 * Math.Sin(2 * Math.PI * 1.3 * i / steps);
                var src = new Vec3(Math.Cos(th) * Math.Cos(el), Math.Sin(th) * Math.Cos(el), Math.Sin(el)) * D;
                set.Source = src; set.Flow = (src * -1).Normalized() * 0.5; set.Exit = 2 * D;
                try { StreamBed.Tick(s, set, rng, st); } catch (Exception ex) { log("  stream tick skipped: " + ex.Message); }
                st.Log.Clear();
                // Now and then a cell of the cluster pops.
                if (i >= nextPop && pops < 10 && !s.RebuildPending && s.PendingPops == 0)
                {
                    var inMain = mainPiece();
                    var cand = Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => inMain(k) && !s.IsDead(k)).ToList();
                    if (cand.Count >= 8)
                    {
                        int k = cand[rng.Next(cand.Count)];
                        s.RequestPop(k); pops++;
                        log(string.Format(ci, "  step {0}: pop bubble {1} ({2} in the cluster)", s.Steps, k, cand.Count));
                    }
                    nextPop = i + steps / 14 + rng.Next(Math.Max(1, steps / 14));
                }
            }
            s.Step(); s.RearrangeLog.Clear();
            if (s.Steps % every == 0) frames.Add(Capture(s.Mesh, mainPiece()));
            if (i % 1000 == 999)
                log(string.Format(ci, "  stream step {0}/{1}: {2} bubbles ({3} spawned, {4} gone), {5} tris, {6} frames, {7:F0} s",
                    i + 1, steps, s.Mesh.BubbleCount, st.Spawned, st.Removed, s.Mesh.T.Count, frames.Count, sw.Elapsed.TotalSeconds));
        }
        // Wind down: no more spawns, stragglers drift in, then the foam relaxes on its own.
        s.ExtForce = null;
        s.S.Gather = 0.3;
        runCapture(900);
        s.S.Gather = 0;
        runCapture(720);
        Settle(s, log);
        frames.Add(Capture(s.Mesh, mainPiece()));
        log(string.Format(ci, "stream done: {0} bubbles, {1} pops, {2} foam frames, {3:F0} s", s.Mesh.BubbleCount, pops, frames.Count, sw.Elapsed.TotalSeconds));
        return s;
    }
}

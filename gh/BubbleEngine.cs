// Bubble cluster solver: Grasshopper-side engine (live loop, preview, recorder, Rhino mesh host).
// Used by the plugin and by the paste-in script; see gh/build_gh.py and plugin/.

#region Usings
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Globalization;
using System.Reflection;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion
public static class Settings
{
    // Solver (see SolverSettings in the core for what each does).
    public static readonly double Momentum = 0.9;
    public static readonly double Dt = 0.2;
    public static readonly double Smooth = 0.3;
    public static readonly int RemeshEvery = 10;
    public static readonly bool Rearrange = true;   // topology changes by rebuild (built on a background thread, no pause)

    // Live loop.
    public static readonly double TickBudgetMs = 40;        // solver time per Grasshopper solution (keeps Rhino responsive)
    public static readonly int MaxStepsPerTick = 50;
    public static readonly int ScheduleDelayMs = 15;        // pause between solutions so Rhino can redraw / take input
    public static readonly int SettleWindowSteps = 200;     // settled when area changes less than...
    public static readonly double SettledAreaChange = 1e-4; // ...this fraction over the window (mesh-level jitter is ~5e-5)
    public static readonly int MaxSteps = 6000;             // hard stop, counted from the last live change (resize, drag, add, pop)

    // Info: the full angle report costs ~25 ms in Rhino, so it's refreshed every N ticks
    // (and whenever paused or settled); in between, Info shows a cheap status line.
    public static readonly int FullReportEvery = 10;

    // Preview (drawn by this component; outputs are only filled when something is wired to them).
    public static readonly Color OuterFilmColor = Color.FromArgb(150, 200, 235);
    public static readonly double OuterFilmTransparency = 0.82;  // 0 opaque .. 1 invisible
    public static readonly Color WallColor = Color.FromArgb(255, 175, 70);
    public static readonly double WallTransparency = 0.55;
    public static readonly Color BorderColor = Color.FromArgb(245, 245, 245);
    public static readonly int BorderThickness = 3;
    public static readonly Color NodeColor = Color.FromArgb(255, 60, 60);
    public static readonly int NodeSize = 6;

    // Guards against accidentally huge meshes (e.g. a radius typo).
    public static readonly int MaxTriangles = 60000;        // estimated; above this the build is refused

    // Packing pre-pass (when Pack is true).
    public static readonly int PackIterations = 1000;
    public static readonly double PackCohesion = 0.01;

    // Run recordings (so Claude can replay / inspect a run): see ../CLAUDE.md.
    public static readonly bool RecordRuns = true;
    public static readonly string RunsFolder = @"D:\AUGBOT\Pneuhaus\Tools\bubble-clusters\runs";
    public static readonly int KeepRuns = 5;
    public static readonly int SnapshotEvery = 10;   // ticks between mesh/viewport snapshots (+ first and settled)
    public static readonly bool CaptureViewport = true;
    public static readonly int CaptureWidth = 1200;
    public static readonly int CaptureHeight = 800;
}

public static class BubbleState
{
    public static readonly Dictionary<Guid, BubbleRun> Runs = new Dictionary<Guid, BubbleRun>();
}

public class BubbleRun
{
    public string Signature;
    public FoamSolver Solver;
    public RunRecorder Recorder;
    public Func<RunRecorder> MakeRecorder;
    public string MeshInfo;
    public double WindowArea = double.NaN;
    public int WindowStep;
    public bool Settled;
    public string StopReason = "";
    public List<string> Warnings = new List<string>();
    public int Ticks;
    public double LastTickMs;
    public int LastTickSteps;
    public string LastReport;
    public List<string> Rearrangements = new List<string>();   // all rearrangement messages this run
    public List<string> NewRearrangements = new List<string>(); // since the last recorded tick
    public int LastReportTick = -1;
    public FoamView View = new FoamView();

    // Live control state.
    public string AppliedRadii = "";
    public Point3d[] Baseline;       // per input bubble: point position when the current drag started
    public Vec3[] Anchor;            // per input bubble: bubble centroid when the drag started
    public bool[] Grabbed;
    public bool LastAdd, LastPop;
    public double MinRadius;          // smallest radius this CellSize can hold (1.5 x CellSize)
    public int LiveRaised;            // how many live radii are currently below MinRadius
    public List<string> Events = new List<string>();     // live changes since the last recorded tick
    public List<string> EventHistory = new List<string>();

    public int LimitFrom;
    public Vec3[] WindowCentroids;   // bubble centroids at the start of the settle window            // step of the last live change; MaxSteps counts from here

    // Any live change restarts the step budget. With a run-wide cap, every press after step 6000
    // got a single tick and stopped again, so queued adds never happened (Alex's pop run 2026-10-15_224201).
    public void Unsettle()
    {
        if (Solver != null) LimitFrom = Solver.Steps;
        Settled = false;
        StopReason = "";
        WindowArea = double.NaN;
    }

    public void Event(string e)
    {
        string line = "step " + Solver.Steps + ": " + e;
        Events.Add(line);
        EventHistory.Add(line);
    }
}

// Rhino-side copies of the film mesh, rebuilt only when the solver's connectivity changes;
// on other ticks only vertex positions are copied. Used for the preview and the outputs.
public class FoamView
{
    public int Version = -1;
    public List<Mesh> Films = new List<Mesh>();
    public List<bool> FilmIsWall = new List<bool>();
    public List<bool> FilmIsFoot = new List<bool>();      // a footprint on the host (not drawn; its outline is a border)      // true: between two bubbles; false: outer cap
    public List<int[]> FilmMap = new List<int[]>();       // Rhino vertex -> foam vertex
    public List<FoamAnalysis.Chain> Chains = new List<FoamAnalysis.Chain>();
    public List<int> NodeIds = new List<int>();
    public List<Polyline> Borders = new List<Polyline>();
    public List<Point3d> Nodes = new List<Point3d>();
    public BoundingBox Box = BoundingBox.Empty;

    public void Update(FoamSolver s)
    {
        var m = s.Mesh;
        if (Version != s.TopologyVersion)
        {
            Version = s.TopologyVersion;
            Films.Clear(); FilmIsWall.Clear(); FilmIsFoot.Clear(); FilmMap.Clear();
            foreach (var grp in m.T.GroupBy(t => FoamMesh.EdgeKey(t.Neg, t.Pos)).OrderBy(g => g.Key))
            {
                var mesh = new Mesh();
                var map = new Dictionary<int, int>();
                var back = new List<int>();
                foreach (var t in grp)
                {
                    int a = MapV(mesh, m, map, back, t.A), b = MapV(mesh, m, map, back, t.B), c = MapV(mesh, m, map, back, t.C);
                    mesh.Faces.AddFace(a, b, c);
                }
                Films.Add(mesh);
                FilmIsWall.Add(grp.First().Neg > 0 && grp.First().Pos > 0);
                FilmIsFoot.Add(FoamMesh.IsSolid(grp.First()));
                FilmMap.Add(back.ToArray());
            }
            Chains = FoamAnalysis.BorderChains(m, out NodeIds);
        }
        else
        {
            for (int f = 0; f < Films.Count; f++)
            {
                var mv = Films[f].Vertices;
                int[] map = FilmMap[f];
                for (int i = 0; i < map.Length; i++) { var p = m.V[map[i]]; mv.SetVertex(i, p.X, p.Y, p.Z); }
            }
        }
        foreach (var mesh in Films) mesh.Normals.ComputeNormals();
        Borders.Clear();
        foreach (var c in Chains)
        {
            var pl = new Polyline(c.Verts.Count + 1);
            foreach (int v in c.Verts) pl.Add(m.V[v].X, m.V[v].Y, m.V[v].Z);
            if (c.Closed) pl.Add(pl[0]);
            Borders.Add(pl);
        }
        Nodes = NodeIds.Select(v => new Point3d(m.V[v].X, m.V[v].Y, m.V[v].Z)).ToList();
        Box = BoundingBox.Empty;
        foreach (var mesh in Films) Box.Union(mesh.GetBoundingBox(false));
    }

    static int MapV(Mesh mesh, FoamMesh m, Dictionary<int, int> map, List<int> back, int v)
    {
        int i;
        if (map.TryGetValue(v, out i)) return i;
        i = mesh.Vertices.Add(m.V[v].X, m.V[v].Y, m.V[v].Z);
        map[v] = i;
        back.Add(v);
        return i;
    }
}

// The component logic, shared by the compiled plugin (plugin/BubbleSolverComponent.cs) and the
// paste-in script (BubbleSolver.template.cs). Both set Component / GrasshopperDocument and call Solve.
public class BubbleEngine
{
    public IGH_Component Component;
    public GH_Document GrasshopperDocument;

    public void Solve(List<Point3d> Centers, List<double> Radii, double CellSize, bool Pack, bool Reset, bool Run,
                           double Pull, bool Add, double AddRadius, bool Pop, Point3d PopAt,
                           Point3d AddAt, double Gather, Point3d GatherTo, Plane HostPlane, Mesh HostMesh,
                           ref object Films, ref object Bubbles, ref object Borders, ref object Nodes,
                           ref object Pressures, ref object Info)
    {
        if (Centers == null || Radii == null || Centers.Count == 0 || Centers.Count != Radii.Count)
        { Info = "Need Centers and Radii lists of the same length."; return; }
        if (Radii.Any(r => !(r > 0)))
        { Info = "All radii must be positive."; return; }

        // Rebuild only when the set of bubbles or the meshing changes; values are applied live.
        HostSurface host = null; string hostSig = "none";
        if (InputWired("HostPlane") && HostPlane.IsValid)
        {
            host = new PlaneHost(new Vec3(HostPlane.Origin.X, HostPlane.Origin.Y, HostPlane.Origin.Z), new Vec3(HostPlane.ZAxis.X, HostPlane.ZAxis.Y, HostPlane.ZAxis.Z));
            hostSig = "plane " + HostPlane.Origin + " " + HostPlane.ZAxis;
        }
        else if (InputWired("HostMesh") && HostMesh != null && HostMesh.IsValid && HostMesh.Faces.Count > 0)
        {
            host = new RhinoMeshHost(HostMesh);
            hostSig = "mesh " + HostMesh.Vertices.Count + " " + HostMesh.GetBoundingBox(false);
        }
        string sig = Centers.Count + "|" + CellSize.ToString("R") + "|" + Pack + "|" + hostSig;
        Guid id = Component.InstanceGuid;
        BubbleRun run;
        BubbleState.Runs.TryGetValue(id, out run);

        if (Reset || run == null || run.Signature != sig)
        {
            double h = CellSize > 0 ? CellSize : Radii.Min() / 6.0;
            // Refuse builds that would lock Rhino up. Upper bound: every sphere fully exposed.
            double sphereArea = Radii.Sum(r => 4 * Math.PI * r * r);
            double estTris = sphereArea / (0.433 * h * h);
            if (estTris > Settings.MaxTriangles)
            {
                double hOk = Math.Sqrt(sphereArea / (0.433 * Settings.MaxTriangles));
                Info = string.Format(CultureInfo.InvariantCulture,
                    "Not built: about {0:N0} triangles at CellSize {1:G3} (limit {2:N0}).\n" +
                    "Radii range {3:G3} to {4:G3}. Check for a typo, or use CellSize >= {5:G3}.",
                    estTris, h, Settings.MaxTriangles, Radii.Min(), Radii.Max(), hOk);
                return;
            }
            run = Build(Centers, Radii, h, Pack, sig, host);
            BubbleState.Runs[id] = run;
            run.AppliedRadii = string.Join(",", Radii.Select(r => r.ToString("R")));
            run.Baseline = Centers.ToArray();
            run.Anchor = new Vec3[Centers.Count];
            run.Grabbed = new bool[Centers.Count];
            run.LastAdd = Add;
            run.LastPop = Pop;
        }

        // Unconnected point inputs arrive as (0,0,0), indistinguishable from a real point there
        // (every pop in run 2026-10-15_224201 was "at 0 0 0"), so check the wires.
        ApplyLive(run, Centers, Radii, Pull, Add, AddRadius, Pop, InputWired("PopAt") ? PopAt : Point3d.Unset,
                  InputWired("AddAt") ? AddAt : Point3d.Unset, Gather, InputWired("GatherTo") ? GatherTo : Point3d.Unset);

        if (Run && !run.Settled)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int steps = 0;
            do { run.Solver.Step(); steps++; }
            while (steps < Settings.MaxStepsPerTick && sw.Elapsed.TotalMilliseconds < Settings.TickBudgetMs);
            run.LastTickMs = sw.Elapsed.TotalMilliseconds;
            run.LastTickSteps = steps;
            if (run.Solver.RearrangeLog.Count > 0)
            {
                run.Rearrangements.AddRange(run.Solver.RearrangeLog);
                run.NewRearrangements.AddRange(run.Solver.RearrangeLog);
                run.Solver.RearrangeLog.Clear();
            }
            run.Ticks++;
            var sv = run.Solver;
            if (double.IsNaN(run.WindowArea)) { run.WindowArea = sv.Area; run.WindowStep = sv.Steps; run.WindowCentroids = sv.Centroids(); }
            else if (sv.Steps - run.WindowStep >= Settings.SettleWindowSteps)
            {
                // A bubble drifting toward the cluster (Gather) keeps its area, so the area test alone would call it settled.
                var cen = sv.Centroids();
                double drift = 0;
                if (run.WindowCentroids != null)
                    for (int k = 1; k < cen.Length && k < run.WindowCentroids.Length; k++) drift = Math.Max(drift, (cen[k] - run.WindowCentroids[k]).Length);
                if (!sv.RebuildPending && sv.PendingAdds == 0 && sv.PendingPops == 0 && Math.Abs(sv.Area - run.WindowArea) < Settings.SettledAreaChange * sv.Area &&
                    drift < 0.1 * sv.TargetEdge)
                { run.Settled = true; run.StopReason = "area stable"; }
                run.WindowArea = sv.Area; run.WindowStep = sv.Steps; run.WindowCentroids = cen;
            }
            if (!run.Settled && sv.Steps - run.LimitFrom >= Settings.MaxSteps) { run.Settled = true; run.StopReason = "step limit"; }
            // Record only runs that actually relax (slider scrubbing with Run off makes no folders).
            if (run.Recorder == null && Settings.RecordRuns) run.Recorder = run.MakeRecorder();
            if (run.Recorder != null) run.Recorder.Tick(run);
            if (!run.Settled)
                GrasshopperDocument.ScheduleSolution(Settings.ScheduleDelayMs, d => Component.ExpireSolution(false));
        }

        var s = run.Solver;
        var m = s.Mesh;
        run.View.Update(s);
        current = run;

        // Outputs only when wired: the component draws its own preview (see DrawViewport*),
        // and filling unwired outputs would make Grasshopper draw the default preview on top.
        if (Wired("Films")) Films = run.View.Films.Select(x => x.DuplicateMesh()).ToList();
        if (Wired("Bubbles")) Bubbles = BubbleMeshes(m);
        if (Wired("Borders")) Borders = run.View.Borders.Select(pl => new PolylineCurve(pl)).ToList();
        if (Wired("Nodes")) Nodes = run.View.Nodes;
        Pressures = Enumerable.Range(1, m.BubbleCount).Select(k => s.Pressure[k]).ToList();

        bool active = Run && !run.Settled;
        if (!active || run.LastReport == null || run.Ticks - run.LastReportTick >= Settings.FullReportEvery)
        {
            run.LastReport = FoamAnalysis.Report(s);
            run.LastReportTick = run.Ticks;
        }
        var sb = new StringBuilder();
        sb.AppendLine(run.Settled ? "SETTLED (" + run.StopReason + ")" : (Run ? "running..." : "paused (set Run to relax)"));
        if (s.RebuildPending) sb.AppendLine("rearranging: building the new mesh in the background...");
        foreach (var w in run.Warnings) sb.AppendLine("WARNING: " + w);
        if (run.LiveRaised > 0)
            sb.AppendLine("WARNING: " + run.LiveRaised + " radius value(s) below " + run.MinRadius.ToString("G3", CultureInfo.InvariantCulture) +
                          " (the smallest this CellSize can hold) are being held at that size.");
        int grabbed = run.Grabbed == null ? 0 : run.Grabbed.Count(g => g);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "bubbles: {0} ({1} from inputs, {2} added{3}, {4} popped{5}); dragging {6}{7}",
            m.BubbleCount - s.DeadBubbles.Count, Centers.Count, s.Added, s.PendingAdds > 0 ? ", " + s.PendingAdds + " queued" : "",
            s.Popped, s.PendingPops > 0 ? ", " + s.PendingPops + " waiting for a rebuild to finish" : "",
            grabbed, grabbed > 0 ? " (Pull " + s.S.Pull.ToString("G3", CultureInfo.InvariantCulture) + ")" : ""));
        foreach (var e in run.EventHistory.Skip(Math.Max(0, run.EventHistory.Count - 3))) sb.AppendLine("  " + e);
        sb.AppendLine("mesh: " + run.MeshInfo);
        if (run.Rearrangements.Count > 0)
        {
            sb.AppendLine("rearrangements: " + run.Rearrangements.Count + " (latest below)");
            foreach (var l in run.Rearrangements.Skip(Math.Max(0, run.Rearrangements.Count - 3))) sb.AppendLine("  " + l);
        }
        if (active)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "now: step {0}, area {1:G6}, max volume error {2:P3}",
                                        s.Steps, s.Area, s.MaxVolumeError));
        sb.AppendLine((active ? "report from tick " + run.LastReportTick + ":\n" : "") + run.LastReport);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "last tick {0:F0} ms for {1} steps", run.LastTickMs, run.LastTickSteps));
        if (run.Recorder != null)
            sb.AppendLine("recording: " + run.Recorder.Folder + (run.Recorder.Error != null ? "  (recorder error: " + run.Recorder.Error + ")" : ""));
        Info = sb.ToString();
    }

    // Live changes without a rebuild: radii → target volumes (ramped), moved points → drags,
    // Add button → a new bubble at an interior node. Any change wakes a settled run.
    static void ApplyLive(BubbleRun run, List<Point3d> centers, List<double> radii, double pull, bool add, double addRadius, bool pop, Point3d popAt,
                          Point3d addAt, double gather, Point3d gatherTo)
    {
        var s = run.Solver;

        string rsig = string.Join(",", radii.Select(r => r.ToString("R")));
        if (rsig != run.AppliedRadii)
        {
            // Same floor as at build: smaller bubbles fall between the rebuild grid's samples.
            var clamped = radii.Select(r => Math.Max(r, run.MinRadius)).ToList();
            run.LiveRaised = Enumerable.Range(0, radii.Count).Count(i => radii[i] < run.MinRadius);
            s.SetDesiredRadii(clamped);
            run.AppliedRadii = rsig;
            run.Event("radii -> " + string.Join(" ", radii.Select(r => r.ToString("G3", CultureInfo.InvariantCulture))));
            run.Unsettle();
        }

        // Drag: once a point moves away from its baseline, its bubble is pulled toward
        // (bubble centroid at that moment) + (how far the point has moved). Untouched points don't
        // pull, so the cluster still relaxes freely. Pull = 0 releases everything.
        bool pullChanged = Math.Abs(s.S.Pull - Math.Max(0, pull)) > 1e-12;
        s.S.Pull = Math.Max(0, pull);
        Vec3[] cen = null;
        for (int i = 0; i < centers.Count && i < run.Baseline.Length; i++)
        {
            int k = i + 1;
            if (k >= s.PullOn.Length) break;
            if (s.IsDead(k)) continue;   // popped (a merged bubble moves with its partner's point)
            if (pull <= 0)
            {
                if (run.Grabbed[i]) { run.Grabbed[i] = false; s.PullOn[k] = false; run.Event("released bubble " + k); run.Unsettle(); }
                run.Baseline[i] = centers[i];
                continue;
            }
            Vector3d moved = centers[i] - run.Baseline[i];
            if (!run.Grabbed[i])
            {
                if (moved.Length < 1e-9) continue;
                if (cen == null) cen = s.Centroids();
                run.Anchor[i] = cen[k];
                run.Grabbed[i] = true;
                run.Event("grabbed bubble " + k);
            }
            var target = run.Anchor[i] + new Vec3(moved.X, moved.Y, moved.Z);
            if (!s.PullOn[k] || (s.PullTarget[k] - target).Length > 1e-9) run.Unsettle();
            s.PullOn[k] = true;
            s.PullTarget[k] = target;
        }
        if (pullChanged) run.Unsettle();

        if (add && !run.LastAdd)
        {
            double r = addRadius > 0 ? addRadius : radii.Average();
            // Same guard as the initial build: AddRadius 10 into a cluster of r ~1 bubbles took the mesh from
            // 32k to 72k triangles, with 3.7 s ticks and 25 s rebuilds (Alex's runs 2026-10-15_233324, 2026-10-16_000150).
            double h = s.TargetEdge;
            double estAdd = 4 * Math.PI * r * r / (0.433 * h * h);
            int room = Settings.MaxTriangles - s.Mesh.T.Count;
            if (estAdd > room)
            {
                double rMax = room > 0 ? Math.Sqrt(room * 0.433 * h * h / (4 * Math.PI)) : 0;
                run.Event("add refused: r " + r.ToString("G3", CultureInfo.InvariantCulture) + " would add about " + estAdd.ToString("N0", CultureInfo.InvariantCulture) +
                          " triangles (limit " + Settings.MaxTriangles.ToString("N0", CultureInfo.InvariantCulture) + ", " + s.Mesh.T.Count.ToString("N0", CultureInfo.InvariantCulture) +
                          " now). Largest that fits: r " + rMax.ToString("G2", CultureInfo.InvariantCulture) + ", or raise CellSize.");
            }
            else if (addAt.IsValid)
            {
                s.RequestAdd(r, 1, true, new Vec3(addAt.X, addAt.Y, addAt.Z));
                run.Event("add requested (r " + r.ToString("G3", CultureInfo.InvariantCulture) + ") at " + addAt.X.ToString("R", CultureInfo.InvariantCulture) + " " +
                          addAt.Y.ToString("R", CultureInfo.InvariantCulture) + " " + addAt.Z.ToString("R", CultureInfo.InvariantCulture));
            }
            else
            {
                s.RequestAdd(r, 1);
                run.Event("add requested (r " + r.ToString("G3", CultureInfo.InvariantCulture) + ")");
            }
            run.Unsettle();
        }
        run.LastAdd = add;

        if (pop && !run.LastPop)
        {
            if (!popAt.IsValid) run.Event("pop pressed, but nothing is connected to PopAt");
            else
            {
                // The bubble is picked when the pop happens (after any running rebuild).
                s.RequestPop(0, new Vec3(popAt.X, popAt.Y, popAt.Z));
                run.Event("pop requested at " + popAt.X.ToString("R", CultureInfo.InvariantCulture) + " " +
                          popAt.Y.ToString("R", CultureInfo.InvariantCulture) + " " + popAt.Z.ToString("R", CultureInfo.InvariantCulture));
                run.Unsettle();
            }
        }
        run.LastPop = pop;

        // Gather: toward GatherTo, or the cluster's centre when it isn't connected.
        double g = Math.Max(0, gather);
        if (Math.Abs(g - s.S.Gather) > 1e-12) { run.Event("gather -> " + g.ToString("G3", CultureInfo.InvariantCulture)); run.Unsettle(); }
        s.S.Gather = g;
        if (g > 0)
        {
            Vec3 to;
            if (gatherTo.IsValid) to = new Vec3(gatherTo.X, gatherTo.Y, gatherTo.Z);
            else
            {
                var gc = s.Centroids(); var gvol = s.Mesh.Volumes();
                Vec3 sum = Vec3.Zero; double vs = 0;
                for (int k = 1; k < gc.Length; k++) if (gvol[k] > 0) { sum = sum + gc[k] * gvol[k]; vs += gvol[k]; }
                to = vs > 0 ? sum / vs : Vec3.Zero;
                if (s.Host != null) to = s.Host.Project(to);   // collect onto the surface
            }
            if ((to - s.S.GatherTo).Length > 1e-6 * Math.Max(1, to.Length) && gatherTo.IsValid) run.Unsettle();
            s.S.GatherTo = to;
        }
        if (s.PendingAdds > 0 || s.PendingPops > 0 || s.RebuildPending) run.Unsettle();
    }

    static BubbleRun Build(List<Point3d> centers, List<double> radii, double h, bool pack, string sig, HostSurface host)
    {
        var c = centers.Select(p => new Vec3(p.X, p.Y, p.Z)).ToList();
        // Radii below 1.5 x CellSize are raised: such a bubble falls between the grid's samples and
        // never exists (an r 0.09 bubble at CellSize 0.15 in Alex's run blocked every rebuild).
        double minR = 1.5 * h;
        var raised = Enumerable.Range(0, radii.Count).Where(i => radii[i] < minR).ToList();
        var r = radii.Select(x => Math.Max(x, minR)).ToList();
        if (pack) ClusterPacker.Pack(c, r, Settings.PackIterations, Settings.PackCohesion);
        string info;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mesh = MobiusMesher.Build(c, r, h, out info, host);
        info += string.Format(CultureInfo.InvariantCulture, " ({0:F0} ms)", sw.Elapsed.TotalMilliseconds);
        var set = new SolverSettings
        {
            Momentum = Settings.Momentum, Dt = Settings.Dt, Smooth = Settings.Smooth, RemeshEvery = Settings.RemeshEvery,
            Rearrange = Settings.Rearrange, AsyncRearrange = true,
        };
        var run = new BubbleRun { Signature = sig, Solver = new FoamSolver(mesh, set) { Host = host }, MeshInfo = info };
        if (host != null)
        {
            int below = Enumerable.Range(0, c.Count).Count(i => host.Height(c[i]) < -r[i]);
            if (below > 0) run.Warnings.Add(below + " bubble(s) start entirely inside the host solid and won't exist. Move them to the air side.");
        }
        for (int i = 0; i < c.Count; i++)
            for (int j = 0; j < c.Count; j++)
                if (i != j && r[i] <= r[j] && (c[i] - c[j]).Length + r[i] <= r[j])
                    run.Warnings.Add("bubble " + (i + 1) + " (r " + r[i].ToString("G3") + ") is entirely inside bubble " +
                                     (j + 1) + " (r " + r[j].ToString("G3") + ") and won't exist. Typo in Radii?");
        var vol0 = mesh.Volumes();
        for (int k = 1; k <= mesh.BubbleCount; k++)
            if (!(vol0[k] > 0) && !run.Warnings.Any(w => w.StartsWith("bubble " + k + " ")))
                run.Warnings.Add("bubble " + k + " has no volume in the mesh (swallowed by neighbours). Try Pack, or move it out.");
        run.MinRadius = minR;
        if (raised.Count > 0)
            run.Warnings.Add("bubble " + string.Join(", ", raised.Select(i => (i + 1) + " (r " + radii[i].ToString("G3", CultureInfo.InvariantCulture) + ")")) +
                             " raised to r " + minR.ToString("G3", CultureInfo.InvariantCulture) + ", the smallest CellSize " +
                             h.ToString("G3", CultureInfo.InvariantCulture) + " can hold. For smaller bubbles lower CellSize (about radius / 1.5).");
        run.Solver.SetTargetRadii(r);
        if (run.Solver.DeadBubbles.Count > 0)
            run.Warnings.Add("bubble " + string.Join(", ", run.Solver.DeadBubbles) + " got no space in the mesh and is left out " +
                             "(it won't block anything). Try Pack, move it, or Reset.");
        run.Solver.Area = mesh.TotalArea();
        run.MakeRecorder = () => new RunRecorder(c, r, h, pack, info, set, host);
        return run;
    }

    // Look outputs up by name: Rhino 8 script components can have an extra output (e.g. "out")
    // ahead of ours, so positions shift. Using position 1 for Bubbles returned null.
    // An output we can't find is treated as wired, so it always gets data.
    bool InputWired(string name)
    {
        foreach (var p in Component.Params.Input)
            if (p.NickName == name || p.Name == name) return p.SourceCount > 0;
        return false;
    }

    bool Wired(string name)
    {
        foreach (var p in Component.Params.Output)
            if (p.NickName == name || p.Name == name) return p.Recipients.Count > 0;
        return true;
    }

    // ---- preview ----
    BubbleRun current;
    Rhino.Display.DisplayMaterial outerMat, wallMat;

    public BoundingBox ClippingBox
    {
        get { return current != null ? current.View.Box : BoundingBox.Empty; }
    }

    public void DrawViewportMeshes(IGH_PreviewArgs args)
    {
        if (current == null) return;
        if (outerMat == null)
        {
            outerMat = new Rhino.Display.DisplayMaterial(Settings.OuterFilmColor, Settings.OuterFilmTransparency);
            wallMat = new Rhino.Display.DisplayMaterial(Settings.WallColor, Settings.WallTransparency);
        }
        var v = current.View;
        // Walls first, then the outer caps, so the see-through caps don't hide the walls.
        for (int i = 0; i < v.Films.Count; i++) if (v.FilmIsWall[i]) args.Display.DrawMeshShaded(v.Films[i], wallMat);
        for (int i = 0; i < v.Films.Count; i++) if (!v.FilmIsWall[i] && !v.FilmIsFoot[i]) args.Display.DrawMeshShaded(v.Films[i], outerMat);
    }

    public void DrawViewportWires(IGH_PreviewArgs args)
    {
        if (current == null) return;
        var v = current.View;
        foreach (var pl in v.Borders) args.Display.DrawPolyline(pl, Settings.BorderColor, Settings.BorderThickness);
        foreach (var p in v.Nodes)
            args.Display.DrawPoint(p, Rhino.Display.PointStyle.RoundSimple, Settings.NodeSize, Settings.NodeColor);
    }

    static List<Mesh> BubbleMeshes(FoamMesh m)
    {
        var res = new List<Mesh>();
        for (int k = 1; k <= m.BubbleCount; k++)
        {
            var tris = m.T.Where(t => t.Neg == k || t.Pos == k)
                          .Select(t => t.Neg == k ? t : new FoamTri(t.A, t.C, t.B, t.Pos, t.Neg));
            res.Add(ToRhino(m, tris));
        }
        return res;
    }

    static Mesh ToRhino(FoamMesh m, IEnumerable<FoamTri> tris)
    {
        var mesh = new Mesh();
        var map = new Dictionary<int, int>();
        foreach (var t in tris)
        {
            int a = Map(mesh, m, map, t.A), b = Map(mesh, m, map, t.B), c = Map(mesh, m, map, t.C);
            mesh.Faces.AddFace(a, b, c);
        }
        mesh.Normals.ComputeNormals();
        mesh.Compact();
        return mesh;
    }

    static int Map(Mesh mesh, FoamMesh m, Dictionary<int, int> map, int v)
    {
        int i;
        if (map.TryGetValue(v, out i)) return i;
        i = mesh.Vertices.Add(m.V[v].X, m.V[v].Y, m.V[v].Z);
        map[v] = i;
        return i;
    }
}

// A Rhino mesh as a host surface (the core has no RhinoCommon). Height is signed by the mesh
// normal at the closest point, so normals must point to the air side. Calls are locked: the
// background rebuild and the solver's main-thread steps can both query it.
public class RhinoMeshHost : HostSurface
{
    readonly Mesh mesh;
    readonly object gate = new object();

    public RhinoMeshHost(Mesh m)
    {
        mesh = m.DuplicateMesh();
        mesh.FaceNormals.ComputeFaceNormals();
        mesh.Normals.ComputeNormals();
        mesh.ClosestMeshPoint(Point3d.Origin, 0);   // build the search tree here, on the main thread
    }

    MeshPoint Closest(Vec3 x)
    {
        lock (gate) return mesh.ClosestMeshPoint(new Point3d(x.X, x.Y, x.Z), 0);
    }

    Vector3d NormalAt(MeshPoint mp)
    {
        Vector3d n;
        lock (gate) n = mesh.NormalAt(mp);
        n.Unitize();
        return n;
    }

    public override double Height(Vec3 x)
    {
        var mp = Closest(x);
        if (mp == null) return 1e9;
        var n = NormalAt(mp);
        var d = new Vector3d(x.X - mp.Point.X, x.Y - mp.Point.Y, x.Z - mp.Point.Z);
        double len = d.Length;
        return d * n >= 0 ? len : -len;
    }

    public override Vec3 Project(Vec3 x)
    {
        var mp = Closest(x);
        return mp == null ? x : new Vec3(mp.Point.X, mp.Point.Y, mp.Point.Z);
    }

    public override Vec3 Normal(Vec3 x)
    {
        var mp = Closest(x);
        if (mp == null) return new Vec3(0, 0, 1);
        var n = NormalAt(mp);
        return new Vec3(n.X, n.Y, n.Z);
    }
}

// Records each run to Settings.RunsFolder/<timestamp>/ so it can be replayed headless
// (harness: `dotnet run -- replay <folder>`) or inspected:
//   inputs.txt      cell size + bubble centres/radii after packing (exact replay input)
//   log.jsonl       "run" header (settings, mesh info), one "tick" per Grasshopper solution,
//                   "snapshot" lines naming the files below
//   mesh_kNNNN.obj  the film mesh, one OBJ group per film ("g r<a>_<b>"), at tick NNNN
//   view_kNNNN.png  the active Rhino viewport at the same moment
// Every write is fail-safe: the first error is kept in Error and recording carries on.
public class RunRecorder
{
    public readonly string Folder;
    public string Error;
    readonly string logPath;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    string pendingCapture;
    bool waitingForIdle;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public RunRecorder(List<Vec3> c, List<double> r, double h, bool pack, string meshInfo, SolverSettings set, HostSurface host)
    {
        Folder = Path.Combine(Settings.RunsFolder, DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff"));
        logPath = Path.Combine(Folder, "log.jsonl");
        Try(() =>
        {
            Directory.CreateDirectory(Folder);
            var sb = new StringBuilder();
            sb.Append("# pack ").Append(pack ? "true" : "false").Append(" (centres below are after packing)\n");
            sb.Append("cell ").Append(h.ToString("R", CI)).Append('\n');
            var hp = host as PlaneHost;
            if (hp != null)
                sb.Append("host plane ").Append(string.Join(" ", new[] { hp.Origin.X, hp.Origin.Y, hp.Origin.Z, hp.N.X, hp.N.Y, hp.N.Z }.Select(x => x.ToString("R", CI)))).Append('\n');
            else if (host != null) sb.Append("# host mesh (not recorded; replays run without it)\n");
            for (int i = 0; i < c.Count; i++)
                sb.Append("bubble ").Append(c[i].X.ToString("R", CI)).Append(' ').Append(c[i].Y.ToString("R", CI)).Append(' ')
                  .Append(c[i].Z.ToString("R", CI)).Append(' ').Append(r[i].ToString("R", CI)).Append('\n');
            File.WriteAllText(Path.Combine(Folder, "inputs.txt"), sb.ToString());
            PruneOldRuns();
        });
        var fields = typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Static)
                                     .Select(f => Json.Pair(f.Name, f.GetValue(null)))
                                     .Concat(typeof(SolverSettings).GetFields().Select(f => Json.Pair("solver." + f.Name, f.GetValue(set))));
        Append("{\"type\":\"run\",\"started\":" + Json.Str(DateTime.Now.ToString("s")) + ",\"bubbles\":" + c.Count +
               ",\"mesh\":" + Json.Str(meshInfo) + ",\"settings\":{" + string.Join(",", fields) + "}}");
    }

    void PruneOldRuns()
    {
        if (Settings.KeepRuns <= 0) return;
        var runs = new DirectoryInfo(Settings.RunsFolder).GetDirectories()
            .Where(d => System.Text.RegularExpressions.Regex.IsMatch(d.Name, @"^\d{4}-\d{2}-\d{2}_\d{6}(_\d{3})?$")
                        && File.Exists(Path.Combine(d.FullName, "inputs.txt")))
            .OrderByDescending(d => d.Name).ToList();
        // The current run folder is the newest; keep KeepRuns including it.
        foreach (DirectoryInfo old in runs.Skip(Settings.KeepRuns)) old.Delete(true);
    }

    public void Tick(BubbleRun run)
    {
        var s = run.Solver;
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"tick\",\"tick\":").Append(run.Ticks)
          .Append(",\"t\":").Append(Json.Num(clock.Elapsed.TotalSeconds))
          .Append(",\"ms\":").Append(Json.Num(run.LastTickMs))
          .Append(",\"step\":").Append(s.Steps)
          .Append(",\"verts\":").Append(s.Mesh.V.Count)
          .Append(",\"tris\":").Append(s.Mesh.T.Count)
          .Append(",\"area\":").Append(Json.Num(s.Area))
          .Append(",\"residual\":").Append(Json.Num(s.Residual))
          .Append(",\"maxVolumeError\":").Append(Json.Num(s.MaxVolumeError))
          .Append(",\"settled\":").Append(run.Settled ? "true" : "false")
          .Append(",\"rearrangements\":[").Append(string.Join(",", run.NewRearrangements.Select(Json.Str))).Append("]")
          .Append(",\"events\":[").Append(string.Join(",", run.Events.Select(Json.Str))).Append("]")
          .Append(",\"pull\":").Append(Json.Num(s.S.Pull))
          .Append(",\"pullTargets\":{").Append(string.Join(",", Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => k < s.PullOn.Length && s.PullOn[k])
              .Select(k => "\"" + k + "\":[" + Json.Num(s.PullTarget[k].X) + "," + Json.Num(s.PullTarget[k].Y) + "," + Json.Num(s.PullTarget[k].Z) + "]"))).Append("}")
          .Append(",\"desiredRadii\":[").Append(string.Join(",", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => Json.Num(s.DesiredRadius(k))))).Append("]")
          .Append(",\"pressures\":[").Append(string.Join(",", Enumerable.Range(1, s.Mesh.BubbleCount).Select(k => Json.Num(s.Pressure[k])))).Append("]}");
        Append(sb.ToString());
        bool rearranged = run.NewRearrangements.Count > 0 || run.Events.Count > 0;
        run.NewRearrangements.Clear();
        run.Events.Clear();

        if (run.Ticks != 1 && !rearranged && run.Ticks % Math.Max(1, Settings.SnapshotEvery) != 0 && !run.Settled) return;
        string name = "k" + run.Ticks.ToString("D4");
        var border = FoamAnalysis.BorderAngles(s.Mesh);
        var node = FoamAnalysis.NodeAngles(s.Mesh, 2);
        Try(() => File.WriteAllText(Path.Combine(Folder, "mesh_" + name + ".obj"), FoamAnalysis.ToObj(s.Mesh)));
        if (Settings.CaptureViewport)
        {
            pendingCapture = Path.Combine(Folder, "view_" + name + ".png");
            if (!waitingForIdle) { waitingForIdle = true; RhinoApp.Idle += OnIdle; }
        }
        Append("{\"type\":\"snapshot\",\"tick\":" + run.Ticks + ",\"mesh\":" + Json.Str("mesh_" + name + ".obj") +
               ",\"view\":" + (Settings.CaptureViewport ? Json.Str("view_" + name + ".png") : "null") +
               ",\"borderAngles\":" + Json.Str(border.ToString()) + ",\"nodeAngles\":" + Json.Str(node.ToString()) + "}");
    }

    // Capture once the solution has finished and Rhino is idle: during a solution Grasshopper
    // has cleared the component previews, so mid-solution captures miss the bubbles.
    void OnIdle(object sender, EventArgs e)
    {
        RhinoApp.Idle -= OnIdle;
        waitingForIdle = false;
        if (pendingCapture == null) return;
        string path = pendingCapture;
        pendingCapture = null;
        Try(() =>
        {
            var view = RhinoDoc.ActiveDoc == null ? null : RhinoDoc.ActiveDoc.Views.ActiveView;
            if (view == null) return;
            var capture = new Rhino.Display.ViewCapture
            {
                Width = Settings.CaptureWidth, Height = Settings.CaptureHeight, ScaleScreenItems = false,
                DrawAxes = false, DrawGrid = false, DrawGridAxes = false, TransparentBackground = false,
            };
            using (Bitmap bmp = capture.CaptureToBitmap(view))
                if (bmp != null) bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        });
    }

    void Append(string line) { Try(() => File.AppendAllText(logPath, line + "\n", new UTF8Encoding(false))); }

    void Try(Action a)
    {
        try { a(); }
        catch (Exception e) { if (Error == null) Error = e.GetType().Name + ": " + e.Message; }
    }
}

// Minimal JSON writing (no System.Text.Json in Rhino's .NET Framework mode).
public static class Json
{
    public static string Str(string s)
    {
        return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "")
                               .Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }
    public static string Num(double d)
    {
        return double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture);
    }
    public static string Val(object v)
    {
        if (v is bool) return (bool)v ? "true" : "false";
        if (v is double) return Num((double)v);
        if (v is int) return ((int)v).ToString(CultureInfo.InvariantCulture);
        return Str(Convert.ToString(v, CultureInfo.InvariantCulture));
    }
    public static string Pair(string key, object value) { return Str(key) + ":" + Val(value); }
}

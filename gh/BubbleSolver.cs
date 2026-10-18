// Bubble cluster solver — Phase 3: live control + topology changes (rearrange, contact, pop) (see ../ROADMAP.md, setup in ../gh/SETUP.md)
//
// Physically accurate soap bubble clusters: minimum total film area with each bubble's
// volume held fixed (Surface Evolver-style). Plateau's laws come out of the solve:
// 120 degree borders, 109.47 degree nodes, constant-curvature films. Info reports how
// close the mesh is to each one.
//
// INPUTS
//   Centers   : Point3d list  starting bubble centres. Moving one later (e.g. gumball) drags
//                             its bubble by the same offset, when Pull > 0
//   Radii     : double list   bubble radii: each bubble keeps the volume of a free sphere of that
//                             radius. Changing a value later inflates/deflates that bubble live
//   CellSize  : double        mesh resolution (edge length); <= 0 → smallest radius / 6
//   Pack      : bool          slide centres apart so no two overlap more than a double bubble
//   Reset     : bool          rebuild from the inputs. Changing how many bubbles, CellSize or Pack
//                             also rebuilds; changing values of Centers / Radii does not
//   Run       : bool          keep relaxing; stops by itself once settled
//   Pull      : double        drag strength (0 = off and releases all; ~1 soft, ~5 stiff)
//   Add       : bool          button: add a bubble at a random interior node (where 4 bubbles meet)
//   AddRadius : double        radius of added bubbles (<= 0 → mean of Radii)
//   Pop       : bool          button: pop the bubble at PopAt. With an outer film its air escapes;
//                             fully inside the cluster it merges with its biggest neighbour
//   PopAt     : Point3d       a point inside the bubble to pop (outside all: the nearest bubble)
//   AddAt     : Point3d       optional: where Add puts the bubble. Inside the foam → the nearest
//                             Plateau border; out in the open → right there, as a free bubble
//   Gather    : double        every bubble drifts toward GatherTo (0 = off; ~0.2 gentle, ~1 firm),
//                             so free bubbles collect and stick. Angles are slightly off while it's on;
//                             set it back to 0 once they've joined to get the exact equilibrium
//   HostPlane : Plane         optional: a wet surface the bubbles can rest on (the side the Z axis
//                             points to is air). Bubbles touching it become domes meeting it at 90 degrees
//   HostMesh  : Mesh          optional: same, any mesh (an arm, a form); normals point to the air.
//                             HostPlane wins if both are connected. Changing the host rebuilds
//   GatherTo  : Point3d       optional; not connected → the centre of the cluster
//
// OUTPUTS
//   Films     : Mesh list      the walls between bubbles, one mesh per pair of touching bubbles
//   Bubbles   : Mesh list      one closed mesh per bubble, normals outward
//   Borders   : Curve list     Plateau borders (polylines)
//   Nodes     : Point3d list   points where four borders meet
//   Pressures : double list    per bubble, relative to outside (units: tension / length)
//   Info      : string         diagnostics
//   Caps      : Mesh list      the outer films (each bubble's surface facing the air)
//   Foam      : object         live handle for generators (Border Detail, or your own scripts; see FoamHandle)
//
// GENERATED FILE: edit ../core/*.cs or gh/BubbleSolver.template.cs, then run
// `python gh/build_gh.py`. Don't edit the merged gh/BubbleSolver.cs by hand.

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

// Paste-in shim: Grasshopper calls RunScript; everything happens in BubbleEngine (merged below).
// The compiled plugin (plugin/, BubbleClusters.gha) runs the same engine about 6-8x faster.
public class Script_Instance : GH_ScriptInstance
{
#if GH_CHECK
    // Only for the offline compile check (gh/check); Grasshopper generates this itself.
    public override void InvokeRunScript(IGH_Component owner, object rhinoDocument, int iteration, List<object> inputs, IGH_DataAccess DA) { }
    IGH_Component Component = null;
    GH_Document GrasshopperDocument = null;
#endif
    readonly BubbleEngine engine = new BubbleEngine();

    private void RunScript(List<Point3d> Centers, List<double> Radii, double CellSize, bool Pack, bool Reset, bool Run,
                           double Pull, bool Add, double AddRadius, bool Pop, Point3d PopAt,
                           Point3d AddAt, double Gather, Point3d GatherTo, Plane HostPlane, Mesh HostMesh,
                           ref object Films, ref object Bubbles, ref object Borders, ref object Nodes,
                           ref object Pressures, ref object Info, ref object Caps, ref object Foam)
    {
        engine.Component = Component;
        engine.GrasshopperDocument = GrasshopperDocument;
        engine.Solve(Centers, Radii, CellSize, Pack, Reset, Run, Pull, Add, AddRadius, Pop, PopAt, AddAt, Gather, GatherTo,
                     HostPlane, HostMesh, ref Films, ref Bubbles, ref Borders, ref Nodes, ref Pressures, ref Info, ref Caps, ref Foam);
    }

    public override BoundingBox ClippingBox { get { return engine.ClippingBox; } }
    public override void DrawViewportMeshes(IGH_PreviewArgs args) { engine.DrawViewportMeshes(args); }
    public override void DrawViewportWires(IGH_PreviewArgs args) { engine.DrawViewportWires(args); }
}

// ===================== ENGINE (merged from gh/BubbleEngine.cs by build_gh.py) =====================
// Bubble cluster solver: Grasshopper-side engine (live loop, preview, recorder, Rhino mesh host).
// Used by the plugin and by the paste-in script; see gh/build_gh.py and plugin/.



public static class Settings
{
    // Bump on every change that reaches Alex (shown at the top of Info and in each run recording), so a
    // run can be matched to the build that made it. Format: date + letter.
    public const string Build = "2026-10-18f";

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
    public static readonly Color AddAtColor = Color.FromArgb(40, 200, 255);   // AddAt marker, line to the foam, distance label

    // Guards against accidentally huge meshes (e.g. a radius typo).
    public static readonly int MaxTriangles = 100000;       // estimated; builds and adds above this are refused. Was 60k for the paste-in script (rebuilds 7-10 s at ~47k); the plugin rebuilds ~50k in ~2.5 s

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
    public FoamHandle Handle;        // the live handle given out through the Foam output (one per run)
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
    // Fill every output on every solve. The plugin sets this: its preview override replaces Grasshopper's
    // default preview, so filled outputs don't draw twice. The paste-in script can't stop the default
    // preview, so it fills outputs only when wired (wire them before the run settles, or press Reset).
    public bool AlwaysOutput;

    public void Solve(List<Point3d> Centers, List<double> Radii, double CellSize, bool Pack, bool Reset, bool Run,
                           double Pull, bool Add, double AddRadius, bool Pop, Point3d PopAt,
                           Point3d AddAt, double Gather, Point3d GatherTo, Plane HostPlane, Mesh HostMesh,
                           ref object Films, ref object Bubbles, ref object Borders, ref object Nodes,
                           ref object Pressures, ref object Info, ref object Caps, ref object Foam)
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
            // Refuse builds that would lock Rhino up. Upper bound: every sphere fully exposed, each at its own edge size.
            double estTris = Radii.Sum(r => EstTris(r, h));
            if (estTris > Settings.MaxTriangles)
            {
                double hOk = h;
                while (Radii.Sum(r => EstTris(r, hOk)) > Settings.MaxTriangles) hOk *= 1.1;
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
        UpdateAddAtPreview(run, InputWired("AddAt") ? AddAt : Point3d.Unset);

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

        // Films = the walls between bubbles (one mesh per pair); Caps = the outer films; Bubbles = closed volumes.
        // In the script, outputs are filled only when wired (see AlwaysOutput). In 2026-10-16 runs a Films wire
        // added after the run had settled stayed empty, because nothing re-solved the component.
        if (Wired("Films")) Films = Enumerable.Range(0, run.View.Films.Count).Where(i => run.View.FilmIsWall[i]).Select(i => run.View.Films[i].DuplicateMesh()).ToList();
        if (Wired("Caps")) Caps = Enumerable.Range(0, run.View.Films.Count).Where(i => !run.View.FilmIsWall[i] && !run.View.FilmIsFoot[i]).Select(i => run.View.Films[i].DuplicateMesh()).ToList();
        if (Wired("Bubbles")) Bubbles = BubbleMeshes(m);
        if (Wired("Borders")) Borders = run.View.Borders.Select(pl => new PolylineCurve(pl)).ToList();
        if (Wired("Nodes")) Nodes = run.View.Nodes;
        Pressures = Enumerable.Range(1, m.BubbleCount).Select(k => s.Pressure[k]).ToList();
        if (run.Handle == null) run.Handle = new FoamHandle(run, this);
        Foam = run.Handle;

        bool active = Run && !run.Settled;
        if (!active || run.LastReport == null || run.Ticks - run.LastReportTick >= Settings.FullReportEvery)
        {
            run.LastReport = FoamAnalysis.Report(s);
            run.LastReportTick = run.Ticks;
        }
        var sb = new StringBuilder();
        sb.AppendLine("Bubble Clusters build " + Settings.Build);
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
            double estAdd = EstTris(r, h);
            int room = Settings.MaxTriangles - s.Mesh.T.Count;
            if (estAdd > room)
            {
                double rMax = 0;
                for (double rr = h; rr < 1e4 && EstTris(rr, h) <= room; rr *= 1.05) rMax = rr;
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
    // Upper bound on a bubble's triangles (fully exposed sphere) at its own edge size (see MobiusMesher.ScaleFor):
    // about 1,000 for any bubble between 1.5 and 24 x CellSize in radius, more beyond that.
    static double EstTris(double r, double h)
    {
        double e = h * MobiusMesher.ScaleFor(r, h, new SolverSettings().EdgesPerRadius);
        return 4 * Math.PI * r * r / (0.433 * e * e);
    }

    bool InputWired(string name)
    {
        foreach (var p in Component.Params.Input)
            if (p.NickName == name || p.Name == name) return p.SourceCount > 0;
        return false;
    }

    bool Wired(string name)
    {
        if (AlwaysOutput) return true;
        foreach (var p in Component.Params.Output)
            if (p.NickName == name || p.Name == name) return p.Recipients.Count > 0;
        return true;
    }

    // ---- preview ----
    BubbleRun current;
    Rhino.Display.DisplayMaterial outerMat, wallMat;

    public BoundingBox ClippingBox
    {
        get
        {
            if (current == null) return BoundingBox.Empty;
            var box = current.View.Box;
            if (addAtShown.IsValid) box.Union(addAtShown);
            return box;
        }
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
        // The AddAt point, and when it's outside the foam a dotted line to the nearest bubble with the distance.
        // A point that looks inside the cluster in a perspective view can be far off in depth (Alex's run
        // 2026-10-18_121617: 23 units in front of the cluster).
        if (addAtShown.IsValid)
        {
            args.Display.DrawPoint(addAtShown, Rhino.Display.PointStyle.X, 10, Settings.AddAtColor);
            if (!addAtInside)
            {
                args.Display.DrawDottedLine(addAtShown, addAtNearest, Settings.AddAtColor);
                args.Display.DrawDot(addAtShown, "AddAt: " + addAtDist.ToString("G3", CultureInfo.InvariantCulture) + " outside", Settings.AddAtColor, Color.White);
            }
        }
    }

    // AddAt preview state (updated in Solve; the inside test only when the point moves or the mesh changes).
    Point3d addAtShown = Point3d.Unset, addAtNearest;
    double addAtDist;
    bool addAtInside;
    Point3d addAtTestedAt = Point3d.Unset; int addAtTestedVersion = -1;

    void UpdateAddAtPreview(BubbleRun run, Point3d addAt)
    {
        addAtShown = addAt;
        if (!addAt.IsValid) return;
        var s = run.Solver;
        var p = new Vec3(addAt.X, addAt.Y, addAt.Z);
        if (addAt != addAtTestedAt || s.TopologyVersion != addAtTestedVersion)
        {
            addAtInside = Pop.InsideBubble(s.Mesh, p) != 0;
            addAtTestedAt = addAt; addAtTestedVersion = s.TopologyVersion;
        }
        var q = s.NearestFoamPoint(p, out addAtDist);
        addAtNearest = new Point3d(q.X, q.Y, q.Z);
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

// The live foam, handed downstream through the solver's Foam output. Generators (the Border Detail
// component, or your own C# script component) read the current state and ask for changes; the solver
// applies them on its next ticks. Nothing here is a copy: it always reflects the running simulation.
//
// From a GH C# script, use it through `dynamic` (no assembly reference needed):
//     dynamic foam = Foam;                       // input wired from the solver's Foam output
//     if (!foam.Busy) foam.Add(pt, 0.3);         // one bubble (same rules as the AddAt input)
//     foam.AddBatch(points, radii, "my gen");     // many, one rebuild
// Members:
//   state     Step, Settled, Busy (a rebuild / add / pop is pending), CellSize, MinRadius, Seed
//   bubbles   BubbleIds, Centroid(k), Radius(k) (target), Pressure(k), BubbleAt(point) (0 = none)
//   geometry  Borders (polylines), BorderBubbles (bubble ids meeting at each border; 0 = outside air), Nodes
//   actions   Add(point, r), AddBatch(points, radii, label), Pop(point), Remove(k), Wake()
//   floating  InsertFree(point, r) (a free bubble in open space, no rebuild), FreeBubbleIds (bubbles in pieces
//             not on the host), SetForce(k, vector) / ClearForces() (per-bubble push, e.g. a flow), Note(text)
public class FoamHandle
{
    readonly BubbleRun run;
    readonly BubbleEngine engine;
    public FoamHandle(BubbleRun run, BubbleEngine engine) { this.run = run; this.engine = engine; }

    public FoamSolver Solver { get { return run.Solver; } }   // for the built-in generators (core API)
    public bool IsValid { get { return run != null && run.Solver != null; } }
    public int Step { get { return run.Solver.Steps; } }
    public bool Settled { get { return run.Settled; } }
    public bool Busy { get { var s = run.Solver; return s.RebuildPending || s.PendingAdds > 0 || s.PendingPops > 0; } }
    public double CellSize { get { return run.Solver.TargetEdge; } }
    public double MinRadius { get { return run.Solver.TargetEdge * run.Solver.S.RebuildGridFactor; } }

    public List<int> BubbleIds
    {
        get
        {
            var s = run.Solver; var vol = s.Mesh.Volumes();
            return Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => !s.IsDead(k) && vol[k] > 0).ToList();
        }
    }
    public Point3d Centroid(int k) { var c = run.Solver.Centroids()[k]; return new Point3d(c.X, c.Y, c.Z); }
    public double Radius(int k) { return run.Solver.DesiredRadius(k); }
    public double Pressure(int k) { return run.Solver.Pressure[k]; }
    public int BubbleAt(Point3d p) { return global::Pop.InsideBubble(run.Solver.Mesh, new Vec3(p.X, p.Y, p.Z)); }

    public List<Polyline> Borders { get { run.View.Update(run.Solver); return run.View.Borders.Select(pl => new Polyline(pl)).ToList(); } }
    public List<Point3d> Nodes { get { run.View.Update(run.Solver); return new List<Point3d>(run.View.Nodes); } }
    public List<int[]> BorderBubbles
    {
        get
        {
            var m = run.Solver.Mesh; var et = m.EdgeTris();
            run.View.Update(run.Solver);
            var res = new List<int[]>();
            foreach (var c in run.View.Chains)
            {
                var regs = new SortedSet<int>();
                List<int> tris;
                if (c.Verts.Count >= 2 && et.TryGetValue(FoamMesh.EdgeKey(c.Verts[0], c.Verts[1]), out tris))
                    foreach (int ti in tris) { regs.Add(m.T[ti].Neg); regs.Add(m.T[ti].Pos); }
                res.Add(regs.ToArray());
            }
            return res;
        }
    }

    public void Add(Point3d at, double r)
    {
        run.Solver.RequestAdd(r, 1, true, new Vec3(at.X, at.Y, at.Z));
        run.Event("add requested by a generator (r " + r.ToString("G3", CultureInfo.InvariantCulture) + ") at " +
                  at.X.ToString("R", CultureInfo.InvariantCulture) + " " + at.Y.ToString("R", CultureInfo.InvariantCulture) + " " + at.Z.ToString("R", CultureInfo.InvariantCulture));
        Wake();
    }

    public void AddBatch(IList<Point3d> at, IList<double> r, string label)
    {
        if (at == null || r == null || at.Count == 0) return;
        run.Solver.RequestAddBatch(at.Select(p => new Vec3(p.X, p.Y, p.Z)).ToList(), r.ToList(), label);
        run.Event("batch of " + at.Count + " requested by " + (label ?? "a generator") + ": " +
                  string.Join("; ", Enumerable.Range(0, at.Count).Select(i => string.Format(CultureInfo.InvariantCulture, "{0:R} {1:R} {2:R} r {3:R}", at[i].X, at[i].Y, at[i].Z, r[i]))));
        Wake();
    }

    public void Pop(Point3d at)
    {
        run.Solver.RequestPop(0, new Vec3(at.X, at.Y, at.Z));
        run.Event("pop requested by a generator at " + at.X.ToString("R", CultureInfo.InvariantCulture) + " " +
                  at.Y.ToString("R", CultureInfo.InvariantCulture) + " " + at.Z.ToString("R", CultureInfo.InvariantCulture));
        Wake();
    }

    public void InsertFree(Point3d c, double r)
    {
        run.Solver.RequestInsertFree(new Vec3(c.X, c.Y, c.Z), r);
        Wake();
    }

    public void Remove(int k) { run.Solver.RequestPop(k); Wake(); }

    public List<int> FreeBubbleIds
    {
        get
        {
            bool[] onHost;
            var piece = Contacts.Pieces(run.Solver.Mesh, out onHost);
            return Enumerable.Range(1, run.Solver.Mesh.BubbleCount).Where(k => piece[k] > 0 && !onHost[piece[k]]).ToList();
        }
    }

    // A force density on bubble k (strength ~ Pull/Gather units); kept until changed or cleared.
    public void SetForce(int k, Vector3d f)
    {
        var s = run.Solver;
        int n = s.Mesh.BubbleCount;
        if (s.ExtForce == null || s.ExtForce.Length < n + 1)
        {
            var a = new Vec3[n + 1];
            if (s.ExtForce != null) Array.Copy(s.ExtForce, a, s.ExtForce.Length);
            s.ExtForce = a;
        }
        if (k >= 1 && k <= n) s.ExtForce[k] = new Vec3(f.X, f.Y, f.Z);
        Wake();
    }

    public void ClearForces() { run.Solver.ExtForce = null; Wake(); }

    // A line in the run recording (generators note their settings so Claude can replay them).
    public void Note(string text) { run.Event(text); }

    // Make the solver run again (it stops scheduling itself once settled).
    public void Wake()
    {
        run.Unsettle();
        var doc = engine.GrasshopperDocument; var comp = engine.Component;
        if (doc != null && comp != null) doc.ScheduleSolution(Settings.ScheduleDelayMs, d => comp.ExpireSolution(false));
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
        Append("{\"type\":\"run\",\"build\":" + Json.Str(Settings.Build) + ",\"started\":" + Json.Str(DateTime.Now.ToString("s")) + ",\"bubbles\":" + c.Count +
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


// ===================== CORE (merged from ../core/*.cs by build_gh.py) =====================
// ---- core/Vec3.cs ----
// Minimal 3D vector so the core has no RhinoCommon dependency.
public struct Vec3
{
    public double X, Y, Z;
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static readonly Vec3 Zero = new Vec3(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
    public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    public static Vec3 operator -(Vec3 a) { return new Vec3(-a.X, -a.Y, -a.Z); }
    public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
    public static Vec3 operator *(double s, Vec3 a) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
    public static Vec3 operator /(Vec3 a, double s) { return new Vec3(a.X / s, a.Y / s, a.Z / s); }

    public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
    public static Vec3 Cross(Vec3 a, Vec3 b)
    {
        return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
    public double LengthSq { get { return X * X + Y * Y + Z * Z; } }

    public Vec3 Normalized()
    {
        double l = Length;
        return l > 1e-300 ? this / l : Zero;
    }

    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) { return a + (b - a) * t; }

    public override string ToString() { return "(" + X.ToString("G5") + ", " + Y.ToString("G5") + ", " + Z.ToString("G5") + ")"; }
}

// ---- core/FoamMesh.cs ----
// One triangle of film. Its normal, (B-A)x(C-A), points out of region Neg and into region Pos.
// Region 0 is the outside air; bubbles are 1..BubbleCount.
public struct FoamTri
{
    public int A, B, C, Neg, Pos;
    public FoamTri(int a, int b, int c, int neg, int pos) { A = a; B = b; C = c; Neg = neg; Pos = pos; }
}

// A non-manifold triangle mesh of soap films. Plateau borders are edges with 3+ triangles.
public class FoamMesh
{
    public List<Vec3> V = new List<Vec3>();
    public List<FoamTri> T = new List<FoamTri>();
    public int BubbleCount;

    // Region label of a host solid (see HostSurface). Negative, so every "region > 0" test treats it
    // like the outside, which is what it is for volumes.
    public const int Solid = -1;

    public static bool IsSolid(FoamTri t) { return t.Neg == Solid || t.Pos == Solid; }

    // A Plateau border or other feature edge: 3+ films meet, or a film meets a footprint on a
    // host (that contact line has only 2 triangles). Other 2-triangle edges with mismatched films
    // only appear transiently in the mesher's soup, and must stay collapsible there: counting them
    // as borders made rebuilds fail (cell01 replay: inside-out bubble, open edges).
    public bool IsBorder(List<int> tris)
    {
        if (tris.Count >= 3) return true;
        if (tris.Count != 2) return false;
        var a = T[tris[0]]; var b = T[tris[1]];
        if (!IsSolid(a) && !IsSolid(b)) return false;
        return !((a.Neg == b.Neg && a.Pos == b.Pos) || (a.Neg == b.Pos && a.Pos == b.Neg));
    }

    public FoamMesh Clone()
    {
        var m = new FoamMesh { BubbleCount = BubbleCount };
        m.V.AddRange(V);
        m.T.AddRange(T);
        return m;
    }

    public static long EdgeKey(int a, int b)
    {
        return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    }

    public double TriArea(FoamTri t)
    {
        return 0.5 * Vec3.Cross(V[t.B] - V[t.A], V[t.C] - V[t.A]).Length;
    }

    // Film area (footprints on a host don't count: they carry no tension).
    public double TotalArea()
    {
        double s = 0;
        foreach (var t in T) if (!IsSolid(t)) s += TriArea(t);
        return s;
    }

    // Signed volume of each region (index 0 = outside, left at 0).
    public double[] Volumes()
    {
        var vol = new double[BubbleCount + 1];
        foreach (var t in T)
        {
            double v = Vec3.Dot(V[t.A], Vec3.Cross(V[t.B], V[t.C])) / 6.0;
            if (t.Neg > 0) vol[t.Neg] += v;
            if (t.Pos > 0) vol[t.Pos] -= v;
        }
        return vol;
    }

    public double MeanEdgeLength()
    {
        if (T.Count == 0) return 0;
        double s = 0;
        foreach (var t in T)
            s += (V[t.B] - V[t.A]).Length + (V[t.C] - V[t.B]).Length + (V[t.A] - V[t.C]).Length;
        return s / (3.0 * T.Count);
    }

    // Edge → incident triangle indices.
    public Dictionary<long, List<int>> EdgeTris()
    {
        var map = new Dictionary<long, List<int>>(LongKey.Comparer);
        for (int i = 0; i < T.Count; i++)
        {
            var t = T[i];
            Add(map, EdgeKey(t.A, t.B), i);
            Add(map, EdgeKey(t.B, t.C), i);
            Add(map, EdgeKey(t.C, t.A), i);
        }
        return map;
    }

    static void Add(Dictionary<long, List<int>> map, long k, int i)
    {
        List<int> l;
        if (!map.TryGetValue(k, out l)) { l = new List<int>(3); map[k] = l; }
        l.Add(i);
    }

    // Sorted set of region labels around each vertex.
    public List<int>[] VertexRegions()
    {
        var r = new List<int>[V.Count];
        for (int i = 0; i < V.Count; i++) r[i] = new List<int>(4);
        foreach (var t in T)
        {
            foreach (int v in new[] { t.A, t.B, t.C })
            {
                if (!r[v].Contains(t.Neg)) r[v].Add(t.Neg);
                if (!r[v].Contains(t.Pos)) r[v].Add(t.Pos);
            }
        }
        foreach (var l in r) l.Sort();
        return r;
    }

    // Drop degenerate / duplicate triangles and unused vertices.
    public void Clean()
    {
        var seen = new HashSet<long>(LongKey.Comparer);
        var keep = new List<FoamTri>(T.Count);
        foreach (var t in T)
        {
            if (t.A == t.B || t.B == t.C || t.C == t.A || t.Neg == t.Pos) continue;
            var s = new[] { t.A, t.B, t.C };
            Array.Sort(s);
            long key = ((long)s[0] << 42) | ((long)s[1] << 21) | (long)s[2];
            if (!seen.Add(key)) continue;
            keep.Add(t);
        }
        T = keep;
        var map = new int[V.Count];
        for (int i = 0; i < map.Length; i++) map[i] = -1;
        var nv = new List<Vec3>();
        for (int i = 0; i < T.Count; i++)
        {
            var t = T[i];
            t.A = Remap(map, nv, t.A); t.B = Remap(map, nv, t.B); t.C = Remap(map, nv, t.C);
            T[i] = t;
        }
        V = nv;
    }

    int Remap(int[] map, List<Vec3> nv, int i)
    {
        if (map[i] < 0) { map[i] = nv.Count; nv.Add(V[i]); }
        return map[i];
    }
}

// Edge/triangle keys pack vertex indices into a long. .NET's default long hash XORs the two
// halves, so nearby edges collide massively; this mixes the bits properly.
public sealed class LongKey : IEqualityComparer<long>
{
    public static readonly LongKey Comparer = new LongKey();
    public bool Equals(long a, long b) { return a == b; }
    public int GetHashCode(long k)
    {
        // unchecked: Rhino compiles scripts with overflow checking on; this hash relies on wrapping.
        unchecked
        {
            ulong x = (ulong)k;
            x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
            return (int)x;
        }
    }
}

// ---- core/MobiusMesher.cs ----
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
                                 double gridFactor = 1, int remeshIters = 3, int jitter = 0)
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
        // A retry shifts the grid: phases that tie exactly along a grid line give a 4-film edge (two
        // films crossing), and a different alignment breaks the tie (run 2026-10-18_125753).
        if (jitter > 0) lo = lo - new Vec3(0.3713 * jitter * h, 0.2179 * jitter * h, 0.1291 * jitter * h);
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

// ---- core/Remesher.cs ----
// Isotropic remeshing (Botsch & Kobbelt 2004) adapted to multi-region films:
//   split edges > 4/3 L, collapse edges < 4/5 L, flip film edges toward Delaunay.
// Plateau borders are features: they are split along themselves, a border vertex only
// collapses along its border, and border edges are never flipped. Region labels and
// orientation are carried onto every new triangle, so volumes are untouched (splits and
// flips) or change only slightly (collapses; the solver's volume correction absorbs that).
//
// Implementation (2026-10-16): each operation builds a vertex -> triangle table once (Topo) and
// updates it as it splits / collapses / flips, so edge lookups are small list intersections. The
// first version rebuilt a global edge dictionary, region lists and neighbour sets on every pass:
// ~540 ms for one remesh round on a 45k-triangle mesh that needed almost nothing, and ~4 s of each
// ~5 s rebuild.
public static class Remesher
{
    public class Result { public int Splits, Collapses, Flips; }

    // Vertex -> live triangle table over a FoamMesh, kept current by the operations below.
    sealed class Topo
    {
        public readonly FoamMesh M;
        public readonly List<List<int>> VT;
        public readonly List<bool> Dead;
        public readonly List<double> Size;   // per vertex edge-size scale (graded field, see SizeField)
        readonly List<int> scratch = new List<int>(8);

        public Topo(FoamMesh m, double[] regionScale = null, double L = 1)
        {
            M = m;
            Size = new List<double>(SizeField(m, regionScale, L));
            VT = new List<List<int>>(m.V.Count + 64);
            for (int v = 0; v < m.V.Count; v++) VT.Add(new List<int>(7));
            Dead = new List<bool>(m.T.Count + 64);
            for (int i = 0; i < m.T.Count; i++)
            {
                var t = m.T[i];
                VT[t.A].Add(i); VT[t.B].Add(i); VT[t.C].Add(i);
                Dead.Add(false);
            }
        }

        public int AddVertex(Vec3 p, double size) { M.V.Add(p); VT.Add(new List<int>(7)); Size.Add(size); return M.V.Count - 1; }

        public double EdgeSize(int a, int b) { return Math.Min(Size[a], Size[b]); }

        public int AddTri(FoamTri t)
        {
            int i = M.T.Count;
            M.T.Add(t); Dead.Add(false);
            VT[t.A].Add(i); VT[t.B].Add(i); VT[t.C].Add(i);
            return i;
        }

        public static bool Has(FoamTri t, int v) { return t.A == v || t.B == v || t.C == v; }

        // Live triangles on edge (a, b), into `res` (cleared first).
        public List<int> EdgeTris(int a, int b, List<int> res)
        {
            res.Clear();
            var la = VT[a];
            for (int i = 0; i < la.Count; i++) if (Has(M.T[la[i]], b)) res.Add(la[i]);
            return res;
        }

        public bool EdgeExists(int a, int b)
        {
            var la = VT[a];
            for (int i = 0; i < la.Count; i++) if (Has(M.T[la[i]], b)) return true;
            return false;
        }

        public List<int> Ring(int v, List<int> res)
        {
            res.Clear();
            foreach (int ti in VT[v])
            {
                var t = M.T[ti];
                if (t.A != v && !res.Contains(t.A)) res.Add(t.A);
                if (t.B != v && !res.Contains(t.B)) res.Add(t.B);
                if (t.C != v && !res.Contains(t.C)) res.Add(t.C);
            }
            return res;
        }

        public List<int> Regions(int v, List<int> res)
        {
            res.Clear();
            foreach (int ti in VT[v])
            {
                var t = M.T[ti];
                if (!res.Contains(t.Neg)) res.Add(t.Neg);
                if (!res.Contains(t.Pos)) res.Add(t.Pos);
            }
            res.Sort();
            return res;
        }

        public bool EdgeIsBorder(int a, int b) { return M.IsBorder(EdgeTris(a, b, scratch)); }

        // Number of border edges at v.
        public int BorderCount(int v)
        {
            int n = 0;
            var ring = Ring(v, new List<int>(8));
            foreach (int w in ring) if (EdgeIsBorder(v, w)) n++;
            return n;
        }

        // All live edges, each once, as (min << 32 | max) keys.
        public long[] Edges()
        {
            var keys = new long[3 * M.T.Count];
            int n = 0;
            for (int i = 0; i < M.T.Count; i++)
            {
                if (Dead[i]) continue;
                var t = M.T[i];
                keys[n++] = FoamMesh.EdgeKey(t.A, t.B); keys[n++] = FoamMesh.EdgeKey(t.B, t.C); keys[n++] = FoamMesh.EdgeKey(t.C, t.A);
            }
            Array.Sort(keys, 0, n);
            int u = 0;
            for (int i = 0; i < n; i++) if (i == 0 || keys[i] != keys[i - 1]) keys[u++] = keys[i];
            Array.Resize(ref keys, u);
            return keys;
        }

        // Edges to examine: all of them (dirty == null, first pass), else only those touching a vertex
        // changed in the previous pass. Later passes then cost O(changes), not O(mesh).
        public long[] Edges(HashSet<int> dirty)
        {
            if (dirty == null) return Edges();
            var set = new HashSet<long>(LongKey.Comparer);
            var ring = new List<int>(8);
            foreach (int v in dirty)
            {
                if (v >= VT.Count) continue;
                foreach (int w in Ring(v, ring)) set.Add(FoamMesh.EdgeKey(v, w));
            }
            var keys = set.ToArray();
            Array.Sort(keys);
            return keys;
        }

        public bool[] BorderVertices(long[] edges)
        {
            var on = new bool[M.V.Count];
            foreach (long e in edges)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                if (EdgeIsBorder(a, b)) on[a] = on[b] = true;
            }
            return on;
        }

        public void Kill(int ti)
        {
            if (Dead[ti]) return;
            Dead[ti] = true;
            var t = M.T[ti];
            VT[t.A].Remove(ti); VT[t.B].Remove(ti); VT[t.C].Remove(ti);
        }

        // Drop dead triangles (and, via Clean, unused vertices and duplicates).
        public void Compact()
        {
            if (!Dead.Contains(true)) return;
            var keep = new List<FoamTri>(M.T.Count);
            for (int i = 0; i < M.T.Count; i++) if (!Dead[i]) keep.Add(M.T[i]);
            M.T = keep;
            M.Clean();
        }
    }

    // Optional per-region size scale in (0, 1] (index = region, [0] = outside = 1): edges touching
    // region k aim for L * scale[k], so small bubbles get proportionally finer triangles.
    // Passed as a parameter (not a static) because rebuilds run on a background thread.
    // How fast the target edge size may grow with distance: scale + Grading x (distance / L).
    public const double Grading = 0.3;

    // Per-vertex edge-size scale. A vertex starts at the smallest scale of the bubbles around it (a
    // wall between a big and a small bubble is meshed for the small one; the outside air and a host
    // solid don't constrain, so a big bubble's outer film may be coarser than the base edge). Then the
    // field is limited to grow by at most Grading x (distance / L) between neighbours, so sizes change
    // gradually. Without grading, a coarse big-bubble film met a fine border in one row of triangles:
    // noisy angles and a remesh every 10 steps (double bubble r 3 + r 0.6 at CellSize 0.1).
    public static double[] SizeField(FoamMesh m, double[] sc, double L)
    {
        int nv = m.V.Count;
        var s = new double[nv];
        if (sc == null) { for (int v = 0; v < nv; v++) s[v] = 1; return s; }
        for (int v = 0; v < nv; v++) s[v] = double.MaxValue;
        foreach (var t in m.T)
        {
            double ts = double.MaxValue;
            if (t.Neg > 0 && t.Neg < sc.Length) ts = Math.Min(ts, sc[t.Neg]);
            if (t.Pos > 0 && t.Pos < sc.Length) ts = Math.Min(ts, sc[t.Pos]);
            if (ts == double.MaxValue) continue;
            if (ts < s[t.A]) s[t.A] = ts;
            if (ts < s[t.B]) s[t.B] = ts;
            if (ts < s[t.C]) s[t.C] = ts;
        }
        for (int v = 0; v < nv; v++) if (s[v] == double.MaxValue) s[v] = 1;
        // Edges once, then relaxation sweeps (sizes only ever go down, so this terminates).
        var keys = new long[3 * m.T.Count];
        int n = 0;
        foreach (var t in m.T) { keys[n++] = FoamMesh.EdgeKey(t.A, t.B); keys[n++] = FoamMesh.EdgeKey(t.B, t.C); keys[n++] = FoamMesh.EdgeKey(t.C, t.A); }
        Array.Sort(keys, 0, n);
        double g = Grading / Math.Max(L, 1e-12);
        for (int sweep = 0; sweep < 60; sweep++)
        {
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && keys[i] == keys[i - 1]) continue;
                int a = (int)(keys[i] >> 32), b = (int)(keys[i] & 0xffffffff);
                double d = g * (m.V[a] - m.V[b]).Length;
                if (s[a] > s[b] + d) { s[a] = s[b] + d; changed = true; }
                else if (s[b] > s[a] + d) { s[b] = s[a] + d; changed = true; }
            }
            if (!changed) break;
        }
        return s;
    }

    // MaxPasses caps the inner repeat of each operation (split / collapse / flip). The mesher
    // wants full cleanup (6); the solver's periodic remesh uses 2.
    public static Result Run(FoamMesh m, double L, int iterations, double[] scale = null, int maxPasses = 6)
    {
        var r = new Result();
        for (int it = 0; it < iterations; it++)
        {
            r.Splits += SplitLong(m, 4.0 / 3.0 * L, true, scale, maxPasses);
            r.Collapses += CollapseShort(m, 0.8 * L, 4.0 / 3.0 * L, scale, maxPasses);
            r.Flips += FlipDelaunay(m, Math.Min(4, maxPasses));
        }
        return r;
    }

    // Also splits "chords": film edges whose two ends are both on Plateau borders. A film
    // strip made only of border vertices has no interior to bend, and its triangles lie along
    // the border, kinking it (found in the first GH run: period-3 oscillation).
    public static int SplitChords(FoamMesh m) { return SplitLong(m, double.MaxValue, true); }

    public static int SplitLong(FoamMesh m, double maxLen, bool chords = false, double[] scale = null, int maxPasses = 6)
    {
        var tp = new Topo(m, scale, maxLen < double.MaxValue ? maxLen * 0.75 : 1);
        var tris = new List<int>(4);
        int total = 0;
        List<bool> onBorder = chords ? new List<bool>(tp.BorderVertices(tp.Edges())) : null;
        HashSet<int> dirty = null;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var edges = tp.Edges(dirty);
            dirty = new HashSet<int>();
            var cand = new List<KeyValuePair<long, double>>();
            foreach (long e in edges)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                double len = (m.V[a] - m.V[b]).Length;
                tp.EdgeTris(a, b, tris);
                bool chord = chords && tris.Count == 2 && onBorder[a] && onBorder[b] && !m.IsBorder(tris);
                if (len > maxLen * tp.EdgeSize(a, b) || chord) cand.Add(new KeyValuePair<long, double>(e, len));
            }
            if (cand.Count == 0) break;
            cand.Sort((p, q) => q.Value.CompareTo(p.Value));
            int done = 0;
            foreach (var kv in cand)
            {
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                tp.EdgeTris(a, b, tris);
                if (tris.Count == 0) continue;            // already split this pass
                bool border = m.IsBorder(tris);
                int mid = tp.AddVertex((m.V[a] + m.V[b]) * 0.5, tp.EdgeSize(a, b));
                if (chords) onBorder.Add(border);         // a border's midpoint is on the border; a film edge's isn't
                foreach (int ti in tris.ToArray())
                {
                    var t = m.T[ti];
                    // Rotate so the split edge is (A,B), keeping orientation.
                    if (!IsEdge(t.A, t.B, a, b))
                    {
                        if (IsEdge(t.B, t.C, a, b)) t = new FoamTri(t.B, t.C, t.A, t.Neg, t.Pos);
                        else t = new FoamTri(t.C, t.A, t.B, t.Neg, t.Pos);
                    }
                    // (A, B, C) -> (A, mid, C) in place + new (mid, B, C).
                    m.T[ti] = new FoamTri(t.A, mid, t.C, t.Neg, t.Pos);
                    tp.VT[t.B].Remove(ti);
                    tp.VT[mid].Add(ti);
                    tp.AddTri(new FoamTri(mid, t.B, t.C, t.Neg, t.Pos));
                    dirty.Add(t.C);
                }
                dirty.Add(mid); dirty.Add(a); dirty.Add(b);
                done++;
            }
            total += done;
            if (done == 0) break;
        }
        return total;
    }

    public static bool[] BorderVertices(FoamMesh m, Dictionary<long, List<int>> edges)
    {
        var onBorder = new bool[m.V.Count];
        foreach (var kv in edges)
            if (m.IsBorder(kv.Value)) { onBorder[(int)(kv.Key >> 32)] = true; onBorder[(int)(kv.Key & 0xffffffff)] = true; }
        return onBorder;
    }

    static bool IsEdge(int p, int q, int a, int b) { return (p == a && q == b) || (p == b && q == a); }

    public static int CollapseShort(FoamMesh m, double minLen, double maxLen, double[] scale = null, int maxPasses = 6)
    {
        var tp = new Topo(m, scale, maxLen * 0.75);
        var etris = new List<int>(4);
        var regU = new List<int>(5); var regV = new List<int>(5);
        var ringU = new List<int>(8); var ringV = new List<int>(8); var ringD = new List<int>(8); var ringK = new List<int>(8);
        var opp = new List<int>(4);
        var et = new List<int>(4);
        int total = 0;
        HashSet<int> dirty = null;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var cand = new List<KeyValuePair<long, double>>();
            var scan = tp.Edges(dirty);
            dirty = new HashSet<int>();
            foreach (long e in scan)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                double len = (m.V[a] - m.V[b]).Length;
                if (len < minLen * tp.EdgeSize(a, b)) cand.Add(new KeyValuePair<long, double>(e, len));
            }
            if (cand.Count == 0) break;
            cand.Sort((p, q) => p.Value.CompareTo(q.Value));
            int done = 0;
            foreach (var kv in cand)
            {
                int u = (int)(kv.Key >> 32), v = (int)(kv.Key & 0xffffffff);
                tp.EdgeTris(u, v, etris);
                if (etris.Count == 0) continue;                          // gone (a neighbour collapsed)
                double len = (m.V[u] - m.V[v]).Length;
                if (len >= minLen * tp.EdgeSize(u, v)) continue; // no longer short
                bool borderEdge = m.IsBorder(etris);
                tp.Regions(u, regU); tp.Regions(v, regV);
                int bu = tp.BorderCount(u), bv = tp.BorderCount(v);
                int keep, drop, bKeep, bDrop;
                bool sameClass = regU.Count == regV.Count && Subset(regU, regV);
                if (Subset(regV, regU) && !(sameClass && bv > bu)) { keep = u; drop = v; bKeep = bu; bDrop = bv; }
                else if (Subset(regU, regV)) { keep = v; drop = u; bKeep = bv; bDrop = bu; }
                else continue;
                var regDrop = drop == u ? regU : regV;
                if (regDrop.Count >= 3 && !borderEdge) continue;              // borders collapse only along themselves
                if (bDrop > 2 || (bDrop > 0 && bKeep == 0)) continue;          // never remove nodes
                // Link condition: shared neighbours must be exactly the edge's opposite vertices.
                opp.Clear();
                foreach (int ti in etris)
                {
                    var t = m.T[ti];
                    if (t.A != u && t.A != v) opp.Add(t.A);
                    if (t.B != u && t.B != v) opp.Add(t.B);
                    if (t.C != u && t.C != v) opp.Add(t.C);
                }
                tp.Ring(u, ringU); tp.Ring(v, ringV);
                bool linkOk = true;
                foreach (int w in ringU) if (w != v && ringV.Contains(w) && !opp.Contains(w)) { linkOk = false; break; }
                if (!linkOk) continue;
                // Same-class collapses (film-film, along a border) go to the midpoint.
                Vec3 target = sameClass && bDrop == bKeep ? (m.V[u] + m.V[v]) * 0.5 : m.V[keep];
                // Don't create chords: a film vertex collapsing onto a border vertex must not
                // connect it to another border vertex except along an existing border edge.
                var rDrop = drop == u ? ringU : ringV;
                var rKeep = drop == u ? ringV : ringU;
                bool makesChord = false;
                if (bKeep > 0 && bDrop == 0)
                    foreach (int w in rDrop)
                    {
                        if (w == keep || tp.BorderCount(w) == 0) continue;
                        tp.EdgeTris(keep, w, et);
                        if (et.Count == 0 || !m.IsBorder(et)) { makesChord = true; break; }
                    }
                if (makesChord) continue;
                bool tooLong = false;
                double maxHere = maxLen * tp.EdgeSize(u, v);
                foreach (int w in rDrop) if ((m.V[w] - target).Length > maxHere) { tooLong = true; break; }
                foreach (int w in rKeep) if (!tooLong && (m.V[w] - target).Length > maxHere) tooLong = true;
                if (tooLong) continue;
                if (!KeepsOrientation(m, tp.VT[drop], drop, keep, target)) continue;
                if (!KeepsOrientation(m, tp.VT[keep], keep, keep, target)) continue;

                // Collapse drop -> keep.
                foreach (int ti in etris.ToArray()) tp.Kill(ti);
                foreach (int ti in tp.VT[drop].ToArray())
                {
                    var t = m.T[ti];
                    if (t.A == drop) t.A = keep;
                    if (t.B == drop) t.B = keep;
                    if (t.C == drop) t.C = keep;
                    m.T[ti] = t;
                    tp.VT[keep].Add(ti);
                }
                tp.VT[drop].Clear();
                m.V[keep] = target;
                tp.Size[keep] = Math.Min(tp.Size[keep], tp.Size[drop]);
                dirty.Add(keep);
                foreach (int w in rKeep) dirty.Add(w);
                foreach (int w in rDrop) dirty.Add(w);
                dirty.Remove(drop);
                done++;
            }
            total += done;
            if (done == 0) break;
        }
        tp.Compact();
        return total;
    }

    static bool Subset(List<int> small, List<int> big)
    {
        foreach (int x in small) if (!big.Contains(x)) return false;
        return true;
    }

    // Moving vertex `moved` to `target` (and renaming it `keep`) must not flip or squash
    // any triangle that survives the collapse.
    static bool KeepsOrientation(FoamMesh m, List<int> tris, int moved, int keep, Vec3 target)
    {
        foreach (int ti in tris)
        {
            var t = m.T[ti];
            if (moved != keep && (t.A == keep || t.B == keep || t.C == keep)) continue; // removed
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            Vec3 n0 = Vec3.Cross(b - a, c - a);
            double scale = (b - a).LengthSq + (c - a).LengthSq;
            if (t.A == moved) a = target;
            if (t.B == moved) b = target;
            if (t.C == moved) c = target;
            Vec3 n1 = Vec3.Cross(b - a, c - a);
            if (n0.Length < 1e-6 * scale) continue;
            if (Vec3.Dot(n0, n1) <= 0.3 * n0.Length * n1.Length) return false;
        }
        return true;
    }

    // Flip interior film edges (exactly two triangles of the same film) when the sum of the
    // opposite angles exceeds 180 degrees (Delaunay criterion), if the result doesn't fold.
    public static int FlipDelaunay(FoamMesh m, int maxPasses = 4)
    {
        var tp = new Topo(m);
        var tris = new List<int>(4);
        int total = 0;
        // Flips never touch borders, so which vertices are on one doesn't change.
        var onBorder = tp.BorderVertices(tp.Edges());
        HashSet<int> dirty = null;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            var edges = tp.Edges(dirty);
            dirty = new HashSet<int>();
            var touched = new bool[m.T.Count];
            int done = 0;
            foreach (long e in edges)
            {
                int a = (int)(e >> 32), b = (int)(e & 0xffffffff);
                tp.EdgeTris(a, b, tris);
                if (tris.Count != 2) continue;
                int t0 = tris[0], t1 = tris[1];
                if (touched[t0] || touched[t1]) continue;
                var A = m.T[t0]; var B = m.T[t1];
                if (A.Neg != B.Neg || A.Pos != B.Pos) continue;
                // Orient A as (p, q, x) with edge p->q, B must then contain q->p.
                A = RotateTo(A, a, b);
                if (A.A < 0) { A = RotateTo(m.T[t0], b, a); if (A.A < 0) continue; }
                int p = A.A, q = A.B, x = A.C;
                var Bq = RotateTo(B, q, p);
                if (Bq.A < 0) continue; // inconsistent orientation
                int y = Bq.C;
                if (x == y) continue;
                if (tp.EdgeExists(x, y)) continue;
                // An "ear" has all three corners on a Plateau border: it lies along the border
                // instead of spanning the film, kinks it, and makes the solver oscillate.
                // Flip it away whenever the other side's apex is off the border.
                bool ear = onBorder[p] && onBorder[q] && (onBorder[x] != onBorder[y]);
                double angX = Angle(m.V[p] - m.V[x], m.V[q] - m.V[x]);
                double angY = Angle(m.V[p] - m.V[y], m.V[q] - m.V[y]);
                if (!ear && angX + angY <= Math.PI + 1e-9) continue;
                // New triangles (x, p, y) and (y, q, x) — check they don't fold.
                Vec3 n0 = Vec3.Cross(m.V[q] - m.V[p], m.V[x] - m.V[p]) + Vec3.Cross(m.V[p] - m.V[q], m.V[y] - m.V[q]);
                Vec3 n1 = Vec3.Cross(m.V[p] - m.V[x], m.V[y] - m.V[x]);
                Vec3 n2 = Vec3.Cross(m.V[q] - m.V[y], m.V[x] - m.V[y]);
                if (Vec3.Dot(n1, n0) <= 0 || Vec3.Dot(n2, n0) <= 0) continue;
                if (!ear && Vec3.Dot(n1, n2) <= 0.5 * n1.Length * n2.Length) continue;
                // t0 (p, q, x) -> (x, p, y): loses q, gains y.  t1 (q, p, y) -> (y, q, x): loses p, gains x.
                m.T[t0] = new FoamTri(x, p, y, A.Neg, A.Pos);
                m.T[t1] = new FoamTri(y, q, x, A.Neg, A.Pos);
                tp.VT[q].Remove(t0); tp.VT[y].Add(t0);
                tp.VT[p].Remove(t1); tp.VT[x].Add(t1);
                touched[t0] = touched[t1] = true;
                dirty.Add(p); dirty.Add(q); dirty.Add(x); dirty.Add(y);
                done++;
            }
            total += done;
            if (done == 0) break;
        }
        return total;
    }

    // Rotate t so that its first two vertices are (a, b) in that order; A = -1 if impossible.
    static FoamTri RotateTo(FoamTri t, int a, int b)
    {
        if (t.A == a && t.B == b) return t;
        if (t.B == a && t.C == b) return new FoamTri(t.B, t.C, t.A, t.Neg, t.Pos);
        if (t.C == a && t.A == b) return new FoamTri(t.C, t.A, t.B, t.Neg, t.Pos);
        return new FoamTri(-1, -1, -1, t.Neg, t.Pos);
    }

    static double Angle(Vec3 u, Vec3 v)
    {
        double c = Vec3.Dot(u, v) / Math.Max(u.Length * v.Length, 1e-300);
        return Math.Acos(Math.Max(-1, Math.Min(1, c)));
    }

    // Edge incidence histogram: [1] open edges (should be 0), [2] film (incl. contact lines on a host), [3] border, [4+] errors.
    public static int[] EdgeHistogram(FoamMesh m)
    {
        var h = new int[6];
        foreach (var kv in m.EdgeTris()) h[Math.Min(kv.Value.Count, 5)]++;
        return h;
    }
}

// ---- core/FoamSolver.cs ----
public class SolverSettings
{
    public double Tension = 1.0;     // film tension (2 surfaces x surface tension); pressures scale with it
    public double Dt = 0.2;          // time step, in units of (mean edge length)^2
    public double MaxMove = 0.25;    // per-step displacement cap, in mean edge lengths
    public double Smooth = 0.3;      // tangential smoothing per step (0 = off); doesn't change the shape
    public int VolumeIters = 1;      // Newton corrections back to target volumes per step
    public double EdgeLength = 0;    // remesh target edge length (0 = keep the mesher's cell size)
    public int RemeshEvery = 10;     // steps between remesh checks (0 = never); remeshes only if edges are out of range
    public int MaintainEvery = 50;   // steps between light passes (split border chords + Delaunay flips); 0 = never
    public double Momentum = 0.9;    // heavy-ball momentum (0 = plain gradient flow); damps toward rest, no wobble physics
    public double MobilityFloor = 0.3; // min vertex area in the mobility, x EdgeLength^2 (stability)

    // Rearrangement (topology changes by rebuild, see Rearranger).
    public bool Rearrange = true;
    public int RearrangeEvery = 25;  // steps between checks
    public int RearrangeCooldown = 75; // steps after a rebuild before checking again
    public int RearrangeWarmup = 100; // no checks before this step: bubbles are still inflating to their volumes, fits fail
    public double PinchAngle = 25;   // degrees: mean smallest film angle along a border that counts as pinched
    public double ShortBorder = 0.5; // border between two nodes shorter than this x edge length → rebuild
    public double FitPrior = 0.01;   // weight of the sphere prior in the rebuild fit
    public bool InteriorFitBySize = true; // rebuild fit: interior bubbles take A from their size, not their pressure
    public bool AsyncRearrange = false; // build the new mesh on a background thread (GH: true, so Rhino doesn't freeze)
    public int SimulatedDelay = 0;      // harness: with AsyncRearrange, build at once but apply exactly this many steps later (deterministic replays)
    public double RebuildGridFactor = 1.5; // rebuild grid = edge x this (1.5: ~35% faster than 1, same quality; 2 changed topology)
    public int RebuildRemeshIters = 2;
    public bool TransferShape = true; // after a rebuild, put the old film shape back outside the event zone (ShapeTransfer)

    // Adaptive size: about this many edges across each bubble's radius (0 = uniform), never
    // finer than 1/4 of the base edge. Small bubbles otherwise get too few triangles to hold
    // their shape and are crushed (15-bubble GH run, r 0.35 at cell 0.1).
    public double EdgesPerRadius = 6;

    // Live control (Phase 2).
    public double VolumeRate = 0.02; // max change of a bubble's target volume per step, as a fraction (smooth inflate/deflate)
    public double Pull = 0;          // strength of the pull toward each bubble's PullTarget (0 = off; ~1 moderate, ~5 stiff)
    public double Gather = 0;        // every bubble drifts toward GatherTo (0 = off; ~0.2 gentle, ~1 firm), like floating bubbles collecting
    public Vec3 GatherTo;
}

// Surface Evolver-style gradient flow: minimise total film area with each bubble's volume
// fixed. Pressures are the Lagrange multipliers. Plateau's laws are not imposed; they are
// what the minimum looks like, so the 120 / 109.47 degree angles are a check, not an input.
//
// Performance note: Rhino compiles script components without optimisation (measured ~6x
// slower than Release), so the per-step code works on flat double/int arrays with the vector
// math written out by hand instead of Vec3 operators, and topology lookups are precomputed
// in RebuildTopology. Keep new hot-loop code in this style.
public class FoamSolver
{
    public FoamMesh Mesh;
    public SolverSettings S;
    public double[] TargetVolume;    // per region, index 0 unused: the volume enforced this step
    public double[] DesiredVolume;   // per region: where TargetVolume is heading (ramped by VolumeRate)
    public Vec3[] PullTarget;        // per region: point the bubble is pulled toward (if PullOn)
    // Per region: an extra force density set by generators (e.g. a stream's flow), applied like Pull/Gather
    // (volume-neutral). Indices beyond the array, or null, mean no force.
    public Vec3[] ExtForce;
    public bool[] PullOn;
    public double[] Pressure;        // per region relative to outside, index 0 = 0
    public int Steps;
    public double Area, Residual, MaxVolumeError;
    public double EdgeLength;        // length scale for dt, cap, mobility floor and remesh target
    public Remesher.Result LastRemesh;
    public HostSurface Host;         // optional solid the bubbles rest on (see HostSurface); set before stepping
    public int Rearrangements;
    public List<string> RearrangeLog = new List<string>();  // newest last; read and clear by the caller
    int lastRearrangeStep = -1000000, failedRebuilds;
    public double TargetEdge { get { return S.EdgeLength > 0 ? S.EdgeLength : lockedEdge; } }
    public List<long> BorderEdges = new List<long>();
    public List<int> Nodes = new List<int>();

    // ---- topology (rebuilt when connectivity changes) ----
    int nv, nt, nslot;
    int[] tv;                        // 3 per triangle: vertex ids
    int[] cNeg, cPos;                // 3 per triangle: slot of the Neg / Pos region at that corner (-1 = outside)
    int[] tNeg, tPos;                // per triangle regions
    int[] slotStart;                 // per vertex: first slot (nv + 1 entries)
    int[] slotRegion;                // per slot: bubble id (1..n)
    int[] cls;                       // 0 film, 1 border line, 2 node
    int[] nbStart, nbList;           // smoothing neighbours (CSR): film one-ring / 2 border neighbours

    // ---- per-step work arrays ----
    double[] X, X0, prev;            // positions, positions at step start, last displacement (3 per vertex)
    double[] GA;                     // area gradient (3 per vertex)
    double[] VA;                     // vertex area (1/3 of incident triangles)
    double[] GV;                     // volume gradient per slot (3 per slot)
    double[] vol;                    // per region
    double[] nrm, nxt;               // smoothing scratch (3 per vertex)
    double lockedEdge;               // remesh target fixed at the first rebuild so it doesn't drift
    public double[] RegionScale;     // per region edge-size scale (see SolverSettings.EdgesPerRadius)
    double[] vScale;                 // per vertex: smallest scale of its regions

    public FoamSolver(FoamMesh mesh, SolverSettings s)
    {
        Mesh = mesh;
        S = s ?? new SolverSettings();
        TargetVolume = mesh.Volumes();
        DesiredVolume = (double[])TargetVolume.Clone();
        PullTarget = new Vec3[mesh.BubbleCount + 1];
        PullOn = new bool[mesh.BubbleCount + 1];
        Pressure = new double[mesh.BubbleCount + 1];
        lockedEdge = EdgeLength = mesh.MeanEdgeLength();
        var radii = new double[mesh.BubbleCount];
        for (int k = 1; k <= mesh.BubbleCount; k++)
            radii[k - 1] = Math.Pow(3 * Math.Max(TargetVolume[k], 0) / (4 * Math.PI), 1.0 / 3);
        RegionScale = MobiusMesher.SizeScale(radii, lockedEdge, S.EdgesPerRadius);
        RebuildTopology();
        // The mesher's lockedEdge guess is the mean edge, which adaptive sizing pulls down:
        // use the base (largest) target instead when some bubbles are refined.
        if (RegionScale.Skip(1).Any(x => x < 1))
        {
            double maxScaleMean = 0; int cnt = 0;
            foreach (var t in mesh.T)
                if (t.Neg >= 0 && t.Pos >= 0 && Math.Abs(RegionScale[t.Neg] - 1) < 1e-9 && Math.Abs(RegionScale[t.Pos] - 1) < 1e-9)
                {
                    maxScaleMean += (mesh.V[t.B] - mesh.V[t.A]).Length; cnt++;
                }
            if (cnt > 0) { lockedEdge = EdgeLength = maxScaleMean / cnt; }
        }
    }

    public int TopologyVersion;      // bumps whenever connectivity changes (viewers cache on it)

    bool[] wetT;                     // per triangle: a footprint on the host (no tension)
    int[] wetList;                   // vertices on a footprint (slide on the host)

    public void RebuildTopology()
    {
        TopologyVersion++;
        var m = Mesh;
        if (Host != null) MobiusMesher.SnapToHost(m, Host);
        nv = m.V.Count; nt = m.T.Count;
        var regions = m.VertexRegions();
        slotStart = new int[nv + 1];
        var sr = new List<int>();
        for (int v = 0; v < nv; v++)
        {
            slotStart[v] = sr.Count;
            foreach (int r in regions[v]) if (r > 0) sr.Add(r);
        }
        slotStart[nv] = sr.Count;
        slotRegion = sr.ToArray();
        vScale = new double[nv];
        for (int v = 0; v < nv; v++)
        {
            double sc = 1;
            if (RegionScale != null)
                foreach (int r in regions[v]) if (r >= 0 && r < RegionScale.Length && RegionScale[r] < sc) sc = RegionScale[r];
            vScale[v] = sc;
        }
        nslot = slotRegion.Length;

        tv = new int[3 * nt]; cNeg = new int[3 * nt]; cPos = new int[3 * nt];
        tNeg = new int[nt]; tPos = new int[nt];
        wetT = new bool[nt];
        var wetV = new bool[m.V.Count];
        for (int t = 0; t < nt; t++)
        {
            var f = m.T[t];
            tv[3 * t] = f.A; tv[3 * t + 1] = f.B; tv[3 * t + 2] = f.C;
            tNeg[t] = f.Neg; tPos[t] = f.Pos;
            if (FoamMesh.IsSolid(f)) { wetT[t] = true; wetV[f.A] = wetV[f.B] = wetV[f.C] = true; }
            for (int k = 0; k < 3; k++)
            {
                cNeg[3 * t + k] = SlotOf(tv[3 * t + k], f.Neg);
                cPos[3 * t + k] = SlotOf(tv[3 * t + k], f.Pos);
            }
        }

        var edges = m.EdgeTris();
        var ring = new HashSet<int>[nv];
        var border = new List<int>[nv];
        for (int v = 0; v < nv; v++) { ring[v] = new HashSet<int>(); border[v] = new List<int>(2); }
        BorderEdges.Clear();
        foreach (var kv in edges)
        {
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            ring[a].Add(b); ring[b].Add(a);
            if (m.IsBorder(kv.Value))
            {
                BorderEdges.Add(kv.Key);
                border[a].Add(b); border[b].Add(a);
            }
        }
        cls = new int[nv];
        nbStart = new int[nv + 1];
        var nl = new List<int>();
        Nodes.Clear();
        for (int v = 0; v < nv; v++)
        {
            nbStart[v] = nl.Count;
            if (border[v].Count == 0) { cls[v] = 0; nl.AddRange(ring[v]); }
            else if (border[v].Count == 2) { cls[v] = 1; nl.AddRange(border[v]); }
            else { cls[v] = 2; Nodes.Add(v); }
        }
        nbStart[nv] = nl.Count;
        nbList = nl.ToArray();
        var wl = new List<int>();
        for (int v = 0; v < nv; v++) if (wetV[v]) wl.Add(v);
        wetList = wl.ToArray();

        X = new double[3 * nv]; X0 = new double[3 * nv]; GA = new double[3 * nv]; VA = new double[nv];
        nrm = new double[3 * nv]; nxt = new double[3 * nv];
        GV = new double[3 * nslot];
        vol = new double[m.BubbleCount + 1];
        if (prev == null || prev.Length != 3 * nv) prev = new double[3 * nv];
    }

    int SlotOf(int v, int region)
    {
        if (region == 0) return -1;
        for (int s = slotStart[v]; s < slotStart[v + 1]; s++) if (slotRegion[s] == region) return s;
        return -1;
    }

    public int VertexClass(int v) { return cls[v]; }

    // Target volumes from the requested radii (4/3 pi r^3): each bubble keeps the air it was
    // blown with. Without this the targets are the starting diagram's cells, which neighbours
    // have already carved. Small packed bubbles were then squeezed to a fraction of their
    // intended size (15-bubble GH run). Also resets the adaptive size scale.
    public void SetTargetRadii(IList<double> radii)
    {
        for (int k = 1; k <= Mesh.BubbleCount && k - 1 < radii.Count; k++)
            DesiredVolume[k] = TargetVolume[k] = 4.0 / 3.0 * Math.PI * radii[k - 1] * radii[k - 1] * radii[k - 1];
        RegionScale = MobiusMesher.SizeScale(radii, lockedEdge, S.EdgesPerRadius);
        MarkDead();
        RebuildTopology();
    }

    // Bubbles with no volume in the mesh (smaller than the grid at build, or swallowed while
    // packing) are excluded: target 0, never counted in volume error, never block a rebuild.
    // In Alex's run an r 0.09 bubble at CellSize 0.15 never existed, but it kept the volume error at
    // 100% and made every rebuild refuse ("would vanish (NaN)").
    public List<int> DeadBubbles = new List<int>();

    public void MarkDead()
    {
        var vol = Mesh.Volumes();
        for (int k = 1; k <= Mesh.BubbleCount; k++)
            if (!(vol[k] > 1e-12) && !DeadBubbles.Contains(k))
            {
                DeadBubbles.Add(k);
                TargetVolume[k] = DesiredVolume[k] = 0;
            }
    }

    public bool IsDead(int k) { return DeadBubbles.Contains(k); }

    // Live resize: bubbles inflate/deflate toward these radii at S.VolumeRate per step.
    // Radii beyond the list (bubbles added later) keep their current desired size.
    public void SetDesiredRadii(IList<double> radii)
    {
        bool scaleChanged = false;
        for (int k = 1; k <= Mesh.BubbleCount && k - 1 < radii.Count; k++)
        {
            if (IsDead(k)) continue; // a bubble that doesn't exist can't be inflated (Reset to rebuild it)
            DesiredVolume[k] = 4.0 / 3.0 * Math.PI * radii[k - 1] * radii[k - 1] * radii[k - 1] + (k < mergedVolume.Length ? mergedVolume[k] : 0);
            double sc = MobiusMesher.ScaleFor(radii[k - 1], lockedEdge, S.EdgesPerRadius);
            if (Math.Abs(sc - RegionScale[k]) > 0.05) { RegionScale[k] = sc; scaleChanged = true; sizeFieldVersion = -1; }
        }
        if (scaleChanged) UpdateVertexScale();
    }

    public double DesiredRadius(int k) { return Math.Pow(3 * DesiredVolume[k] / (4 * Math.PI), 1.0 / 3); }

    // ---- free bubbles (see FreeBubble) ----
    readonly List<KeyValuePair<Vec3, double>> pendingInserts = new List<KeyValuePair<Vec3, double>>();
    public int PendingInserts { get { return pendingInserts.Count; } }

    // Drop a free bubble (centre, radius) into open space with no rebuild. Queued, and applied only when no
    // rebuild is running (a rebuild started earlier would replace the mesh and lose it). The caller should
    // keep it clear of the foam; if it overlaps, it's added through a normal (rebuild) add instead.
    public void RequestInsertFree(Vec3 center, double radius) { pendingInserts.Add(new KeyValuePair<Vec3, double>(center, radius)); }

    void ApplyInserts()
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var ins in pendingInserts.ToList())
        {
            pendingInserts.Remove(ins);
            Vec3 c = ins.Key;
            double r = Math.Max(ins.Value, TargetEdge * S.RebuildGridFactor);
            double gap = double.MaxValue;
            foreach (var q in Mesh.V) gap = Math.Min(gap, (q - c).Length - r);
            if (Host != null) gap = Math.Min(gap, Host.Height(c) - r);
            if (gap < 2 * TargetEdge)
            {
                RequestAdd(r, 1, true, c);
                RearrangeLog.Add("step " + Steps + ": free bubble at " + c + " overlaps the foam; added with a rebuild instead");
                continue;
            }
            int k = Mesh.BubbleCount + 1;
            double scale = MobiusMesher.ScaleFor(r, TargetEdge, S.EdgesPerRadius);
            FreeBubble.Append(Mesh, k, c, r, TargetEdge * scale);
            Mesh.BubbleCount = k;
            GrowRegion(k, Mesh.Volumes()[k], 4.0 / 3.0 * Math.PI * r * r * r, scale);
            Added++;
            prev = null;
            RebuildTopology();
            RearrangeLog.Add("step " + Steps + ": free bubble " + k + " (r " + r.ToString("G3", ci) + ") inserted at " + c);
        }
    }

    // Extend every per-region array to region k.
    void GrowRegion(int k, double target, double desired, double scale)
    {
        TargetVolume = Grow(TargetVolume, k, target);
        DesiredVolume = Grow(DesiredVolume, k, desired);
        Pressure = Grow(Pressure, k, 0);
        RegionScale = Grow(RegionScale, k, scale);
        var pt = new Vec3[k + 1]; Array.Copy(PullTarget, pt, Math.Min(PullTarget.Length, k + 1)); PullTarget = pt;
        var po = new bool[k + 1]; Array.Copy(PullOn, po, Math.Min(PullOn.Length, k + 1)); PullOn = po;
    }

    // ---- popping (see Pop) ----
    // Volume a bubble took over from merged neighbours; SetDesiredRadii adds it back on top of the
    // radius input, so a later slider change doesn't shrink the merged bubble back to one input.
    double[] mergedVolume = new double[0];
    readonly List<KeyValuePair<int, Vec3>> pendingPops = new List<KeyValuePair<int, Vec3>>();
    public int Popped;
    public int PendingPops { get { return pendingPops.Count; } }

    // Pop bubble k, or (k = 0) the bubble at point `at`, which is resolved when the pop happens.
    // Applied at the next step with no background rebuild running (that rebuild would bring the
    // bubble back).
    public void RequestPop(int k, Vec3 at = default(Vec3)) { pendingPops.Add(new KeyValuePair<int, Vec3>(k, at)); }

    void ApplyPops()
    {
        foreach (var req in pendingPops.ToList())
        {
            pendingPops.Remove(req);
            int k = req.Key > 0 ? req.Key : Pop.BubbleAt(Mesh, req.Value);
            var res = Pop.Apply(Mesh, k);
            RearrangeLog.Add("step " + Steps + ": " + res);
            if (res.Fail != null) continue;
            if (res.Into > 0)
            {
                int j = res.Into;
                TargetVolume[j] += res.Volume;
                DesiredVolume[j] += DesiredVolume[k];
                if (mergedVolume.Length <= j) Array.Resize(ref mergedVolume, Mesh.BubbleCount + 1);
                mergedVolume[j] += DesiredVolume[k]; // already includes anything k had merged
                if (PullOn[k] && !PullOn[j]) { PullOn[j] = true; PullTarget[j] = PullTarget[k]; }
            }
            PullOn[k] = false;
            TargetVolume[k] = DesiredVolume[k] = 0;
            Pressure[k] = 0;
            if (!DeadBubbles.Contains(k)) DeadBubbles.Add(k);
            Popped++;
            prev = null;
            RebuildTopology();
        }
    }

    void UpdateVertexScale()
    {
        for (int v = 0; v < nv; v++)
        {
            double sc = 1;
            for (int sl = slotStart[v]; sl < slotStart[v + 1]; sl++)
                if (RegionScale[slotRegion[sl]] < sc) sc = RegionScale[slotRegion[sl]];
            vScale[v] = sc;
        }
    }

    // Volume-weighted centroid of each bubble (index 0 unused).
    public Vec3[] Centroids()
    {
        var m = Mesh;
        var c = new Vec3[m.BubbleCount + 1];
        var v = new double[m.BubbleCount + 1];
        foreach (var t in m.T)
        {
            Vec3 a = m.V[t.A], b = m.V[t.B], cc = m.V[t.C];
            double tv = Vec3.Dot(a, Vec3.Cross(b, cc)) / 6.0;
            Vec3 tc = (a + b + cc) / 4.0;
            if (t.Neg > 0) { v[t.Neg] += tv; c[t.Neg] = c[t.Neg] + tc * tv; }
            if (t.Pos > 0) { v[t.Pos] -= tv; c[t.Pos] = c[t.Pos] - tc * tv; }
        }
        for (int k = 1; k <= m.BubbleCount; k++) if (v[k] > 0) c[k] = c[k] / v[k];
        return c;
    }

    void Load()
    {
        var V = Mesh.V;
        for (int v = 0; v < nv; v++) { var p = V[v]; X[3 * v] = p.X; X[3 * v + 1] = p.Y; X[3 * v + 2] = p.Z; }
    }

    void Store()
    {
        var V = Mesh.V;
        for (int v = 0; v < nv; v++) V[v] = new Vec3(X[3 * v], X[3 * v + 1], X[3 * v + 2]);
    }

    // One pass over the triangles: volumes per region and their gradients per slot; with
    // withArea also the area gradient, vertex areas and the total area.
    double Pass(bool withArea)
    {
        Array.Clear(GV, 0, GV.Length);
        Array.Clear(vol, 0, vol.Length);
        if (withArea) { Array.Clear(GA, 0, GA.Length); Array.Clear(VA, 0, VA.Length); }
        double area = 0;
        double[] x = X;
        for (int t = 0; t < nt; t++)
        {
            int ia = 3 * tv[3 * t], ib = 3 * tv[3 * t + 1], ic = 3 * tv[3 * t + 2];
            double ax = x[ia], ay = x[ia + 1], az = x[ia + 2];
            double bx = x[ib], by = x[ib + 1], bz = x[ib + 2];
            double cx = x[ic], cy = x[ic + 1], cz = x[ic + 2];
            if (withArea && !wetT[t])   // footprints on a host carry no tension
            {
                double ux = bx - ax, uy = by - ay, uz = bz - az, wx = cx - ax, wy = cy - ay, wz = cz - az;
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-300)
                {
                    double s = 0.5 / len; nx *= s; ny *= s; nz *= s; // half unit normal
                    // grad_a = 0.5 n x (c - b), grad_b = 0.5 n x (a - c), grad_c = 0.5 n x (b - a)
                    double ex = cx - bx, ey = cy - by, ez = cz - bz;
                    GA[ia] += ny * ez - nz * ey; GA[ia + 1] += nz * ex - nx * ez; GA[ia + 2] += nx * ey - ny * ex;
                    ex = ax - cx; ey = ay - cy; ez = az - cz;
                    GA[ib] += ny * ez - nz * ey; GA[ib + 1] += nz * ex - nx * ez; GA[ib + 2] += nx * ey - ny * ex;
                    ex = bx - ax; ey = by - ay; ez = bz - az;
                    GA[ic] += ny * ez - nz * ey; GA[ic + 1] += nz * ex - nx * ez; GA[ic + 2] += nx * ey - ny * ex;
                    double third = len / 6.0;
                    VA[ia / 3] += third; VA[ib / 3] += third; VA[ic / 3] += third;
                    area += 0.5 * len;
                }
            }
            // Volume: v = a.(b x c)/6; grad_a = (b x c)/6, grad_b = (c x a)/6, grad_c = (a x b)/6.
            double dax = (by * cz - bz * cy) / 6, day = (bz * cx - bx * cz) / 6, daz = (bx * cy - by * cx) / 6;
            double dbx = (cy * az - cz * ay) / 6, dby = (cz * ax - cx * az) / 6, dbz = (cx * ay - cy * ax) / 6;
            double dcx = (ay * bz - az * by) / 6, dcy = (az * bx - ax * bz) / 6, dcz = (ax * by - ay * bx) / 6;
            double tvol = ax * dax + ay * day + az * daz;
            int k0 = 3 * t;
            int neg = tNeg[t], pos = tPos[t];
            if (neg > 0)
            {
                vol[neg] += tvol;
                int s0 = 3 * cNeg[k0], s1 = 3 * cNeg[k0 + 1], s2 = 3 * cNeg[k0 + 2];
                GV[s0] += dax; GV[s0 + 1] += day; GV[s0 + 2] += daz;
                GV[s1] += dbx; GV[s1 + 1] += dby; GV[s1 + 2] += dbz;
                GV[s2] += dcx; GV[s2 + 1] += dcy; GV[s2 + 2] += dcz;
            }
            if (pos > 0)
            {
                vol[pos] -= tvol;
                int s0 = 3 * cPos[k0], s1 = 3 * cPos[k0 + 1], s2 = 3 * cPos[k0 + 2];
                GV[s0] -= dax; GV[s0 + 1] -= day; GV[s0 + 2] -= daz;
                GV[s1] -= dbx; GV[s1 + 1] -= dby; GV[s1 + 2] -= dbz;
                GV[s2] -= dcx; GV[s2 + 1] -= dcy; GV[s2 + 2] -= dcz;
            }
        }
        // Footprint vertices can only slide on the host: keep the tangential part of every force.
        // The pressure solve then sees the same constraint, and volumes stay exact.
        if (Host != null)
            foreach (int v in wetList)
            {
                int i = 3 * v;
                Vec3 n = Host.Normal(new Vec3(x[i], x[i + 1], x[i + 2]));
                if (withArea)
                {
                    double d = GA[i] * n.X + GA[i + 1] * n.Y + GA[i + 2] * n.Z;
                    GA[i] -= d * n.X; GA[i + 1] -= d * n.Y; GA[i + 2] -= d * n.Z;
                }
                for (int sl = slotStart[v]; sl < slotStart[v + 1]; sl++)
                {
                    int j = 3 * sl;
                    double d = GV[j] * n.X + GV[j + 1] * n.Y + GV[j + 2] * n.Z;
                    GV[j] -= d * n.X; GV[j + 1] -= d * n.Y; GV[j + 2] -= d * n.Z;
                }
            }
        return area;
    }

    // Put footprint vertices back on the host (moves are tangential to first order; a curved host
    // needs the correction).
    void SnapWet()
    {
        if (Host == null) return;
        foreach (int v in wetList)
        {
            int i = 3 * v;
            var q = Host.Project(new Vec3(X[i], X[i + 1], X[i + 2]));
            X[i] = q.X; X[i + 1] = q.Y; X[i + 2] = q.Z;
        }
    }

    double Mob(int v)
    {
        double floor = S.MobilityFloor * EdgeLength * EdgeLength;
        double a = VA[v];
        return 1.0 / (a > floor ? a : floor);
    }

    // True when more than 1% of edges have drifted outside [0.5 L, 1.6 L] x the local size.
    // Uses the remesher's graded size field (cached per topology version: vertices move slowly).
    double[] sizeField; int sizeFieldVersion = -1; double[] sizeFieldScale;
    public bool NeedsRemesh()
    {
        double L = S.EdgeLength > 0 ? S.EdgeLength : lockedEdge;
        double lo = 0.25 * L * L, hi = 2.56 * L * L;
        if (sizeFieldVersion != TopologyVersion || sizeFieldScale != RegionScale)
        {
            sizeField = Remesher.SizeField(Mesh, RegionScale, L);
            sizeFieldVersion = TopologyVersion; sizeFieldScale = RegionScale;
        }
        int bad = 0;
        var V = Mesh.V;
        foreach (var t in Mesh.T)
        {
            Vec3 a = V[t.A], b = V[t.B];
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z, l2 = dx * dx + dy * dy + dz * dz;
            if (sizeField != null && t.A < sizeField.Length && t.B < sizeField.Length)
            {
                double fs = Math.Min(sizeField[t.A], sizeField[t.B]); fs *= fs;
                if (l2 < lo * fs || l2 > hi * fs) bad++;
                continue;
            }
            // Same rule as Remesher.EdgeScale: the smaller bubble decides; outside / solid don't constrain.
            double sc = 1;
            if (RegionScale != null)
            {
                double sn = t.Neg > 0 ? RegionScale[t.Neg] : double.MaxValue, sp = t.Pos > 0 ? RegionScale[t.Pos] : double.MaxValue;
                sc = Math.Min(sn, sp);
                if (sc == double.MaxValue) sc = 1;
            }
            sc *= sc;
            if (l2 < lo * sc || l2 > hi * sc) bad++;
        }
        return bad > 0.01 * Mesh.T.Count;
    }

    public void Remesh()
    {
        double L = S.EdgeLength > 0 ? S.EdgeLength : lockedEdge;
        LastRemesh = Remesher.Run(Mesh, L, 1, RegionScale, 2);
        prev = null;
        RebuildTopology();
        EdgeLength = L;
    }

    // Light pass: split border chords + Delaunay flips, without the full remesh.
    public void Maintain()
    {
        int changed = Remesher.SplitChords(Mesh) + Remesher.FlipDelaunay(Mesh, 2);
        if (changed == 0) return;
        if (Mesh.V.Count != nv) prev = null;
        RebuildTopology();
    }

    // Swap in a rebuilt mesh (same bubbles, same target volumes).
    public void ReplaceMesh(FoamMesh mesh)
    {
        Mesh = mesh;
        prev = null;
        RebuildTopology();
        EdgeLength = TargetEdge;
    }

    Rearranger.RebuildJob pendingJob;
    System.Threading.Tasks.Task pendingTask;
    public bool RebuildPending { get { return pendingJob != null; } }
    int pendingReadyStep;

    // ---- adding bubbles ----
    class AddRequest
    {
        public double Radius; public bool HasAt; public Vec3 At;
        public List<Vec3> Ats; public List<double> Radii;   // a batch: seeds at exactly these points (generators)
        public string Label;
    }
    readonly List<AddRequest> addQueue = new List<AddRequest>();
    AddRequest currentAdd;
    int addRetries; Random addRng;

    // Queue a new bubble of the given radius. Without a point it's seeded at a random interior
    // four-way node (a point where four bubbles meet). With a point (AddAt):
    //   inside the foam  -> the nearest Plateau border point (a new bubble wedged into that border)
    //   outside the foam -> right there, as a free bubble (it joins the cluster when it touches,
    //                       e.g. pulled in by Gather)
    // It's inserted by a rebuild (background if AsyncRearrange) and then inflates at VolumeRate.
    // Several requests queue up.
    public void RequestAdd(double radius, int seed) { RequestAdd(radius, seed, false, Vec3.Zero); }

    public void RequestAdd(double radius, int seed, bool hasAt, Vec3 at)
    {
        if (addRng == null) addRng = new Random(seed);
        addQueue.Add(new AddRequest { Radius = radius, HasAt = hasAt, At = at });
    }

    public int PendingAdds { get { return addQueue.Count; } }

    // A batch of bubbles, seeded at exactly these points, inserted with ONE rebuild. For generators: e.g.
    // Border Detail puts seeds on Plateau borders. Seeds should be spaced at least ~2 rebuild cells apart.
    // Radii below the rebuild grid are raised to it (as for single adds).
    public void RequestAddBatch(IList<Vec3> ats, IList<double> radii, string label = null)
    {
        if (ats == null || ats.Count == 0) return;
        addQueue.Add(new AddRequest { Ats = new List<Vec3>(ats), Radii = new List<double>(radii), Label = label });
    }

    // Where a requested AddAt point actually seeds (see RequestAdd).
    // A point inside bubble k seeds on k's own nearest border; if k has none (a free bubble), on k's
    // surface nearest the point, so the new bubble attaches to the one pointed into. Before, the nearest
    // border anywhere was used: in Alex's run 2026-10-18_121617 a point inside a free bubble 23 units from
    // the cluster put the new bubble on the cluster.
    Vec3 PlaceAddPoint(Vec3 p, out string where)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        int k = Pop.InsideBubble(Mesh, p);
        if (k == 0)
        {
            double dn;
            NearestFoamPoint(p, out dn);
            where = "at the given point, outside the foam (" + dn.ToString("G3", ci) + " from the nearest bubble)";
            return p;
        }
        int best = -1; double bd = double.MaxValue;
        foreach (long e in BorderEdges)
            foreach (int v in new[] { (int)(e >> 32), (int)(e & 0xffffffff) })
            {
                if (SlotOf(v, k) < 0) continue;
                double d = (Mesh.V[v] - p).LengthSq;
                if (d < bd) { bd = d; best = v; }
            }
        if (best >= 0)
        {
            where = "on bubble " + k + "'s nearest border (" + Math.Sqrt(bd).ToString("G2", ci) + " from the point)";
            return Mesh.V[best];
        }
        foreach (var t in Mesh.T)
        {
            if (!((t.Neg == k && t.Pos == 0) || (t.Pos == k && t.Neg == 0))) continue;
            foreach (int v in new[] { t.A, t.B, t.C })
            {
                double d = (Mesh.V[v] - p).LengthSq;
                if (d < bd) { bd = d; best = v; }
            }
        }
        if (best < 0) { where = "at the given point"; return p; }
        where = "on bubble " + k + "'s surface nearest the point (it has no borders yet)";
        return Mesh.V[best];
    }

    // Nearest mesh vertex to p and its distance (shows where an AddAt point is relative to the foam).
    public Vec3 NearestFoamPoint(Vec3 p, out double dist)
    {
        double bd = double.MaxValue; Vec3 best = p;
        foreach (var q in Mesh.V) { double d = (q - p).LengthSq; if (d < bd) { bd = d; best = q; } }
        dist = Math.Sqrt(bd);
        return best;
    }

    // Interior nodes (4 bubbles, no outside air) first, then any node, then any border
    // vertex, then the centroid of bubble 1. Nodes next to a bubble under 2 rebuild-grid cells in
    // radius are avoided when possible: the seed can erase it ("bubble 24 would vanish" in the
    // adds28 replay, where many bubbles were r 0.2 at a 0.225 grid).
    Vec3 PickAddPoint(out string where)
    {
        var interior = new List<int>(); var any = new List<int>();
        double small = 2 * TargetEdge * S.RebuildGridFactor;
        foreach (int v in Nodes)
        {
            any.Add(v);
            int cnt = slotStart[v + 1] - slotStart[v];
            // slots hold only bubbles; a node touching outside has 3 bubble slots
            if (cnt < 4) continue;
            interior.Add(v);
        }
        Func<List<int>, List<int>> roomy = l => l.Where(v =>
        {
            for (int sl = slotStart[v]; sl < slotStart[v + 1]; sl++) if (DesiredRadius(slotRegion[sl]) < small) return false;
            return true;
        }).ToList();
        var ri = roomy(interior);
        if (ri.Count > 0) interior = ri;
        var ra = roomy(any);
        if (ra.Count > 0) any = ra;
        var pool = interior.Count > 0 ? interior : any;
        if (pool.Count > 0)
        {
            where = interior.Count > 0 ? "interior node" : "surface node";
            return Mesh.V[pool[addRng.Next(pool.Count)]];
        }
        if (BorderEdges.Count > 0)
        {
            where = "border";
            long e = BorderEdges[addRng.Next(BorderEdges.Count)];
            return Mesh.V[(int)(e >> 32)];
        }
        where = "bubble 1 centre";
        return Centroids()[1];
    }

    void StartAdd()
    {
        currentAdd = addQueue[0];
        addQueue.RemoveAt(0);
        if (currentAdd.Ats != null) { StartAddBatch(currentAdd); return; }
        string where;
        Vec3 at = currentAdd.HasAt ? PlaceAddPoint(currentAdd.At, out where) : PickAddPoint(out where);
        // The seed must be at least about one rebuild-grid cell across, or the grid misses it
        // ("the new bubble got no volume" for r 0.1–0.4 at CellSize 0.15, where the grid is 0.225).
        // A bubble requested smaller than the grid can hold is raised to that size.
        double grid = TargetEdge * S.RebuildGridFactor;
        double radius = currentAdd.Radius, minRadius = 1.0 * grid;
        string note = "";
        if (radius < minRadius)
        {
            note = "; radius " + radius.ToString("G3", System.Globalization.CultureInfo.InvariantCulture) + " raised to " +
                   minRadius.ToString("G3", System.Globalization.CultureInfo.InvariantCulture) + " (smallest this CellSize can hold)";
            radius = minRadius;
        }
        double r0 = Math.Max(1.25 * grid, 1.5 * TargetEdge * Math.Max(0.25, Math.Min(1, S.EdgesPerRadius > 0 ? radius / (S.EdgesPerRadius * TargetEdge) : 1)));
        var job = Rearranger.PrepareAdd(this, at, r0, radius);
        job.What += " (" + where + ")" + note;
        Dispatch(job);
    }

    void StartAddBatch(AddRequest req)
    {
        double grid = TargetEdge * S.RebuildGridFactor;
        var r0s = new List<double>(); var radii = new List<double>();
        int raised = 0;
        for (int i = 0; i < req.Ats.Count; i++)
        {
            double radius = req.Radii[i];
            if (radius < grid) { radius = grid; raised++; }
            radii.Add(radius);
            r0s.Add(Math.Max(1.25 * grid, 1.5 * TargetEdge * Math.Max(0.25, Math.Min(1, S.EdgesPerRadius > 0 ? radius / (S.EdgesPerRadius * TargetEdge) : 1))));
        }
        string what = "add " + req.Ats.Count + " bubbles (" + (req.Label ?? "batch") + ")" +
                      (raised > 0 ? "; " + raised + " raised to the smallest this CellSize can hold" : "");
        Dispatch(Rearranger.PrepareAdds(this, req.Ats, r0s, radii, what));
    }

    void Dispatch(Rearranger.RebuildJob job)
    {
        if (S.AsyncRearrange)
        {
            pendingJob = job;
            if (S.SimulatedDelay > 0) { job.Run(); pendingTask = System.Threading.Tasks.Task.FromResult(0); pendingReadyStep = Steps + S.SimulatedDelay; }
            else pendingTask = System.Threading.Tasks.Task.Run(() => job.Run());
        }
        else
        {
            job.Run();
            Finish(job);
        }
    }

    // Churn guards (live 15-bubble run: one short border triggered a rebuild every ~225 steps
    // five times in a row, because each rebuild recreated it):
    //   - debounce: an event must be seen in two consecutive checks
    //   - events that triggered the last rebuild and are seen again right after it didn't help;
    //     ignore those borders for IgnoreSteps
    //   - no rebuilds while any bubble is still re-inflating (target < 95% of desired)
    HashSet<string> seenLastCheck = new HashSet<string>();
    HashSet<string> triggeredLast = new HashSet<string>();
    Dictionary<string, int> ignoreUntil = new Dictionary<string, int>();
    bool firstCheckAfterRebuild;
    Dictionary<string, double> borderLengths = new Dictionary<string, double>(); // per border, at the last check
    const int IgnoreSteps = 1000;

    static string Key(Rearranger.Event e) { return e.Kind + ":" + string.Join("-", e.Regions); }

    bool Reinflating()
    {
        for (int k = 1; k < TargetVolume.Length; k++)
            if (TargetVolume[k] < 0.95 * DesiredVolume[k]) return true;
        return false;
    }

    void CheckRearrange()
    {
        if (pendingJob != null || Steps < S.RearrangeWarmup) return;
        if (Steps - lastRearrangeStep < S.RearrangeCooldown || failedRebuilds >= 3) return;
        // While bubbles re-inflate, border events wait (fits fail on half-inflated bubbles), but new
        // contacts are still checked: an inflating or gathered bubble would otherwise pass straight
        // through its neighbours.
        var all = Reinflating() ? Contacts.Detect(this) : Rearranger.Detect(this, borderLengths);
        var keys = new HashSet<string>(all.Select(Key));
        if (firstCheckAfterRebuild)
        {
            foreach (var k in keys)
                if (triggeredLast.Contains(k) && !ignoreUntil.ContainsKey(k))
                {
                    ignoreUntil[k] = Steps + IgnoreSteps;
                    RearrangeLog.Add("step " + Steps + ": " + k + " came back after a rebuild; ignoring it for " + IgnoreSteps + " steps");
                }
            firstCheckAfterRebuild = false;
        }
        foreach (var k in ignoreUntil.Where(kv => kv.Value <= Steps).Select(kv => kv.Key).ToList()) ignoreUntil.Remove(k);
        // Contacts act at once (two pieces touching isn't a slow trend, and the bubble may be
        // passing straight through). Border events need two consecutive sightings.
        var events = all.Where(e => e.Kind == "contact" ||
                                    (seenLastCheck.Contains(Key(e)) && !ignoreUntil.ContainsKey(Key(e)))).ToList();
        seenLastCheck = keys;
        if (events.Count == 0) return;
        triggeredLast = new HashSet<string>(events.Where(e => e.Kind != "contact").Select(Key));
        firstCheckAfterRebuild = true;
        // Contacts first in the message.
        events = events.OrderBy(e => e.Kind == "contact" ? 0 : 1).ToList();
        string what = string.Join("; ", events.Take(4).Select(e => e.ToString())) + (events.Count > 4 ? " (+" + (events.Count - 4) + ")" : "");
        var job = Rearranger.Prepare(this, what, events);
        foreach (var e in events) if (e.Grow != null) job.Grow.AddRange(e.Grow);
        foreach (var e in events)
            if (e.Shift.LengthSq > 0 && e.Regions != null && e.Regions.Length > 0) job.Shift.Add(new KeyValuePair<int, Vec3>(e.Regions[0], e.Shift));
        if (S.AsyncRearrange)
        {
            pendingJob = job;
            if (S.SimulatedDelay > 0) { job.Run(); pendingTask = System.Threading.Tasks.Task.FromResult(0); pendingReadyStep = Steps + S.SimulatedDelay; }
            else pendingTask = System.Threading.Tasks.Task.Run(() => job.Run());
        }
        else
        {
            job.Run();
            Finish(job);
        }
    }

    // Apply a finished background rebuild (called at the start of each step).
    void PollRebuild()
    {
        if (pendingJob == null || !pendingTask.IsCompleted || Steps < pendingReadyStep) return;
        var job = pendingJob;
        pendingJob = null; pendingTask = null;
        Finish(job);
    }

    void Finish(Rearranger.RebuildJob job)
    {
        lastRearrangeStep = Steps;
        string when = S.AsyncRearrange ? " (background, steps " + job.Step + "-" + Steps + ")" : "";
        if (job.Fail != null)
        {
            LastFailedJob = job;
            string retry = "";
            if (job.AddCount > 0)
            {
                // A refused add tries another node (twice) instead of being dropped.
                if (addRetries < 2 && currentAdd != null)
                {
                    addRetries++;
                    addQueue.Insert(0, currentAdd);   // same request; a point request is re-jittered by PrepareAdd
                    retry = currentAdd.HasAt || currentAdd.Ats != null ? "; trying again" : "; trying another node";
                }
                else { addRetries = 0; retry = "; add dropped after 3 tries"; }
            }
            else failedRebuilds++;
            RearrangeLog.Add("step " + job.Step + ": " + job.What + " -> rebuild refused: " + job.Fail + when + retry);
            return;
        }
        if (job.AddCount > 0) addRetries = 0;
        int bordersBefore = BorderEdges.Count, nodesBefore = Nodes.Count;
        int firstNew = job.CurrentVolume.Length;   // regions from here on are this job's new bubbles
        if (job.AddCount > 0)
        {
            // Grow the per-region arrays for the new bubbles; each target starts at its seeded
            // volume and ramps to the requested size.
            var seededVol = job.Mesh.Volumes();
            int kMax = job.Mesh.BubbleCount;
            for (int i = 0; i < job.AddCount; i++)
            {
                int k = firstNew + i;
                double rr = job.AddRadii[i];
                TargetVolume = Grow(TargetVolume, k, seededVol[k]);
                DesiredVolume = Grow(DesiredVolume, k, 4.0 / 3.0 * Math.PI * rr * rr * rr);
                Pressure = Grow(Pressure, k, 0);
                if (job.Scale == null) RegionScale = Grow(RegionScale, k, 1);
            }
            if (job.Scale != null) RegionScale = job.Scale;
            var pt = new Vec3[kMax + 1]; Array.Copy(PullTarget, pt, PullTarget.Length); PullTarget = pt;
            var po = new bool[kMax + 1]; Array.Copy(PullOn, po, PullOn.Length); PullOn = po;
            Added += job.AddCount;
        }
        // A bubble the rebuild left a little off keeps its target: the volume correction puts the gap
        // back within a step or two, without a visible shrink-and-regrow. "A little" = its radius off by
        // under half its edge length (the Newton step then moves films less than half an edge).
        // A bigger gap starts from the rebuilt volume and the ramp brings it back at VolumeRate,
        // instead of one large Newton correction tearing the mesh.
        var rebuilt = job.Mesh.Volumes();
        var reinflate = new List<string>();
        double kept = 1;
        for (int k = 1; k < TargetVolume.Length && k < rebuilt.Length; k++)
        {
            // Fidelity of the rebuild itself: against the volumes when it was prepared (live resizes
            // in the meantime are the ramp's job, not a loss).
            if (k < job.CurrentVolume.Length && job.CurrentVolume[k] > 0) kept = Math.Min(kept, rebuilt[k] / job.CurrentVolume[k]);
            if (!(TargetVolume[k] > 0) || k >= firstNew) { TargetVolume[k] = rebuilt[k]; continue; }
            double dr = Math.Abs(Math.Pow(3 * Math.Max(rebuilt[k], 0) / (4 * Math.PI), 1.0 / 3) - Math.Pow(3 * TargetVolume[k] / (4 * Math.PI), 1.0 / 3));
            double edge = TargetEdge * (RegionScale != null && k < RegionScale.Length ? RegionScale[k] : 1);
            if (dr < 0.5 * edge) continue;
            reinflate.Add(k + " at " + (rebuilt[k] / TargetVolume[k]).ToString("P0", System.Globalization.CultureInfo.InvariantCulture));
            TargetVolume[k] = rebuilt[k];
        }
        ReplaceMesh(job.Mesh);
        if (job.AddCount > 1) MarkDead();   // a batch seed that took no space is left out, not re-inflated forever
        failedRebuilds = 0;
        Rearrangements++;
        RearrangeLog.Add("step " + job.Step + ": " + job.What + " -> rebuilt in " + job.Ms + " ms" + when + "; nodes " +
                         nodesBefore + "->" + Nodes.Count + ", border edges " + bordersBefore + "->" + BorderEdges.Count +
                         "; smallest volume kept vs snapshot " + kept.ToString("P0", System.Globalization.CultureInfo.InvariantCulture) +
                         (reinflate.Count > 0 ? "; re-inflating bubble " + string.Join(", ", reinflate) : "") +
                         (job.Transfer != null ? "; " + job.Transfer : "") +
                         (job.Calibration != null && job.Calibration.Contains("rescued") ? "; " + job.Calibration : "") + "; " + job.Timing);
    }

    public int Added;
    public Rearranger.RebuildJob LastFailedJob;   // the most recent refused rebuild (diagnostics: harness failrepro)

    static double[] Grow(double[] a, int k, double value)
    {
        var b = new double[k + 1];
        Array.Copy(a, b, Math.Min(a.Length, k + 1));
        b[k] = value;
        return b;
    }

    public void Step()
    {
        PollRebuild();
        if (pendingJob == null && pendingPops.Count > 0) ApplyPops();
        if (pendingJob == null && pendingInserts.Count > 0) ApplyInserts();
        // Adds wait until every bubble is back to size: a bubble still inflating from a previous
        // add or rebuild is small, and the next rebuild could lose it ("bubble 18 would vanish").
        if (pendingJob == null && addQueue.Count > 0 && !Reinflating()) StartAdd();
        if (Steps > 0)
        {
            if (S.Rearrange && S.RearrangeEvery > 0 && Steps % S.RearrangeEvery == 0) CheckRearrange();
            if (S.RemeshEvery > 0 && Steps % S.RemeshEvery == 0 && NeedsRemesh()) Remesh();
            else if (S.MaintainEvery > 0 && Steps % S.MaintainEvery == 0) Maintain();
        }
        int nb = Mesh.BubbleCount;
        Load();
        Array.Copy(X, X0, X.Length);
        RampVolumes();
        Pass(true);
        double ten = S.Tension;
        if (ten != 1.0) for (int i = 0; i < GA.Length; i++) GA[i] *= ten;
        ApplyPull();

        // Pressures: choose lambda so the motion keeps every volume fixed (to first order).
        var G = new double[nb, nb];
        var rhs = new double[nb];
        for (int v = 0; v < nv; v++)
        {
            double mob = Mob(v);
            double gx = GA[3 * v], gy = GA[3 * v + 1], gz = GA[3 * v + 2];
            for (int s = slotStart[v]; s < slotStart[v + 1]; s++)
            {
                int k = slotRegion[s] - 1;
                double sx = GV[3 * s], sy = GV[3 * s + 1], sz = GV[3 * s + 2];
                rhs[k] += mob * (sx * gx + sy * gy + sz * gz);
                for (int s2 = slotStart[v]; s2 < slotStart[v + 1]; s2++)
                    G[k, slotRegion[s2] - 1] += mob * (sx * GV[3 * s2] + sy * GV[3 * s2 + 1] + sz * GV[3 * s2 + 2]);
            }
        }
        double[] lambda = LinAlg.Solve(G, rhs);
        for (int k = 0; k < nb; k++) Pressure[k + 1] = lambda[k];

        double dt = S.Dt * EdgeLength * EdgeLength;
        double capBase = S.MaxMove * EdgeLength;
        double mom = S.Momentum;
        double res = 0, aSum = 0;
        for (int v = 0; v < nv; v++)
        {
            double fx = -GA[3 * v], fy = -GA[3 * v + 1], fz = -GA[3 * v + 2];
            for (int s = slotStart[v]; s < slotStart[v + 1]; s++)
            {
                double l = lambda[slotRegion[s] - 1];
                fx += GV[3 * s] * l; fy += GV[3 * s + 1] * l; fz += GV[3 * s + 2] * l;
            }
            double mob = Mob(v);
            fx *= mob; fy *= mob; fz *= mob;
            res += (fx * fx + fy * fy + fz * fz) * VA[v]; aSum += VA[v];
            double dx = fx * dt + prev[3 * v] * mom, dy = fy * dt + prev[3 * v + 1] * mom, dz = fz * dt + prev[3 * v + 2] * mom;
            double d2 = dx * dx + dy * dy + dz * dz, cap = capBase * vScale[v];
            if (d2 > cap * cap) { double sc = cap / Math.Sqrt(d2); dx *= sc; dy *= sc; dz *= sc; }
            X[3 * v] += dx; X[3 * v + 1] += dy; X[3 * v + 2] += dz;
        }
        Residual = Math.Sqrt(res / Math.Max(aSum, 1e-300));

        if (S.Smooth > 0) SmoothTangential(S.Smooth);
        for (int it = 0; it < S.VolumeIters; it++) CorrectVolumesInternal();
        SnapWet();

        // Momentum remembers the whole move (physics + smoothing + volume correction). Carrying
        // only the physics move was tried 2026-10-14 and made the random-12 benchmark worse.
        for (int i = 0; i < X.Length; i++) prev[i] = X[i] - X0[i];

        Area = TotalAreaAndVolumes();
        MaxVolumeError = 0;
        for (int k = 1; k <= nb; k++)
            if (TargetVolume[k] > 0)
                MaxVolumeError = Math.Max(MaxVolumeError, Math.Abs(vol[k] - TargetVolume[k]) / TargetVolume[k]);
        Store();
        Steps++;
    }

    void RampVolumes()
    {
        for (int k = 1; k < TargetVolume.Length; k++)
        {
            double d = DesiredVolume[k] - TargetVolume[k];
            if (d == 0) continue;
            double max = S.VolumeRate * Math.Max(TargetVolume[k], 0.1 * DesiredVolume[k]);
            TargetVolume[k] += Math.Max(-max, Math.Min(max, d));
        }
    }

    // External pull: each vertex of a pulled bubble's films gets VA * Pull * (target - centroid) / R^2,
    // subtracted from the area gradient. That way the volume constraints (pressures) see it, and the
    // pull moves the bubble without changing any volume. Total pull on a bubble is ~4 pi Pull d
    // for offset d, independent of its size.
    // Gather works the same way for every live bubble, toward S.GatherTo, with the offset capped at
    // the bubble's radius: far bubbles drift in at a steady rate instead of being yanked, and a
    // cluster is only pressed together gently. A bubble also being dragged gets both.
    void ApplyPull()
    {
        int nb = Mesh.BubbleCount;
        bool any = S.Gather > 0;
        if (S.Pull > 0) for (int k = 1; k <= nb; k++) if (PullOn[k]) { any = true; break; }
        if (ExtForce != null) for (int k = 1; k < ExtForce.Length && !any; k++) if (ExtForce[k].LengthSq > 0) any = true;
        if (!any) return;
        var forced = new bool[nb + 1];
        var cen = new double[3 * (nb + 1)];
        var vk = new double[nb + 1];
        double[] x = X;
        for (int t = 0; t < nt; t++)
        {
            int ia = 3 * tv[3 * t], ib = 3 * tv[3 * t + 1], ic = 3 * tv[3 * t + 2];
            double ax = x[ia], ay = x[ia + 1], az = x[ia + 2], bx = x[ib], by = x[ib + 1], bz = x[ib + 2], cx = x[ic], cy = x[ic + 1], cz = x[ic + 2];
            double tvol = (ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx)) / 6;
            double gx = (ax + bx + cx) / 4, gy = (ay + by + cy) / 4, gz = (az + bz + cz) / 4;
            int ng = tNeg[t], ps = tPos[t];
            if (ng > 0) { vk[ng] += tvol; cen[3 * ng] += gx * tvol; cen[3 * ng + 1] += gy * tvol; cen[3 * ng + 2] += gz * tvol; }
            if (ps > 0) { vk[ps] -= tvol; cen[3 * ps] -= gx * tvol; cen[3 * ps + 1] -= gy * tvol; cen[3 * ps + 2] -= gz * tvol; }
        }
        var fk = new double[3 * (nb + 1)];
        for (int k = 1; k <= nb; k++)
        {
            if (!(vk[k] > 0)) continue;
            double r2 = Math.Pow(3 * vk[k] / (4 * Math.PI), 2.0 / 3);
            double cx = cen[3 * k] / vk[k], cy = cen[3 * k + 1] / vk[k], cz = cen[3 * k + 2] / vk[k];
            if (S.Pull > 0 && PullOn[k])
            {
                double s = S.Pull / r2;
                fk[3 * k] = s * (PullTarget[k].X - cx);
                fk[3 * k + 1] = s * (PullTarget[k].Y - cy);
                fk[3 * k + 2] = s * (PullTarget[k].Z - cz);
                forced[k] = true;
            }
            if (ExtForce != null && k < ExtForce.Length && ExtForce[k].LengthSq > 0)
            {
                fk[3 * k] += ExtForce[k].X; fk[3 * k + 1] += ExtForce[k].Y; fk[3 * k + 2] += ExtForce[k].Z;
                forced[k] = true;
            }
            if (S.Gather > 0)
            {
                double gx = S.GatherTo.X - cx, gy = S.GatherTo.Y - cy, gz = S.GatherTo.Z - cz;
                double d = Math.Sqrt(gx * gx + gy * gy + gz * gz), r = Math.Sqrt(r2);
                if (d > 1e-12)
                {
                    double s = S.Gather / r2 * Math.Min(d, r) / d;
                    fk[3 * k] += s * gx; fk[3 * k + 1] += s * gy; fk[3 * k + 2] += s * gz;
                    forced[k] = true;
                }
            }
        }
        for (int v = 0; v < nv; v++)
            for (int sl = slotStart[v]; sl < slotStart[v + 1]; sl++)
            {
                int k = slotRegion[sl];
                if (!forced[k]) continue;
                GA[3 * v] -= VA[v] * fk[3 * k]; GA[3 * v + 1] -= VA[v] * fk[3 * k + 1]; GA[3 * v + 2] -= VA[v] * fk[3 * k + 2];
            }
    }

    // Newton step back onto the volume constraints along the volume gradients. Uses the
    // vertex areas from the last full pass for the mobility.
    void CorrectVolumesInternal()
    {
        int nb = Mesh.BubbleCount;
        Pass(false);
        var G = new double[nb, nb];
        var rhs = new double[nb];
        for (int k = 1; k <= nb; k++) rhs[k - 1] = TargetVolume[k] - vol[k];
        for (int v = 0; v < nv; v++)
        {
            double mob = Mob(v);
            for (int s = slotStart[v]; s < slotStart[v + 1]; s++)
            {
                double sx = GV[3 * s], sy = GV[3 * s + 1], sz = GV[3 * s + 2];
                for (int s2 = slotStart[v]; s2 < slotStart[v + 1]; s2++)
                    G[slotRegion[s] - 1, slotRegion[s2] - 1] += mob * (sx * GV[3 * s2] + sy * GV[3 * s2 + 1] + sz * GV[3 * s2 + 2]);
            }
        }
        double[] mu = LinAlg.Solve(G, rhs);
        for (int v = 0; v < nv; v++)
        {
            double mob = Mob(v), dx = 0, dy = 0, dz = 0;
            for (int s = slotStart[v]; s < slotStart[v + 1]; s++)
            {
                double u = mu[slotRegion[s] - 1];
                dx += GV[3 * s] * u; dy += GV[3 * s + 1] * u; dz += GV[3 * s + 2] * u;
            }
            X[3 * v] += dx * mob; X[3 * v + 1] += dy * mob; X[3 * v + 2] += dz * mob;
        }
    }

    // Public entry (harness / tools): correct volumes on the current mesh positions.
    public void CorrectVolumes()
    {
        Load();
        Pass(true);
        CorrectVolumesInternal();
        Store();
    }

    double TotalAreaAndVolumes()
    {
        Array.Clear(vol, 0, vol.Length);
        double area = 0;
        double[] x = X;
        for (int t = 0; t < nt; t++)
        {
            int ia = 3 * tv[3 * t], ib = 3 * tv[3 * t + 1], ic = 3 * tv[3 * t + 2];
            double ax = x[ia], ay = x[ia + 1], az = x[ia + 2];
            double bx = x[ib], by = x[ib + 1], bz = x[ib + 2];
            double cx = x[ic], cy = x[ic + 1], cz = x[ic + 2];
            double ux = bx - ax, uy = by - ay, uz = bz - az, wx = cx - ax, wy = cy - ay, wz = cz - az;
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            if (!wetT[t]) area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
            double tvol = (ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx)) / 6;
            if (tNeg[t] > 0) vol[tNeg[t]] += tvol;
            if (tPos[t] > 0) vol[tPos[t]] -= tvol;
        }
        return area;
    }

    // Move vertices within the surface (films) or along the border (lines) toward their
    // neighbours' average. Keeps triangles well shaped without changing the geometry.
    void SmoothTangential(double w)
    {
        double[] x = X;
        Array.Clear(nrm, 0, nrm.Length);
        for (int t = 0; t < nt; t++)
        {
            int ia = 3 * tv[3 * t], ib = 3 * tv[3 * t + 1], ic = 3 * tv[3 * t + 2];
            double ux = x[ib] - x[ia], uy = x[ib + 1] - x[ia + 1], uz = x[ib + 2] - x[ia + 2];
            double wx = x[ic] - x[ia], wy = x[ic + 1] - x[ia + 1], wz = x[ic + 2] - x[ia + 2];
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            nrm[ia] += nx; nrm[ia + 1] += ny; nrm[ia + 2] += nz;
            nrm[ib] += nx; nrm[ib + 1] += ny; nrm[ib + 2] += nz;
            nrm[ic] += nx; nrm[ic + 1] += ny; nrm[ic + 2] += nz;
        }
        Array.Copy(x, nxt, x.Length);
        for (int v = 0; v < nv; v++)
        {
            int s0 = nbStart[v], s1 = nbStart[v + 1], cnt = s1 - s0;
            if (cls[v] == 2 || cnt == 0) continue;
            double ax = 0, ay = 0, az = 0;
            for (int s = s0; s < s1; s++) { int u = 3 * nbList[s]; ax += x[u]; ay += x[u + 1]; az += x[u + 2]; }
            int i = 3 * v;
            double lx = ax / cnt - x[i], ly = ay / cnt - x[i + 1], lz = az / cnt - x[i + 2];
            double dx, dy, dz;
            if (cls[v] == 0) { dx = nrm[i]; dy = nrm[i + 1]; dz = nrm[i + 2]; }
            else
            {
                int p = 3 * nbList[s0], q = 3 * nbList[s0 + 1];
                dx = x[q] - x[p]; dy = x[q + 1] - x[p + 1]; dz = x[q + 2] - x[p + 2];
            }
            double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dl > 1e-300) { dx /= dl; dy /= dl; dz /= dl; }
            double dot = lx * dx + ly * dy + lz * dz;
            if (cls[v] == 0) { lx -= dx * dot; ly -= dy * dot; lz -= dz * dot; }   // remove normal part
            else { lx = dx * dot; ly = dy * dot; lz = dz * dot; }                  // keep tangent part
            nxt[i] = x[i] + lx * w; nxt[i + 1] = x[i + 1] + ly * w; nxt[i + 2] = x[i + 2] + lz * w;
        }
        Array.Copy(nxt, X, X.Length);
    }
}

public static class LinAlg
{
    // Dense Gaussian elimination with partial pivoting. Singular directions get 0.
    public static double[] Solve(double[,] A0, double[] b0)
    {
        int n = b0.Length;
        var A = (double[,])A0.Clone();
        var b = (double[])b0.Clone();
        var x = new double[n];
        double scale = 0;
        for (int i = 0; i < n; i++) scale = Math.Max(scale, Math.Abs(A[i, i]));
        double eps = 1e-14 * Math.Max(scale, 1e-300);
        var dead = new bool[n];
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[p, c])) p = r;
            if (Math.Abs(A[p, c]) < eps) { dead[c] = true; continue; }
            if (p != c)
            {
                for (int k = 0; k < n; k++) { double t = A[c, k]; A[c, k] = A[p, k]; A[p, k] = t; }
                double tb = b[c]; b[c] = b[p]; b[p] = tb;
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = A[r, c] / A[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) A[r, k] -= f * A[c, k];
                b[r] -= f * b[c];
            }
        }
        for (int c = n - 1; c >= 0; c--)
        {
            if (dead[c]) { x[c] = 0; continue; }
            double s = b[c];
            for (int k = c + 1; k < n; k++) s -= A[c, k] * x[k];
            x[c] = s / A[c, c];
        }
        return x;
    }
}

// ---- core/FoamAnalysis.cs ----
// Read-outs from a film mesh: Plateau borders, nodes, junction angles, sphere fits.
public static class FoamAnalysis
{
    public class Chain
    {
        public List<int> Verts = new List<int>();
        public bool Closed;
    }

    // Border edges joined into chains that run node to node (or close on themselves).
    public static List<Chain> BorderChains(FoamMesh m, out List<int> nodes)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (var kv in m.EdgeTris())
        {
            if (!m.IsBorder(kv.Value)) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            if (!adj.ContainsKey(a)) adj[a] = new List<int>();
            if (!adj.ContainsKey(b)) adj[b] = new List<int>();
            adj[a].Add(b); adj[b].Add(a);
        }
        nodes = adj.Where(kv => kv.Value.Count != 2).Select(kv => kv.Key).ToList();
        var used = new HashSet<long>(LongKey.Comparer);
        var chains = new List<Chain>();
        foreach (int s in nodes)
            foreach (int nb in adj[s])
            {
                if (used.Contains(FoamMesh.EdgeKey(s, nb))) continue;
                var c = new Chain();
                c.Verts.Add(s);
                int prev = s, cur = nb;
                used.Add(FoamMesh.EdgeKey(s, nb));
                while (true)
                {
                    c.Verts.Add(cur);
                    if (adj[cur].Count != 2) break;
                    int nx = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
                    if (used.Contains(FoamMesh.EdgeKey(cur, nx))) break;
                    used.Add(FoamMesh.EdgeKey(cur, nx));
                    prev = cur; cur = nx;
                }
                chains.Add(c);
            }
        // Closed loops with no nodes (e.g. the ring of a double bubble).
        foreach (var kv in adj)
            foreach (int nb in kv.Value)
            {
                if (used.Contains(FoamMesh.EdgeKey(kv.Key, nb))) continue;
                var c = new Chain { Closed = true };
                int start = kv.Key, prev = start, cur = nb;
                c.Verts.Add(start);
                used.Add(FoamMesh.EdgeKey(start, nb));
                while (cur != start)
                {
                    c.Verts.Add(cur);
                    int nx = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
                    used.Add(FoamMesh.EdgeKey(cur, nx));
                    prev = cur; cur = nx;
                }
                chains.Add(c);
            }
        return chains;
    }

    public class AngleStats
    {
        public int Count;
        public double Mean, MeanAbsDev, MaxAbsDev;
        public override string ToString()
        {
            return Count == 0 ? "n/a" :
                string.Format(CultureInfo.InvariantCulture, "mean {0:F2}°, |dev| mean {1:F2}° max {2:F2}° (n={3})",
                              Mean, MeanAbsDev, MaxAbsDev, Count);
        }
    }

    static AngleStats Stats(List<double> angles, double target)
    {
        var s = new AngleStats { Count = angles.Count };
        if (angles.Count == 0) return s;
        s.Mean = angles.Average();
        s.MeanAbsDev = angles.Average(a => Math.Abs(a - target));
        s.MaxAbsDev = angles.Max(a => Math.Abs(a - target));
        return s;
    }

    // Angles between the three films at each border edge (target 120°). Edges touching a
    // node are skipped; the film direction is taken toward each triangle's far vertex.
    public static AngleStats BorderAngles(FoamMesh m)
    {
        List<int> nodes;
        BorderChains(m, out nodes);
        var nodeSet = new HashSet<int>(nodes);
        var angles = new List<double>();
        foreach (var kv in m.EdgeTris())
        {
            if (kv.Value.Count != 3) continue;
            if (kv.Value.Any(ti => FoamMesh.IsSolid(m.T[ti]))) continue;   // a wall standing on a host: see ContactAngles
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            if (nodeSet.Contains(a) || nodeSet.Contains(b)) continue;
            Vec3 e = (m.V[b] - m.V[a]).Normalized();
            Vec3 mid = (m.V[a] + m.V[b]) * 0.5;
            var dirs = new List<Vec3>();
            foreach (int ti in kv.Value)
            {
                var t = m.T[ti];
                int o = t.A != a && t.A != b ? t.A : (t.B != a && t.B != b ? t.B : t.C);
                Vec3 d = m.V[o] - mid;
                dirs.Add((d - e * Vec3.Dot(d, e)).Normalized());
            }
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    angles.Add(Deg(dirs[i], dirs[j]));
        }
        return Stats(angles, 120.0);
    }

    // Angles between the four borders at each valence-4 node (target 109.47°). Each border
    // direction is measured to the vertex `reach` steps along its chain.
    public static AngleStats NodeAngles(FoamMesh m, int reach)
    {
        List<int> nodes;
        var chains = BorderChains(m, out nodes);
        var dirsAt = new Dictionary<int, List<Vec3>>();
        foreach (var c in chains)
        {
            if (c.Closed || c.Verts.Count < 2) continue;
            int s = c.Verts[0], e = c.Verts[c.Verts.Count - 1];
            int k = Math.Min(reach, c.Verts.Count - 1);
            Add(dirsAt, s, (m.V[c.Verts[k]] - m.V[s]).Normalized());
            Add(dirsAt, e, (m.V[c.Verts[c.Verts.Count - 1 - k]] - m.V[e]).Normalized());
        }
        var angles = new List<double>();
        var onHost = new HashSet<int>();
        foreach (var t in m.T) if (FoamMesh.IsSolid(t)) { onHost.Add(t.A); onHost.Add(t.B); onHost.Add(t.C); }
        foreach (var kv in dirsAt)
        {
            if (kv.Value.Count != 4 || onHost.Contains(kv.Key)) continue;   // a border landing on a host isn't a tetrahedral node
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    angles.Add(Deg(kv.Value[i], kv.Value[j]));
        }
        return Stats(angles, 109.4712);
    }

    // Angle between each film and the host where it meets it (target 90 degrees): at every footprint
    // edge, the angle between the footprint triangle and each film triangle across that edge.
    public static AngleStats ContactAngles(FoamMesh m)
    {
        var angles = new List<double>();
        foreach (var kv in m.EdgeTris())
        {
            var tris = kv.Value;
            int foot = -1;
            foreach (int ti in tris) if (FoamMesh.IsSolid(m.T[ti])) { foot = ti; break; }
            if (foot < 0 || !m.IsBorder(tris)) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            Vec3 e = (m.V[b] - m.V[a]).Normalized(), mid = (m.V[a] + m.V[b]) * 0.5;
            Func<int, Vec3> dir = ti =>
            {
                var t = m.T[ti];
                int o = t.A != a && t.A != b ? t.A : (t.B != a && t.B != b ? t.B : t.C);
                Vec3 d = m.V[o] - mid;
                return (d - e * Vec3.Dot(d, e)).Normalized();
            };
            Vec3 df = dir(foot);
            foreach (int ti in tris)
            {
                if (FoamMesh.IsSolid(m.T[ti])) continue;
                angles.Add(Deg(df, dir(ti)));
            }
        }
        return Stats(angles, 90.0);
    }

    static void Add(Dictionary<int, List<Vec3>> d, int k, Vec3 v)
    {
        List<Vec3> l;
        if (!d.TryGetValue(k, out l)) { l = new List<Vec3>(); d[k] = l; }
        l.Add(v);
    }

    static double Deg(Vec3 a, Vec3 b)
    {
        double c = Math.Max(-1, Math.Min(1, Vec3.Dot(a, b)));
        return Math.Acos(c) * 180.0 / Math.PI;
    }

    // Algebraic least-squares sphere: |x|^2 + D.x + E = 0.
    // Closest point to p on triangle abc (Ericson, Real-Time Collision Detection 5.1.5).
    public static Vec3 ClosestOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 ab = b - a, ac = c - a, ap = p - a;
        double d1 = Vec3.Dot(ab, ap), d2 = Vec3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vec3 bp = p - b;
        double d3 = Vec3.Dot(ab, bp), d4 = Vec3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vec3 cp = p - c;
        double d5 = Vec3.Dot(ab, cp), d6 = Vec3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        double den = 1 / (va + vb + vc);
        return a + ab * (vb * den) + ac * (vc * den);
    }

    // Closest point to p on segment ab.
    public static Vec3 ClosestOnSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        Vec3 ab = b - a;
        double l2 = ab.LengthSq;
        if (l2 <= 0) return a;
        double t = Math.Max(0, Math.Min(1, Vec3.Dot(p - a, ab) / l2));
        return a + ab * t;
    }

    public static bool FitSphere(IList<Vec3> pts, out Vec3 center, out double radius)
    {
        var A = new double[4, 4];
        var b = new double[4];
        foreach (var p in pts)
        {
            double[] row = { p.X, p.Y, p.Z, 1 };
            double rhs = -p.LengthSq;
            for (int i = 0; i < 4; i++)
            {
                b[i] += row[i] * rhs;
                for (int j = 0; j < 4; j++) A[i, j] += row[i] * row[j];
            }
        }
        var x = LinAlg.Solve(A, b);
        center = new Vec3(-x[0] / 2, -x[1] / 2, -x[2] / 2);
        double r2 = center.LengthSq - x[3];
        radius = r2 > 0 ? Math.Sqrt(r2) : double.NaN;
        return r2 > 0;
    }

    // Vertices of the films between regions a and b.
    public static List<Vec3> PairVertices(FoamMesh m, int a, int b)
    {
        var set = new HashSet<int>();
        foreach (var t in m.T)
            if ((t.Neg == a && t.Pos == b) || (t.Neg == b && t.Pos == a)) { set.Add(t.A); set.Add(t.B); set.Add(t.C); }
        return set.Select(i => m.V[i]).ToList();
    }

    public static List<KeyValuePair<int, int>> Pairs(FoamMesh m)
    {
        var s = new HashSet<long>(LongKey.Comparer);
        foreach (var t in m.T) s.Add(FoamMesh.EdgeKey(t.Neg, t.Pos));
        return s.Select(k => new KeyValuePair<int, int>((int)(k >> 32), (int)(k & 0xffffffff)))
                .OrderBy(p => p.Key).ThenBy(p => p.Value).ToList();
    }

    // OBJ with one group per film ("g r<neg>_<pos>") so region labels survive a round trip.
    public static string ToObj(FoamMesh m)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.Append("# bubbles ").Append(m.BubbleCount).Append('\n');
        foreach (var v in m.V)
            sb.Append("v ").Append(v.X.ToString("R", ci)).Append(' ').Append(v.Y.ToString("R", ci))
              .Append(' ').Append(v.Z.ToString("R", ci)).Append('\n');
        foreach (var grp in m.T.GroupBy(t => FoamMesh.EdgeKey(t.Neg, t.Pos)))
        {
            var first = grp.First();
            int a = Math.Min(first.Neg, first.Pos), b = Math.Max(first.Neg, first.Pos);
            sb.Append("g r").Append(a).Append('_').Append(b).Append('\n');
            foreach (var t in grp)
            {
                // Store each triangle oriented from the lower region to the higher one.
                if (t.Neg == a) sb.Append("f ").Append(t.A + 1).Append(' ').Append(t.B + 1).Append(' ').Append(t.C + 1).Append('\n');
                else sb.Append("f ").Append(t.A + 1).Append(' ').Append(t.C + 1).Append(' ').Append(t.B + 1).Append('\n');
            }
        }
        return sb.ToString();
    }

    public static FoamMesh FromObj(string text)
    {
        var m = new FoamMesh();
        var ci = CultureInfo.InvariantCulture;
        int a = 0, b = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0] == "#" && p.Length >= 3 && p[1] == "bubbles") m.BubbleCount = int.Parse(p[2], ci);
            else if (p[0] == "v") m.V.Add(new Vec3(double.Parse(p[1], ci), double.Parse(p[2], ci), double.Parse(p[3], ci)));
            else if (p[0] == "g" && p[1].StartsWith("r"))
            {
                var ab = p[1].Substring(1).Split('_');
                a = int.Parse(ab[0], ci); b = int.Parse(ab[1], ci);
                m.BubbleCount = Math.Max(m.BubbleCount, b);
            }
            else if (p[0] == "f")
                m.T.Add(new FoamTri(int.Parse(p[1].Split('/')[0], ci) - 1, int.Parse(p[2].Split('/')[0], ci) - 1,
                                    int.Parse(p[3].Split('/')[0], ci) - 1, a, b));
        }
        return m;
    }

    // One-paragraph health report, used by both the GH Info output and the harness.
    public static string Report(FoamSolver s)
    {
        var m = s.Mesh;
        List<int> nodes;
        var chains = BorderChains(m, out nodes);
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendFormat(ci, "step {0}: {1} verts, {2} tris, {3} bubbles, {4} borders, {5} nodes\n",
                        s.Steps, m.V.Count, m.T.Count, m.BubbleCount, chains.Count, nodes.Count);
        sb.AppendFormat(ci, "area {0:G6}, residual {1:G3}, max volume error {2:P3}\n", s.Area, s.Residual, s.MaxVolumeError);
        sb.Append("border angles (120°): ").Append(BorderAngles(m)).Append('\n');
        sb.Append("node angles (109.47°): ").Append(NodeAngles(m, 2)).Append('\n');
        if (m.T.Any(FoamMesh.IsSolid)) sb.Append("contact angles on the host (90°): ").Append(ContactAngles(m)).Append('\n');
        sb.Append("pressures:");
        for (int k = 1; k <= m.BubbleCount; k++) sb.AppendFormat(ci, " {0}:{1:G4}", k, s.Pressure[k]);
        return sb.ToString();
    }
}

// ---- core/ClusterPacker.cs ----
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

// ---- core/BubbleFunctions.cs ----
// Per-bubble implicit functions f_k(x) = A_k |x|^2 + B_k . x + D_k, negative inside bubble k.
// A sphere (c, r) is A = 1/(2r), B = -c/r, D = (|c|^2 - r^2)/(2r): the Möbius diagram.
// Any (A, B, D) keeps walls (f_i = f_j) and outer films (f_k = 0) spherical, so fitted
// functions can describe a relaxed cluster for remeshing.
public class BubbleFunctions
{
    public int Count;
    public double[] A, D;
    public Vec3[] B;
    public HostSurface Host;   // optional solid (region FoamMesh.Solid); fixed, never fitted

    // Does the host solid win at x (against every bubble and the outside)?
    public bool SolidWins(Vec3 x)
    {
        if (Host == null) return false;
        double gs = Host.Phase(x);
        if (gs <= 0) return false;
        for (int k = 1; k <= Count; k++) if (-F(k, x) >= gs) return false;
        return true;
    }

    public BubbleFunctions(int n)
    {
        Count = n; A = new double[n]; D = new double[n]; B = new Vec3[n];
    }

    public static BubbleFunctions FromSpheres(IList<Vec3> c, IList<double> r, HostSurface host = null)
    {
        var f = new BubbleFunctions(c.Count);
        for (int i = 0; i < c.Count; i++)
        {
            f.A[i] = 1 / (2 * r[i]);
            f.B[i] = c[i] * (-1 / r[i]);
            f.D[i] = (c[i].LengthSq - r[i] * r[i]) / (2 * r[i]);
        }
        f.Host = host;
        return f;
    }

    public BubbleFunctions Clone()
    {
        var c = new BubbleFunctions(Count) { Host = Host };
        Array.Copy(A, c.A, Count); Array.Copy(B, c.B, Count); Array.Copy(D, c.D, Count);
        return c;
    }

    // f_k(x) for bubble k (1-based).
    public double F(int k, Vec3 x)
    {
        int i = k - 1;
        return A[i] * x.LengthSq + B[i].X * x.X + B[i].Y * x.Y + B[i].Z * x.Z + D[i];
    }

    // Mesher phase: outside 0, bubble k = -f_k (largest wins).
    public double Phase(int k, Vec3 x) { return k == 0 ? 0 : -F(k, x); }
}

// ---- core/Contacts.cs ----
// New contacts between separate pieces of foam (Phase 3, "contact" events).
//
// Bubbles that share no wall don't interact at all in the film solver: a bubble dragged out
// of the cluster (separated by a rebuild) and dragged back passed straight through the others
// (Alex's run 2026-10-15_213610). Here bubbles are grouped into connected pieces (by shared
// walls), and every pair of pieces is tested:
//   near    - an outer-film vertex of one piece within eps of an outer-film vertex of the other
//   inside  - an outer-film vertex of one piece lies inside the other piece (ray parity against
//             its closed outer surface): covers a bubble pushed deep into the cluster
// The event lists the smaller piece's bubbles in Grow: the rebuild enlarges those functions by
// about a grid cell before volume calibration, so the two pieces overlap and the diagram gives
// them a shared wall. Calibration then restores every volume (and overlap only grows under it,
// because each side loses volume to the other).
public static class Contacts
{
    // Connected pieces: pieceOf[k] = a representative bubble of k's piece (0 for bubbles with no films);
    // onHost[rep] = true when any bubble of that piece sits on the host (has a footprint).
    public static int[] Pieces(FoamMesh m, out bool[] onHost)
    {
        int n = m.BubbleCount;
        var parent = Enumerable.Range(0, n + 1).ToArray();
        Func<int, int> find = null;
        find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
        var alive = new bool[n + 1];
        var foot = new bool[n + 1];
        foreach (var t in m.T)
        {
            if (t.Neg > 0) alive[t.Neg] = true;
            if (t.Pos > 0) alive[t.Pos] = true;
            if (FoamMesh.IsSolid(t)) { int b = t.Neg == FoamMesh.Solid ? t.Pos : t.Neg; if (b > 0) foot[b] = true; }
            if (t.Neg > 0 && t.Pos > 0) parent[find(t.Neg)] = find(t.Pos);
        }
        var piece = new int[n + 1];
        onHost = new bool[n + 1];
        for (int k = 1; k <= n; k++)
        {
            if (!alive[k]) continue;
            piece[k] = find(k);
            if (foot[k]) onHost[piece[k]] = true;
        }
        return piece;
    }

    public static List<Rearranger.Event> Detect(FoamSolver s)
    {
        var m = s.Mesh;
        int n = m.BubbleCount;
        var events = new List<Rearranger.Event>();

        // Pieces: union-find over bubbles sharing a wall.
        var parent = Enumerable.Range(0, n + 1).ToArray();
        Func<int, int> find = null;
        find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
        var outerTris = new List<int>[n + 1];
        var footTris = new List<int>[n + 1];
        for (int k = 0; k <= n; k++) { outerTris[k] = new List<int>(); footTris[k] = new List<int>(); }
        var onFoot = new bool[m.V.Count];
        for (int i = 0; i < m.T.Count; i++)
        {
            var t = m.T[i];
            if (FoamMesh.IsSolid(t))
            {
                int b = t.Neg == FoamMesh.Solid ? t.Pos : t.Neg;
                if (b > 0) footTris[b].Add(i);
                onFoot[t.A] = onFoot[t.B] = onFoot[t.C] = true;
                continue;
            }
            if (t.Neg > 0 && t.Pos > 0) { parent[find(t.Neg)] = find(t.Pos); continue; }
            int k = t.Neg == 0 ? t.Pos : t.Neg;
            if (k > 0) outerTris[k].Add(i);
        }
        var pieces = new Dictionary<int, List<int>>();
        for (int k = 1; k <= n; k++)
        {
            if (s.IsDead(k) || outerTris[k].Count == 0) continue;
            int r = find(k);
            List<int> l;
            if (!pieces.TryGetValue(r, out l)) { l = new List<int>(); pieces[r] = l; }
            l.Add(k);
        }
        double eps = 0.5 * s.TargetEdge;

        // Landing on the host: a piece with no footprint whose outer film comes within eps of the
        // surface, or any piece whose outer film dips below it. The rebuild hands the part below the
        // surface to the solid, and the bubble becomes a dome sitting on it.
        if (s.Host != null)
            foreach (var p in pieces.Values)
            {
                bool hasFoot = p.Any(k => footTris[k].Count > 0);
                double limit = hasFoot ? -0.5 * eps : eps;
                int hitK = -1; Vec3 hitAt = Vec3.Zero; double hitH = double.MaxValue;
                foreach (int k in p)
                    foreach (int ti in outerTris[k])
                    {
                        var t = m.T[ti];
                        foreach (int v in new[] { t.A, t.B, t.C })
                        {
                            if (onFoot[v]) continue;
                            double hv = s.Host.Height(m.V[v]);
                            if (hv < limit && hv < hitH) { hitH = hv; hitK = k; hitAt = m.V[v]; }
                        }
                    }
                if (hitK > 0)
                {
                    // Landing: shift the bubble so its lowest point ends up one rebuild-grid cell inside the solid.
                    double cell = s.TargetEdge * s.S.RebuildGridFactor;
                    Vec3 shift = hasFoot ? Vec3.Zero : s.Host.Normal(hitAt) * (-(Math.Max(hitH, 0) + cell));
                    events.Add(new Rearranger.Event
                    {
                        Kind = "contact", Regions = new[] { hitK, FoamMesh.Solid }, Value = 0,
                        Detail = (hasFoot ? "pushed into" : "lands on") + " the surface (" + p.Count + "-bubble piece)",
                        At = s.Host.Project(hitAt), Radius = 4 * eps, Shift = shift,
                    });
                }
            }

        if (pieces.Count < 2) return events;
        var list = pieces.Values.ToList();
        // The piece's closed surface for the inside test includes its footprints.
        var tris = list.Select(p => p.SelectMany(k => outerTris[k].Concat(footTris[k])).ToList()).ToList();
        var verts = list.Select(p => p.SelectMany(k => outerTris[k]).ToList()).ToList().Select(ts => ts.SelectMany(ti => new[] { m.T[ti].A, m.T[ti].B, m.T[ti].C }).Distinct().ToList()).ToList();
        var lo = new Vec3[list.Count]; var hi = new Vec3[list.Count];
        for (int p = 0; p < list.Count; p++)
        {
            lo[p] = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            hi[p] = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (int v in verts[p])
            {
                var q = m.V[v];
                lo[p] = new Vec3(Math.Min(lo[p].X, q.X), Math.Min(lo[p].Y, q.Y), Math.Min(lo[p].Z, q.Z));
                hi[p] = new Vec3(Math.Max(hi[p].X, q.X), Math.Max(hi[p].Y, q.Y), Math.Max(hi[p].Z, q.Z));
            }
        }
        for (int a = 0; a < list.Count; a++)
            for (int b = a + 1; b < list.Count; b++)
            {
                if (lo[a].X > hi[b].X + eps || lo[b].X > hi[a].X + eps || lo[a].Y > hi[b].Y + eps ||
                    lo[b].Y > hi[a].Y + eps || lo[a].Z > hi[b].Z + eps || lo[b].Z > hi[a].Z + eps) continue;
                string how = null;
                Vec3 at;
                if (Near(m, verts[a], verts[b], eps, out at)) how = "near";
                else if (AnyInside(m, verts[a], tris[b], out at) || AnyInside(m, verts[b], tris[a], out at)) how = "inside";
                if (how == null) continue;
                int small = list[a].Count <= list[b].Count ? a : b, big = small == a ? b : a;
                events.Add(new Rearranger.Event
                {
                    Kind = "contact",
                    Regions = new[] { list[small][0], list[big][0] },
                    Value = 0,
                    Grow = list[small].ToArray(),
                    Detail = how + " (" + list[small].Count + "-bubble piece touches " + list[big].Count + "-bubble piece)",
                    At = at, Radius = 4 * eps,
                });
            }
        return events;
    }

    // Any vertex of A within eps of a vertex of B (spatial hash on B).
    static bool Near(FoamMesh m, List<int> a, List<int> b, double eps, out Vec3 at)
    {
        at = Vec3.Zero;
        var cells = new Dictionary<long, List<int>>(LongKey.Comparer);
        // unchecked inside the lambda: Rhino compiles with overflow checks, and an unchecked block
        // at the call site doesn't reach into a lambda body.
        Func<Vec3, long> key = p => unchecked(((long)Math.Floor(p.X / eps) * 73856093L) ^ ((long)Math.Floor(p.Y / eps) * 19349663L) ^ ((long)Math.Floor(p.Z / eps) * 83492791L));
        foreach (int v in b)
        {
            long k;
            unchecked { k = key(m.V[v]); }
            List<int> l;
            if (!cells.TryGetValue(k, out l)) { l = new List<int>(); cells[k] = l; }
            l.Add(v);
        }
        double e2 = eps * eps;
        foreach (int v in a)
        {
            var p = m.V[v];
            long cx = (long)Math.Floor(p.X / eps), cy = (long)Math.Floor(p.Y / eps), cz = (long)Math.Floor(p.Z / eps);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        long k;
                        unchecked { k = ((cx + dx) * 73856093L) ^ ((cy + dy) * 19349663L) ^ ((cz + dz) * 83492791L); }
                        List<int> l;
                        if (!cells.TryGetValue(k, out l)) continue;
                        foreach (int w in l) if ((m.V[w] - p).LengthSq < e2) { at = (m.V[w] + p) * 0.5; return true; }
                    }
        }
        return false;
    }

    // Is any of (a subsample of) the points inside the closed surface made of `tris`? Ray parity
    // along a fixed, slightly skewed direction (avoids hitting edges exactly).
    static bool AnyInside(FoamMesh m, List<int> pts, List<int> tris, out Vec3 at)
    {
        at = Vec3.Zero;
        var dir = new Vec3(0.8017837, 0.2672612, 0.5345225);
        int stride = Math.Max(1, pts.Count / 120);
        for (int i = 0; i < pts.Count; i += stride)
        {
            var o = m.V[pts[i]];
            int hits = 0;
            foreach (int ti in tris)
            {
                var t = m.T[ti];
                if (RayHits(o, dir, m.V[t.A], m.V[t.B], m.V[t.C])) hits++;
            }
            if ((hits & 1) == 1) { at = o; return true; }
        }
        return false;
    }

    // Möller–Trumbore, t > 0.
    public static bool RayHits(Vec3 o, Vec3 d, Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 e1 = b - a, e2 = c - a, p = Vec3.Cross(d, e2);
        double det = Vec3.Dot(e1, p);
        if (Math.Abs(det) < 1e-14) return false;
        double inv = 1 / det;
        Vec3 tv = o - a;
        double u = Vec3.Dot(tv, p) * inv;
        if (u < 0 || u > 1) return false;
        Vec3 q = Vec3.Cross(tv, e1);
        double v = Vec3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return false;
        return Vec3.Dot(e2, q) * inv > 1e-12;
    }
}

// ---- core/FreeBubble.cs ----
// A free bubble dropped into open space without a rebuild: an icosphere appended to the film mesh as
// its own piece (region k inside, outside air around it). A lone sphere is already at equilibrium, so it
// needs no relaxation; when it touches the foam, the normal contact rebuild joins it. Generators that
// spawn many bubbles (stream bed) would otherwise pay one rebuild per bubble.
public static class FreeBubble
{
    // Icosphere with edges about `edge` long, outward-facing, labelled (Neg = region, Pos = 0).
    public static void Append(FoamMesh m, int region, Vec3 center, double radius, double edge)
    {
        double t = (1 + Math.Sqrt(5)) / 2;
        var v = new List<Vec3>
        {
            new Vec3(-1, t, 0), new Vec3(1, t, 0), new Vec3(-1, -t, 0), new Vec3(1, -t, 0),
            new Vec3(0, -1, t), new Vec3(0, 1, t), new Vec3(0, -1, -t), new Vec3(0, 1, -t),
            new Vec3(t, 0, -1), new Vec3(t, 0, 1), new Vec3(-t, 0, -1), new Vec3(-t, 0, 1),
        };
        var f = new List<int[]>
        {
            new[] { 0, 11, 5 }, new[] { 0, 5, 1 }, new[] { 0, 1, 7 }, new[] { 0, 7, 10 }, new[] { 0, 10, 11 },
            new[] { 1, 5, 9 }, new[] { 5, 11, 4 }, new[] { 11, 10, 2 }, new[] { 10, 7, 6 }, new[] { 7, 1, 8 },
            new[] { 3, 9, 4 }, new[] { 3, 4, 2 }, new[] { 3, 2, 6 }, new[] { 3, 6, 8 }, new[] { 3, 8, 9 },
            new[] { 4, 9, 5 }, new[] { 2, 4, 11 }, new[] { 6, 2, 10 }, new[] { 8, 6, 7 }, new[] { 9, 8, 1 },
        };
        for (int i = 0; i < v.Count; i++) v[i] = v[i].Normalized();
        // Subdivide until edges are short enough (icosahedron edge = 1.0515 r).
        int levels = 0;
        double e0 = 1.0515 * radius;
        while (e0 / (1 << levels) > 1.15 * edge && levels < 6) levels++;
        for (int l = 0; l < levels; l++)
        {
            var mid = new Dictionary<long, int>();
            Func<int, int, int> midpoint = (a, b) =>
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                int idx;
                if (mid.TryGetValue(key, out idx)) return idx;
                idx = v.Count;
                v.Add(((v[a] + v[b]) * 0.5).Normalized());
                mid[key] = idx;
                return idx;
            };
            var nf = new List<int[]>(f.Count * 4);
            foreach (var tri in f)
            {
                int a = midpoint(tri[0], tri[1]), b = midpoint(tri[1], tri[2]), c = midpoint(tri[2], tri[0]);
                nf.Add(new[] { tri[0], a, c }); nf.Add(new[] { tri[1], b, a }); nf.Add(new[] { tri[2], c, b }); nf.Add(new[] { a, b, c });
            }
            f = nf;
        }
        // Orientation check: the normal must point out of the bubble (into the outside air, region 0).
        double vol = 0;
        foreach (var tri in f) vol += Vec3.Dot(v[tri[0]], Vec3.Cross(v[tri[1]], v[tri[2]]));
        bool flip = vol < 0;
        int baseIdx = m.V.Count;
        foreach (var p in v) m.V.Add(center + p * radius);
        foreach (var tri in f)
            m.T.Add(flip ? new FoamTri(baseIdx + tri[0], baseIdx + tri[2], baseIdx + tri[1], region, 0)
                         : new FoamTri(baseIdx + tri[0], baseIdx + tri[1], baseIdx + tri[2], region, 0));
    }
}

// ---- core/Generators.cs ----
// Generators decide where new bubbles go; the solver inserts them (FoamSolver.RequestAddBatch, one
// rebuild per batch). They run between solver ticks: in Grasshopper as components downstream of the
// solver's Foam output, or as user scripts calling the same Foam handle.

public class BorderDetailSettings
{
    public int PerBatch = 6;          // seeds per batch (one rebuild each batch)
    public double SizeMin = 0.15;     // new bubble radius as a fraction of the smallest bubble on that border
    public double SizeMax = 0.35;
    public int Where = 0;             // 0 any border, 1 outer borders only (touching the outside air: the visible ones), 2 inner only
    public double Spacing = 3;        // seeds at least this x (sum of the two seed radii) apart, and from nodes
    public double MinRadius = 0;      // floor (0: the solver's smallest, ~1 rebuild cell)
}

public static class BorderDetail
{
    public class Pick
    {
        public Vec3 At; public double Radius; public int[] Regions;
    }

    // Choose up to set.PerBatch seed points on Plateau borders. Borders are picked with probability
    // proportional to their length, then a point uniformly along the chosen border. The new radius is
    // a random fraction (SizeMin..SizeMax) of the smallest bubble meeting there.
    public static List<Pick> Choose(FoamSolver s, BorderDetailSettings set, Random rng)
    {
        var m = s.Mesh;
        var et = m.EdgeTris();
        List<int> nodes;
        var chains = FoamAnalysis.BorderChains(m, out nodes);
        var cand = new List<KeyValuePair<FoamAnalysis.Chain, int[]>>();
        var lens = new List<double>();
        foreach (var c in chains)
        {
            if (c.Verts.Count < 2) continue;
            List<int> tris;
            if (!et.TryGetValue(FoamMesh.EdgeKey(c.Verts[0], c.Verts[1]), out tris) || tris.Count != 3) continue;
            var regs = new SortedSet<int>();
            foreach (int ti in tris) { regs.Add(m.T[ti].Neg); regs.Add(m.T[ti].Pos); }
            if (regs.Contains(FoamMesh.Solid)) continue;              // where a wall stands on a host
            bool outer = regs.Contains(0);
            if ((set.Where == 1 && !outer) || (set.Where == 2 && outer)) continue;
            double len = 0;
            for (int i = 0; i + 1 < c.Verts.Count; i++) len += (m.V[c.Verts[i + 1]] - m.V[c.Verts[i]]).Length;
            if (len <= 0) continue;
            cand.Add(new KeyValuePair<FoamAnalysis.Chain, int[]>(c, regs.Where(r => r > 0).ToArray()));
            lens.Add(len);
        }
        var picks = new List<Pick>();
        if (cand.Count == 0) return picks;
        double total = lens.Sum();
        double floor = Math.Max(set.MinRadius, s.TargetEdge * s.S.RebuildGridFactor);
        var nodePts = nodes.Select(v => m.V[v]).ToList();
        for (int attempt = 0; attempt < set.PerBatch * 20 && picks.Count < set.PerBatch; attempt++)
        {
            // Border by length.
            double u = rng.NextDouble() * total, acc = 0; int ci = 0;
            for (; ci < cand.Count - 1; ci++) { acc += lens[ci]; if (acc >= u) break; }
            var chain = cand[ci].Key;
            // Point along it.
            double target = rng.NextDouble() * lens[ci], walked = 0;
            Vec3 at = m.V[chain.Verts[0]];
            for (int i = 0; i + 1 < chain.Verts.Count; i++)
            {
                Vec3 a = m.V[chain.Verts[i]], b = m.V[chain.Verts[i + 1]];
                double l = (b - a).Length;
                if (walked + l >= target) { at = a + (b - a) * ((target - walked) / Math.Max(l, 1e-12)); break; }
                walked += l;
            }
            double local = cand[ci].Value.Length == 0 ? 1 : cand[ci].Value.Min(k => s.DesiredRadius(k));
            double r = Math.Max(floor, local * (set.SizeMin + rng.NextDouble() * (set.SizeMax - set.SizeMin)));
            // Spacing from other picks and from nodes.
            bool ok = true;
            foreach (var p in picks) if ((p.At - at).Length < set.Spacing * (p.Radius + r)) { ok = false; break; }
            if (ok) foreach (var q in nodePts) if ((q - at).Length < set.Spacing * r) { ok = false; break; }
            if (!ok) continue;
            picks.Add(new Pick { At = at, Radius = r, Regions = cand[ci].Value });
        }
        return picks;
    }
}

public class StreamSettings
{
    public Vec3 Source;               // centre of the spawn disc (upstream)
    public Vec3 Flow = new Vec3(1, 0, 0);   // flow direction x strength (~0.3 gentle, ~1 brisk)
    public double Spread = 1;         // spawn disc radius, perpendicular to the flow
    public int Every = 120;           // solver steps between spawns
    public double RMin = 0.3, RMax = 0.7;
    public double Sink = 0;           // drift toward the host (the bed), same units as Flow
    public double Exit = 15;          // free bubbles further than this downstream of Source are removed
    public int MaxFree = 8;           // at most this many floating at once
}

// Stream bed: bubbles are spawned upstream, carried by the flow, and stick when they touch the bed (the
// host) or the stuck cluster; the rest float away and are removed. Call Tick between solver ticks.
public static class StreamBed
{
    public class State
    {
        public int NextSpawn = int.MinValue, Spawned, Removed;
        public HashSet<int> RemoveAsked = new HashSet<int>();
        public List<Vec3> LastSpawn = new List<Vec3>();
    }

    public static string Tick(FoamSolver s, StreamSettings set, Random rng, State st)
    {
        var m = s.Mesh;
        int n = m.BubbleCount;
        bool[] onHost;
        var piece = Contacts.Pieces(m, out onHost);
        // Stuck pieces: on the host; with no host, the largest piece (the cluster being built).
        var stuck = new bool[n + 1];
        if (s.Host != null) { for (int k = 1; k <= n; k++) stuck[k] = piece[k] > 0 && onHost[piece[k]]; }
        else
        {
            var size = new Dictionary<int, int>();
            for (int k = 1; k <= n; k++) if (piece[k] > 0) { int c; size.TryGetValue(piece[k], out c); size[piece[k]] = c + 1; }
            int big = size.Count == 0 ? 0 : size.OrderByDescending(kv => kv.Value).First().Key;
            for (int k = 1; k <= n; k++) stuck[k] = piece[k] > 0 && piece[k] == big && size[big] > 1;
        }
        var cen = s.Centroids();
        Vec3 dir = set.Flow.LengthSq > 0 ? set.Flow.Normalized() : new Vec3(1, 0, 0);
        // Forces on the floating bubbles; none on stuck ones.
        var force = new Vec3[n + 1];
        int floating = 0;
        for (int k = 1; k <= n; k++)
        {
            if (piece[k] == 0 || stuck[k] || s.IsDead(k)) continue;
            floating++;
            double r = Math.Max(s.DesiredRadius(k), 1e-6);
            Vec3 f = set.Flow / r;
            if (set.Sink != 0 && s.Host != null) f = f - s.Host.Normal(cen[k]) * (set.Sink / r);
            force[k] = f;
            // Left the domain downstream: remove.
            if (Vec3.Dot(cen[k] - set.Source, dir) > set.Exit && !st.RemoveAsked.Contains(k))
            {
                st.RemoveAsked.Add(k);
                s.RequestPop(k);
                st.Removed++;
            }
        }
        s.ExtForce = force;
        // Spawn.
        st.LastSpawn.Clear();
        if (s.Steps >= st.NextSpawn && floating < set.MaxFree && s.PendingInserts == 0)
        {
            Vec3 u = Vec3.Cross(dir, Math.Abs(dir.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0)).Normalized();
            Vec3 w = Vec3.Cross(dir, u);
            double a = 2 * Math.PI * rng.NextDouble(), rr = set.Spread * Math.Sqrt(rng.NextDouble());
            Vec3 p = set.Source + u * (rr * Math.Cos(a)) + w * (rr * Math.Sin(a));
            double rad = set.RMin + (set.RMax - set.RMin) * rng.NextDouble();
            // Keep clear of everything (a bubble just spawned may still be near the source).
            double gap = double.MaxValue;
            foreach (var q in m.V) gap = Math.Min(gap, (q - p).Length - rad);
            if (s.Host != null) gap = Math.Min(gap, s.Host.Height(p) - rad);
            if (gap > 2 * s.TargetEdge)
            {
                s.RequestInsertFree(p, rad);
                st.Spawned++;
                st.LastSpawn.Add(p);
                st.NextSpawn = s.Steps + set.Every;
            }
        }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "spawned {0}, removed {1}, floating {2}, stuck {3}", st.Spawned, st.Removed, floating,
            Enumerable.Range(1, n).Count(k => stuck[k] && !s.IsDead(k)));
    }
}

// ---- core/HostSurface.cs ----
// A wet solid the bubbles can rest on (Phase 5): a table, a plane, a ball, later an arm mesh.
//
// The solid is region FoamMesh.Solid (-1). Where a bubble sits on it, the bubble is closed by a
// "footprint": film triangles labelled (Solid | k) lying on the surface. Footprints carry no tension
// (a wet solid under a soap film has the same energy wet or dry), and their vertices slide on the
// surface. The film meets the surface at 90 degrees, which isn't imposed: it's what tension balance
// gives when the contact line can only move along the surface. Dry solid (Solid | outside) isn't
// meshed at all. For volumes the solid is just "outside", so footprints close each bubble.
public abstract class HostSurface
{
    // Signed distance: > 0 on the air side, < 0 inside the solid.
    public abstract double Height(Vec3 x);
    // Closest point on the surface.
    public abstract Vec3 Project(Vec3 x);
    // Unit normal at (or near) x, pointing into the air.
    public abstract Vec3 Normal(Vec3 x);

    // Mesher phase for the solid: steep, so it wins right at the surface and nowhere above it.
    public const double Steepness = 1000;
    public double Phase(Vec3 x) { return -Steepness * Height(x); }
}

public class PlaneHost : HostSurface
{
    public readonly Vec3 Origin, N;
    public PlaneHost(Vec3 origin, Vec3 normal) { Origin = origin; N = normal.Normalized(); }
    public override double Height(Vec3 x) { return Vec3.Dot(x - Origin, N); }
    public override Vec3 Project(Vec3 x) { return x - N * Height(x); }
    public override Vec3 Normal(Vec3 x) { return N; }
}

// Bubbles sitting on the outside of a ball (a simple curved host for testing).
public class SphereHost : HostSurface
{
    public readonly Vec3 C; public readonly double R;
    public SphereHost(Vec3 c, double r) { C = c; R = r; }
    public override double Height(Vec3 x) { return (x - C).Length - R; }
    public override Vec3 Project(Vec3 x) { var d = x - C; double l = d.Length; return l > 1e-12 ? C + d * (R / l) : C + new Vec3(R, 0, 0); }
    public override Vec3 Normal(Vec3 x) { var d = x - C; double l = d.Length; return l > 1e-12 ? d / l : new Vec3(1, 0, 0); }
}

// ---- core/Pop.cs ----
// Popping a bubble (Phase 3, T2), done on the mesh by relabelling, without a rebuild.
//
// A bubble pops when one of its films breaks:
//   - it has an outer film: that film breaks, its air joins the outside. Region k becomes 0, its
//     outer film (now 0|0) disappears, and its walls become outer films of the neighbours, which
//     then bulge out as they relax.
//   - it's fully inside the cluster: one of its walls breaks and it merges with that neighbour
//     (the largest shared wall). Region k becomes j, the k|j wall disappears, j keeps both volumes.
// Every other film keeps its vertices and orientation, so nothing else moves and all the other
// volumes stay exact. Only the films around the popped bubble change, and the solver relaxes them.
public static class Pop
{
    public class Result
    {
        public int Bubble, Into;   // Into = 0: popped to the outside; > 0: merged into that bubble
        public double Volume;      // the popped bubble's volume
        public string Fail;
        public override string ToString()
        {
            if (Fail != null) return "pop bubble " + Bubble + " refused: " + Fail;
            return Into == 0 ? "popped bubble " + Bubble + " (its air escaped)"
                             : "popped bubble " + Bubble + ": it was inside the cluster, so it merged into bubble " + Into;
        }
    }

    // into: -1 = decide as above, 0 = force popping to the outside, j > 0 = merge into j.
    // Changes the mesh in place only on success.
    public static Result Apply(FoamMesh m, int k, int into = -1)
    {
        var res = new Result { Bubble = k, Into = into };
        if (k < 1 || k > m.BubbleCount) { res.Fail = "no bubble " + k; return res; }
        double outer = 0;
        var wall = new Dictionary<int, double>();
        int alive = 0;
        var has = new bool[m.BubbleCount + 1];
        foreach (var t in m.T)
        {
            if (t.Neg > 0) has[t.Neg] = true;
            if (t.Pos > 0) has[t.Pos] = true;
            if (t.Neg != k && t.Pos != k) continue;
            int o = t.Neg == k ? t.Pos : t.Neg;
            double a = m.TriArea(t);
            if (o == FoamMesh.Solid) continue;   // its footprint on a host
            if (o == 0) outer += a;
            else { double w; wall.TryGetValue(o, out w); wall[o] = w + a; }
        }
        for (int b = 1; b <= m.BubbleCount; b++) if (has[b]) alive++;
        if (!has[k]) { res.Fail = "bubble " + k + " doesn't exist any more"; return res; }
        if (alive <= 1) { res.Fail = "it's the last bubble"; return res; }
        if (into < 0) into = outer > 0 || wall.Count == 0 ? 0 : wall.OrderByDescending(kv => kv.Value).First().Key;
        if (into > 0 && !wall.ContainsKey(into)) { res.Fail = "bubble " + into + " isn't a neighbour of " + k; return res; }
        res.Into = into;
        res.Volume = m.Volumes()[k];

        var copy = m.Clone();
        var keep = new List<FoamTri>(copy.T.Count);
        foreach (var t0 in copy.T)
        {
            var t = t0;
            if (t.Neg == k) t.Neg = into;
            if (t.Pos == k) t.Pos = into;
            if (t.Neg == t.Pos) continue;   // the broken film
            if ((t.Neg == FoamMesh.Solid && t.Pos == 0) || (t.Pos == FoamMesh.Solid && t.Neg == 0)) continue; // footprint, now dry
            keep.Add(t);
        }
        copy.T = keep;
        copy.Clean();
        var hist = Remesher.EdgeHistogram(copy);
        if (hist[1] > 0) { res.Fail = "the mesh would have " + hist[1] + " open edges"; return res; }
        m.V = copy.V; m.T = copy.T;
        return res;
    }

    // The bubble containing p, or 0 if p is outside every bubble.
    public static int InsideBubble(FoamMesh m, Vec3 p) { return BubbleAt(m, p, false); }

    // The bubble containing p (ray parity against each bubble's closed surface); if p is outside
    // every bubble, the one whose surface is closest (or 0 when nearest is false).
    public static int BubbleAt(FoamMesh m, Vec3 p, bool nearest = true)
    {
        var dir = new Vec3(0.8017837, 0.2672612, 0.5345225);
        var hits = new int[m.BubbleCount + 1];
        var near = new double[m.BubbleCount + 1];
        for (int k = 0; k <= m.BubbleCount; k++) near[k] = double.MaxValue;
        foreach (var t in m.T)
        {
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            bool hit = Contacts.RayHits(p, dir, a, b, c);
            double d = (FoamAnalysis.ClosestOnTriangle(p, a, b, c) - p).LengthSq;
            foreach (int k in new[] { t.Neg, t.Pos })
            {
                if (k <= 0) continue;
                if (hit) hits[k]++;
                if (d < near[k]) near[k] = d;
            }
        }
        // Inside more than one (shouldn't happen): the one whose surface is nearest.
        int best = 0; double bd = double.MaxValue;
        for (int k = 1; k <= m.BubbleCount; k++)
            if ((hits[k] & 1) == 1 && near[k] < bd) { bd = near[k]; best = k; }
        if (best > 0 || !nearest) return best;
        for (int k = 1; k <= m.BubbleCount; k++)
            if (near[k] < bd) { bd = near[k]; best = k; }
        return best;
    }
}

// ---- core/Rearranger.cs ----
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
        // Bubbles this job adds (the last AddCount regions), each seeded at AddAts[i] with seed radius AddR0s[i],
        // growing to AddRadii[i]. A batch goes in with one rebuild (generators add many at once).
        public List<double> AddRadii = new List<double>();
        public List<Vec3> AddAts = new List<Vec3>();
        public List<double> AddR0s = new List<double>();
        public int AddCount { get { return AddRadii.Count; } }
        public double AddRadius { get { return AddRadii.Count > 0 ? AddRadii[0] : 0; } }   // single-add view (diagnostics)
        public Vec3 AddAt { get { return AddAts.Count > 0 ? AddAts[0] : Vec3.Zero; } }
        public double AddR0 { get { return AddR0s.Count > 0 ? AddR0s[0] : 0; } }
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
        public FoamMesh Snap;                // snapshot of the mesh at Prepare
        public BubbleFunctions Fn0;          // the functions before Run changed them (to re-run a refused rebuild offline)
        public Vec3[] Centroid;              // bubble centroids at Prepare (calibration rescues swallowed bubbles there)
        public List<Vec3> ZoneAt = new List<Vec3>();
        public List<double> ZoneR = new List<double>();
        public ShapeTransfer.Stats Transfer;   // bubbles enlarged by ~one grid cell before calibration (contacts)

        // A fresh copy of this job (functions as prepared), to run again: harness `failrepro`.
        public RebuildJob Retry()
        {
            var j = (RebuildJob)MemberwiseClone();
            j.Fn = (Fn0 ?? Fn).Clone();
            j.Fn0 = null; j.Mesh = null; j.Candidate = null; j.Fail = null; j.Info = null; j.Calibration = null; j.Transfer = null;
            j.Grow = new List<int>(Grow); j.Shift = new List<KeyValuePair<int, Vec3>>(Shift);
            j.AddRadii = new List<double>(AddRadii); j.AddAts = new List<Vec3>(AddAts); j.AddR0s = new List<double>(AddR0s);
            return j;
        }

        public void Run()
        {
            if (Fn0 == null) Fn0 = Fn.Clone();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long tCalV = 0, tBuildV = 0, tTransferV = 0;
            var stageV = new System.Text.StringBuilder();
            try
            {
                // For an add, calibrate the existing bubbles first, then (re)place the seed: calibrating
                // with the seed present let the neighbours squeeze it out ("new bubble got no volume").
                int existing = Fn.Count - AddCount;
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
                Calibration = CalibrateVolumes(Fn, Lo, Hi, H * GridFactor / 1.5, CurrentVolume, existing, Centroid);
                tCalV = sw.ElapsedMilliseconds;
                for (int i = 0; i < AddCount; i++) PlaceSeed(Fn, existing + i + 1, AddAts[i], AddR0s[i]);
                string info;
                var stage = stageV;
                long tStage = sw.ElapsedMilliseconds;
                MobiusMesher.StageTimer = name => { long now = sw.ElapsedMilliseconds; stage.Append(" ").Append(name).Append(" ").Append(now - tStage); tStage = now; };
                var mesh = MobiusMesher.Build(Fn, Lo, Hi, H, out info, Scale, GridFactor, RemeshIters);
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    var hb = Remesher.EdgeHistogram(mesh);
                    if (hb[1] == 0 && hb[4] + hb[5] == 0) break;
                    info += "; retried on a shifted grid (" + hb[1] + " open, " + (hb[4] + hb[5]) + " 4+ film edges)";
                    string info2;
                    mesh = MobiusMesher.Build(Fn, Lo, Hi, H, out info2, Scale, GridFactor, RemeshIters, attempt);
                }
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
                    if (k >= CurrentVolume.Length) { if (AddCount == 1 && !(vol[k] > 0)) Fail = "the new bubble got no volume"; }   // in a batch, a seed that took no space is just left out
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
        return PrepareAdds(s, new List<Vec3> { at }, new List<double> { r0 }, new List<double> { radius },
                           "add bubble " + (s.Mesh.BubbleCount + 1) + " at " + at);
    }

    // Several seeds in one rebuild (generators). Later seeds are placed against earlier ones too, so
    // seeds closer than their r0 carve each other: keep them spaced.
    public static RebuildJob PrepareAdds(FoamSolver s, List<Vec3> ats, List<double> r0s, List<double> radii, string what)
    {
        var job = Prepare(s, what);
        var old = job.Fn;
        int n = old.Count, m = ats.Count;
        var fn = new BubbleFunctions(n + m) { Host = old.Host };
        Array.Copy(old.A, fn.A, n); Array.Copy(old.B, fn.B, n); Array.Copy(old.D, fn.D, n);
        job.Fn = fn;
        // Nudge each seed: centred exactly where four bubbles already tie, a seed produced a
        // non-generic edge where four films meet ("edges with 4+ films").
        var jr = new Random(s.Steps * 7919 + n);
        if (job.Scale != null)
        {
            var sc = new double[job.Scale.Length + m];
            Array.Copy(job.Scale, sc, job.Scale.Length);
            for (int i = 0; i < m; i++) sc[job.Scale.Length + i] = MobiusMesher.ScaleFor(radii[i], s.TargetEdge, s.S.EdgesPerRadius);
            job.Scale = sc;
        }
        for (int i = 0; i < m; i++)
        {
            double r0 = r0s[i];
            Vec3 at = ats[i] + new Vec3(jr.NextDouble() - 0.5, jr.NextDouble() - 0.5, jr.NextDouble() - 0.5) * (0.2 * r0);
            PlaceSeed(fn, n + i + 1, at, r0);
            job.AddAts.Add(at); job.AddR0s.Add(r0); job.AddRadii.Add(radii[i]);
            // A seed outside the current foam (AddAt in open space) must be inside the rebuild box.
            var reach = new Vec3(3 * r0 + 2 * job.H, 3 * r0 + 2 * job.H, 3 * r0 + 2 * job.H);
            Vec3 lo = at - reach, hi = at + reach;
            job.Lo = new Vec3(Math.Min(job.Lo.X, lo.X), Math.Min(job.Lo.Y, lo.Y), Math.Min(job.Lo.Z, lo.Z));
            job.Hi = new Vec3(Math.Max(job.Hi.X, hi.X), Math.Max(job.Hi.Y, hi.Y), Math.Max(job.Hi.Z, hi.Z));
            if (job.Old != null) { job.ZoneAt.Add(at); job.ZoneR.Add(r0); }
        }
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
    public static bool RescueSwallowed = true;   // harness switch for A/B

    public static string CalibrateVolumes(BubbleFunctions fn, Vec3 lo, Vec3 hi, double cell, double[] target, int count, Vec3[] centroid = null)
    {
        if (!RescueSwallowed) centroid = null;
        var rescued = new int[count + 1];
        var rescueNote = new List<string>();
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
        // Spatial bins of the bubbles' spheres (f_b < 0 only inside the sphere centre -B/2A, radius^2 = |c|^2 - D/A),
        // so a sample is tested only against the few spheres that can contain it. Testing every bubble at every
        // sample of the whole box took up to 3 s per rebuild once an r 10 bubble made the box large (run 2026-10-18_125753).
        double ext = Math.Max(hi.X - lo.X, Math.Max(hi.Y - lo.Y, hi.Z - lo.Z));
        double bin = Math.Max(4 * cell, ext / 48);
        int bx = Math.Max(1, (int)Math.Ceiling((hi.X - lo.X) / bin)), by = Math.Max(1, (int)Math.Ceiling((hi.Y - lo.Y) / bin)), bz = Math.Max(1, (int)Math.Ceiling((hi.Z - lo.Z) / bin));
        var bins = new List<int>[bx * by * bz];
        for (int it = 0; it < 16; it++)
        {
            Array.Clear(vol, 0, vol.Length);
            for (int q = 0; q < bins.Length; q++) { if (bins[q] == null) bins[q] = new List<int>(4); else bins[q].Clear(); }
            for (int b = 0; b < n; b++)
            {
                if (!(fn.A[b] > 0)) continue;
                Vec3 c = fn.B[b] * (-0.5 / fn.A[b]);
                double rr = c.LengthSq - fn.D[b] / fn.A[b];
                if (!(rr > 0)) continue;
                double rad = Math.Sqrt(rr);
                int i0 = Math.Max(0, (int)Math.Floor((c.X - rad - lo.X) / bin)), i1 = Math.Min(bx - 1, (int)Math.Floor((c.X + rad - lo.X) / bin));
                int j0 = Math.Max(0, (int)Math.Floor((c.Y - rad - lo.Y) / bin)), j1 = Math.Min(by - 1, (int)Math.Floor((c.Y + rad - lo.Y) / bin));
                int k0 = Math.Max(0, (int)Math.Floor((c.Z - rad - lo.Z) / bin)), k1 = Math.Min(bz - 1, (int)Math.Floor((c.Z + rad - lo.Z) / bin));
                for (int kk = k0; kk <= k1; kk++)
                    for (int jj = j0; jj <= j1; jj++)
                        for (int ii = i0; ii <= i1; ii++) bins[(kk * by + jj) * bx + ii].Add(b);
            }
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        if (solid != null && solid[(k * ny + j) * nx + i]) continue;
                        double x = lo.X + (i + 0.5) * cell, y = lo.Y + (j + 0.5) * cell, z = lo.Z + (k + 0.5) * cell;
                        int bi = Math.Min(bx - 1, (int)((x - lo.X) / bin)), bj = Math.Min(by - 1, (int)((y - lo.Y) / bin)), bk = Math.Min(bz - 1, (int)((z - lo.Z) / bin));
                        var cand = bins[(bk * by + bj) * bx + bi];
                        if (cand.Count == 0) continue;   // outside every sphere: outside air
                        double r2 = x * x + y * y + z * z;
                        int best = 0; double bf = 0; // outside: f = 0, bubble wins where f < its rivals
                        foreach (int b in cand)
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
            // Rescue: a bubble the fit left with (almost) no space is usually swallowed by a big neighbour whose
            // function is far deeper at that spot (r 7.8 bubble: -3.06 at the centre of an r 0.55 bubble scoring
            // -0.38; run 2026-10-18_125753). The regular step below is far too small to win that back, so the bubble
            // is first made to win at its own current centroid, by a margin worth about half its radius; the
            // regular steps then fix its volume.
            bool anyRescue = false;
            if (centroid != null)
                for (int b = 1; b <= n; b++)
                {
                    if (b >= target.Length || !(target[b] > 0) || b >= centroid.Length || rescued[b] >= 2) continue;
                    if (vol[b] >= 0.2 * target[b]) continue;
                    Vec3 c = centroid[b];
                    double rival = 0;   // the outside air
                    for (int j = 1; j <= n; j++) if (j != b && fn.A[j - 1] > 0) rival = Math.Min(rival, fn.F(j, c));
                    double r = Math.Pow(3 * target[b] / (4 * Math.PI), 1.0 / 3);
                    double want = rival - fn.A[b - 1] * r * r * 0.5;
                    double fb = fn.F(b, c);
                    if (fb > want) { fn.D[b - 1] += want - fb; anyRescue = true; rescued[b]++; rescueNote.Add(b.ToString()); }
                }
            if (anyRescue) continue;
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
        return string.Format(CultureInfo.InvariantCulture, "volume calibration: worst error {0:P0} -> {1:P0}", worstBefore, worstAfter) +
               (rescueNote.Count > 0 ? " (rescued swallowed bubble " + string.Join(", ", rescueNote.Distinct()) + ")" : "");
    }

    // Function `index` (1-based; the last one by default) becomes a sphere of radius r0 at `at`, shifted
    // to beat every function before it (and the outside air, f = 0) right at `at`.
    public static void PlaceSeed(BubbleFunctions fn, Vec3 at, double r0) { PlaceSeed(fn, fn.Count, at, r0); }

    public static void PlaceSeed(BubbleFunctions fn, int index, Vec3 at, double r0)
    {
        int n = index - 1;
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
        var job = PrepareCore(s, what, events);
        if (s.S.TransferShape) job.Old = job.Snap;
        return job;
    }

    static RebuildJob PrepareCore(FoamSolver s, string what, List<Event> events)
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
            Snap = m.Clone(),
            Centroid = s.Centroids(),
            ZoneAt = events == null ? new List<Vec3>() : events.Select(e => e.At).ToList(),
            ZoneR = events == null ? new List<double>() : events.Select(e => e.Radius).ToList(),
        };
    }
}

// ---- core/ShapeTransfer.cs ----
// Makes a rebuild act locally (Phase 3). The rebuilt mesh comes from fitted functions, so away
// from the event it only approximates the films it replaces. Squeezed small bubbles lost 20-30%
// of their volume per rebuild and the whole cluster visibly jumped (adds28 run: 19 rebuilds, bubbles at
// 71-94% after each). Here every rebuilt vertex outside the event zone is moved back onto the
// old surface with the same labels:
//   film vertex (2 regions)   -> closest point on the old film between the same two regions
//   border vertex (3 regions) -> closest point on the old border between the same three
//   node (4 regions)          -> the old node with the same four
// Topology comes from the rebuild and shape from the old mesh. Inside the zone the rebuilt
// geometry is kept (that's where the topology changed), with a smooth blend between the two.
// Vertices that find no old match nearby (new walls, new borders) stay where they are.
public static class ShapeTransfer
{
    public class Stats
    {
        public int Moved, Kept, Unmatched;
        public double MeanShift, MaxShift;
        public string Volumes = "";
        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "shape transfer: {0} vertices moved back onto the old films (mean {1:G2}, max {2:G2}), {3} kept in the event zone, {4} unmatched",
                Moved, MeanShift, MaxShift, Kept, Unmatched) + Volumes;
        }
    }

    // zoneAt / zoneR: event points and radii. Full transfer beyond zoneR + inner, none within
    // zoneR, smoothstep blend over `blend`. maxDist: an old match further away than this is ignored.
    // trust: shifts up to this are fit error and fully undone; from there to 2 x trust the transfer
    // fades out. A larger shift means the films really moved (e.g. around a dragged bubble whose pinch
    // spans its whole outline): pulling those back made bubble 14 136% in the drag_gh replay.
    // The weight field is then eased over the mesh so a vertex that keeps its rebuilt position
    // doesn't leave a step next to transferred neighbours.
    public static Stats Apply(FoamMesh nm, FoamMesh old, IList<Vec3> zoneAt, IList<double> zoneR, double inner, double blend, double maxDist, double trust)
    {
        var st = new Stats();
        var vOld = old.Volumes();
        var vBefore = nm.Volumes();
        var oldReg = old.VertexRegions();
        var newReg = nm.VertexRegions();
        var oldEdges = old.EdgeTris();

        // Old incident triangles and border edges per vertex.
        var vt = new List<int>[old.V.Count];
        var vb = new List<int>[old.V.Count];       // border neighbours
        for (int v = 0; v < old.V.Count; v++) { vt[v] = new List<int>(6); vb[v] = new List<int>(2); }
        for (int i = 0; i < old.T.Count; i++) { var t = old.T[i]; vt[t.A].Add(i); vt[t.B].Add(i); vt[t.C].Add(i); }
        foreach (var kv in oldEdges)
        {
            if (!old.IsBorder(kv.Value)) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            vb[a].Add(b); vb[b].Add(a);
        }

        // Hash of old vertices.
        double cell = maxDist;
        var grid = new Dictionary<long, List<int>>(LongKey.Comparer);
        for (int v = 0; v < old.V.Count; v++)
        {
            if (oldReg[v].Count < 2) continue;
            long k = Cell(old.V[v], cell);
            List<int> l;
            if (!grid.TryGetValue(k, out l)) { l = new List<int>(); grid[k] = l; }
            l.Add(v);
        }

        double sum = 0;
        var target = new Vec3[nm.V.Count];
        var wt = new double[nm.V.Count];
        for (int v = 0; v < nm.V.Count; v++)
        {
            var labels = newReg[v];
            if (labels.Count < 2) continue;
            var p = nm.V[v];
            double w = Weight(p, zoneAt, zoneR, inner, blend);
            if (w <= 0) { st.Kept++; continue; }

            // Nearest old vertex whose labels contain this vertex's labels.
            int best = -1; double bd = maxDist * maxDist;
            long cx = (long)Math.Floor(p.X / cell), cy = (long)Math.Floor(p.Y / cell), cz = (long)Math.Floor(p.Z / cell);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        List<int> l;
                        if (!grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out l)) continue;
                        foreach (int o in l)
                        {
                            double d = (old.V[o] - p).LengthSq;
                            if (d < bd && Contains(oldReg[o], labels)) { bd = d; best = o; }
                        }
                    }
            if (best < 0) { st.Unmatched++; continue; }

            Vec3 q;
            if (labels.Count >= 4)
            {
                if (oldReg[best].Count != labels.Count) { st.Unmatched++; continue; }
                q = old.V[best];
            }
            else if (labels.Count == 3)
                q = ClosestOnBorder(old, oldEdges, vb, best, labels, p);
            else
                q = ClosestOnFilm(old, vt, best, labels[0], labels[1], p);
            if (double.IsNaN(q.X)) { st.Unmatched++; continue; }

            double shift = (q - p).Length;
            double x = (2 * trust - shift) / trust;      // 1 at shift <= trust, 0 at 2 x trust
            double wTrust = x >= 1 ? 1 : x <= 0 ? 0 : x * x * (3 - 2 * x);
            if (wTrust <= 0) { st.Unmatched++; continue; }
            target[v] = q;
            wt[v] = w * wTrust;
            st.Moved++;
            sum += shift;
            if (shift > st.MaxShift) st.MaxShift = shift;
        }
        // Ease the weights: a vertex never transfers more than the average of itself and its ring.
        var ring = new List<int>[nm.V.Count];
        for (int v = 0; v < nm.V.Count; v++) ring[v] = new List<int>(6);
        foreach (var kv in nm.EdgeTris())
        {
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            ring[a].Add(b); ring[b].Add(a);
        }
        for (int it = 0; it < 3; it++)
        {
            var nw = new double[wt.Length];
            for (int v = 0; v < wt.Length; v++)
            {
                if (wt[v] <= 0 || ring[v].Count == 0) continue;
                double avg = 0;
                foreach (int u in ring[v]) avg += wt[u];
                avg /= ring[v].Count;
                nw[v] = Math.Min(wt[v], 0.5 * (wt[v] + avg));
            }
            wt = nw;
        }
        for (int v = 0; v < nm.V.Count; v++)
            if (wt[v] > 0) nm.V[v] = nm.V[v] + (target[v] - nm.V[v]) * wt[v];
        st.MeanShift = st.Moved > 0 ? sum / st.Moved : 0;
        var vAfter = nm.Volumes();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var parts = new List<string>();
        for (int k = 1; k < vOld.Length && k < vAfter.Length; k++)
            if (vOld[k] > 0 && (Math.Abs(vAfter[k] / vOld[k] - 1) > 0.05 || Math.Abs(vBefore[k] / vOld[k] - 1) > 0.05))
                parts.Add(k + ": " + (vBefore[k] / vOld[k]).ToString("P0", ci) + " -> " + (vAfter[k] / vOld[k]).ToString("P0", ci));
        if (parts.Count > 0) st.Volumes = "; volume vs snapshot, rebuilt -> transferred: " + string.Join(", ", parts);
        return st;
    }

    static double Weight(Vec3 p, IList<Vec3> at, IList<double> r, double inner, double blend)
    {
        double d = double.MaxValue;
        for (int i = 0; i < at.Count; i++) d = Math.Min(d, (p - at[i]).Length - r[i]);
        double x = (d - inner) / Math.Max(blend, 1e-12);
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        return x * x * (3 - 2 * x);
    }

    static bool Contains(List<int> sup, List<int> sub)
    {
        foreach (int r in sub) if (!sup.Contains(r)) return false;
        return true;
    }

    // Closest point on the old film between regions a and b, among the triangles around `start`
    // and its neighbours (2-ring), which is enough at rebuild-grid distances.
    static Vec3 ClosestOnFilm(FoamMesh old, List<int>[] vt, int start, int a, int b, Vec3 p)
    {
        var ring = new HashSet<int>();
        foreach (int ti in vt[start])
        {
            var t = old.T[ti];
            ring.Add(t.A); ring.Add(t.B); ring.Add(t.C);
        }
        Vec3 best = new Vec3(double.NaN, 0, 0); double bd = double.MaxValue;
        var done = new HashSet<int>();
        foreach (int u in ring)
            foreach (int ti in vt[u])
            {
                if (!done.Add(ti)) continue;
                var t = old.T[ti];
                if (!((t.Neg == a && t.Pos == b) || (t.Neg == b && t.Pos == a))) continue;
                var q = FoamAnalysis.ClosestOnTriangle(p, old.V[t.A], old.V[t.B], old.V[t.C]);
                double d = (q - p).LengthSq;
                if (d < bd) { bd = d; best = q; }
            }
        return best;
    }

    // Closest point on the old border with these three regions, walking a few edges along it
    // from `start` in both directions.
    static Vec3 ClosestOnBorder(FoamMesh old, Dictionary<long, List<int>> oldEdges, List<int>[] vb, int start, List<int> labels, Vec3 p)
    {
        Vec3 best = new Vec3(double.NaN, 0, 0); double bd = double.MaxValue;
        var seen = new HashSet<int> { start };
        var frontier = new List<int> { start };
        for (int depth = 0; depth < 4 && frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (int u in frontier)
                foreach (int w in vb[u])
                {
                    List<int> tris;
                    if (!oldEdges.TryGetValue(FoamMesh.EdgeKey(u, w), out tris)) continue;
                    bool match = true;
                    foreach (int r in labels)
                    {
                        bool any = false;
                        foreach (int ti in tris) if (old.T[ti].Neg == r || old.T[ti].Pos == r) { any = true; break; }
                        if (!any) { match = false; break; }
                    }
                    if (!match) continue;
                    var q = FoamAnalysis.ClosestOnSegment(p, old.V[u], old.V[w]);
                    double d = (q - p).LengthSq;
                    if (d < bd) { bd = d; best = q; }
                    if (seen.Add(w)) next.Add(w);
                }
            frontier = next;
        }
        return best;
    }

    static long Cell(Vec3 p, double cell)
    {
        return Key((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell));
    }

    static long Key(long x, long y, long z)
    {
        unchecked { return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L); }
    }
}


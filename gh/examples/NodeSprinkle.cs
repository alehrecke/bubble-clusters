// Example custom generator for the Bubble Solver: a Rhino 8 C# Script component.
//
// It talks to the solver through the Foam handle only, via `dynamic`, so it needs no reference to the
// plugin. Copy it, change the "choose" part, and you have your own generator.
//
// Setup: a C# Script component with inputs
//   Foam  (object, item)   <- the Bubble Solver's Foam output
//   Run   (bool, item)     <- a toggle
//   Seed  (int, item)      <- -1 for random
//   Count (int, item)      <- bubbles to add in total
// and one output: Info.
//
// What this one does: whenever the foam has settled, put up to 3 small bubbles at random four-way
// nodes (where four bubbles meet), sized 0.2-0.4 x the bubbles there. Repeat until Count.
//
// The handle (see FoamHandle in gh/BubbleEngine.cs):
//   foam.Step, foam.Settled, foam.Busy, foam.CellSize, foam.MinRadius
//   foam.BubbleIds, foam.Centroid(k), foam.Radius(k), foam.Pressure(k), foam.BubbleAt(point)
//   foam.Borders (List<Polyline>), foam.BorderBubbles (List<int[]>, 0 = outside air), foam.Nodes (List<Point3d>)
//   foam.Add(point, r), foam.AddBatch(points, radii, "label"), foam.Pop(point), foam.Wake()
// Keep added seeds at least a couple of CellSizes apart; radii below foam.MinRadius are raised to it.

using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Grasshopper;
using Grasshopper.Kernel;

public class Script_Instance : GH_ScriptInstance
{
#if GH_CHECK
    public override void InvokeRunScript(Grasshopper.Kernel.IGH_Component owner, object rhinoDocument, int iteration, List<object> inputs, Grasshopper.Kernel.IGH_DataAccess DA) { }
#endif
    // Members keep their values between solutions: this is the generator's state.
    Random rng;
    int seedUsed = -1, added, lastSeed = int.MinValue;
    object lastFoam;

    private void RunScript(object Foam, bool Run, int Seed, int Count, ref object Info)
    {
        if (Foam == null) { Info = "Connect the solver's Foam output."; return; }
        dynamic foam = Foam is Grasshopper.Kernel.Types.GH_ObjectWrapper ? ((Grasshopper.Kernel.Types.GH_ObjectWrapper)Foam).Value : Foam;

        // New simulation or new seed: start over.
        if (rng == null || !ReferenceEquals(foam, lastFoam) || Seed != lastSeed)
        {
            seedUsed = Seed >= 0 ? Seed : new Random().Next(1000000);
            rng = new Random(seedUsed);
            added = 0;
        }
        lastFoam = foam; lastSeed = Seed;

        string status = "off";
        if (Run && added < Count)
        {
            if (foam.Busy || !foam.Settled) status = "waiting for the foam to settle";
            else
            {
                // ---- choose: random nodes, spaced, sized from the bubbles around them ----
                List<Point3d> nodes = foam.Nodes;
                var pts = new List<Point3d>(); var radii = new List<double>();
                for (int tries = 0; tries < 50 && pts.Count < Math.Min(3, Count - added) && nodes.Count > 0; tries++)
                {
                    Point3d p = nodes[rng.Next(nodes.Count)];
                    int k = foam.BubbleAt(p + new Vector3d(1e-3, 0, 0));   // one of the bubbles at that node
                    double local = k > 0 ? (double)foam.Radius(k) : 1.0;
                    double r = Math.Max((double)foam.MinRadius, local * (0.2 + 0.2 * rng.NextDouble()));
                    if (pts.Any(q => q.DistanceTo(p) < 4 * r)) continue;
                    pts.Add(p); radii.Add(r);
                }
                // ---- act ----
                if (pts.Count > 0)
                {
                    foam.AddBatch(pts, radii, "node sprinkle, seed " + seedUsed);
                    added += pts.Count;
                    status = "added " + pts.Count;
                }
                else status = "no free nodes";
            }
        }
        else if (added >= Count) status = "done";
        Info = "seed " + seedUsed + "\nadded " + added + " / " + Count + "\n" + status;
    }
}

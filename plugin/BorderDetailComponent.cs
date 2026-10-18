using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BubbleClusters
{
    // Generator: sprinkles small bubbles along the Plateau borders of a running Bubble Solver.
    // Wire the solver's Foam output in. It adds a batch (one rebuild), waits for the foam to relax,
    // adds the next, until Total. Seed >= 0 gives the same choices every time; -1 picks a new random
    // seed on each Reset (Info shows it, so a result you like can be reproduced).
    public class BorderDetailComponent : GH_Component
    {
        Random rng;
        int seedUsed = -1, added, lastBatchStep = int.MinValue, lastSeedInput = int.MinValue;
        bool lastReset;
        FoamHandle lastHandle;
        List<Point3d> lastPoints = new List<Point3d>();
        List<double> lastRadii = new List<double>();
        string status = "";

        public BorderDetailComponent()
            : base("Border Detail", "BorderDetail",
                   "Generator: adds small bubbles along the Plateau borders of a running Bubble Solver, batch by batch.",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("c4f6b3d2-9a5e-4c7f-b8d0-2e3f40516273"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.secondary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Foam", "Foam", "The Bubble Solver's Foam output.", GH_ParamAccess.item);
            p.AddBooleanParameter("Run", "Run", "Keep adding batches until Total.", GH_ParamAccess.item, false);
            p.AddIntegerParameter("Seed", "Seed", "Random seed; -1 = a new random seed on every Reset (shown in Info).", GH_ParamAccess.item, -1);
            p.AddIntegerParameter("Total", "Total", "Stop after adding this many bubbles.", GH_ParamAccess.item, 30);
            p.AddIntegerParameter("PerBatch", "PerBatch", "Bubbles per batch (one rebuild each).", GH_ParamAccess.item, 6);
            p.AddNumberParameter("SizeMin", "SizeMin", "New radius, as a fraction of the smallest bubble on that border: minimum.", GH_ParamAccess.item, 0.15);
            p.AddNumberParameter("SizeMax", "SizeMax", "... maximum.", GH_ParamAccess.item, 0.35);
            p.AddIntegerParameter("Where", "Where", "0 any border, 1 outer borders only (the visible ones), 2 inner only.", GH_ParamAccess.item, 0);
            p.AddNumberParameter("Spacing", "Spacing", "Seeds at least this x (sum of their radii) apart, and from nodes.", GH_ParamAccess.item, 3);
            p.AddIntegerParameter("Pace", "Pace", "Solver steps to wait after a batch lands (0 = wait until the foam settles).", GH_ParamAccess.item, 150);
            p.AddBooleanParameter("Reset", "Reset", "Start over: count from 0, new seed if Seed is -1.", GH_ParamAccess.item, false);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("Info", "Info", "Seed used, progress, last batch.", GH_ParamAccess.item);
            p.AddPointParameter("Points", "Points", "Seed points of the last batch.", GH_ParamAccess.list);
            p.AddNumberParameter("Radii", "Radii", "Radii of the last batch.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object o = null;
            DA.GetData(0, ref o);
            var gw = o as GH_ObjectWrapper;
            var h = (gw != null ? gw.Value : o) as FoamHandle;
            bool run = false, reset = false;
            int seed = -1, total = 30, perBatch = 6, where = 0, pace = 150;
            double sizeMin = 0.15, sizeMax = 0.35, spacing = 3;
            DA.GetData(1, ref run); DA.GetData(2, ref seed); DA.GetData(3, ref total); DA.GetData(4, ref perBatch);
            DA.GetData(5, ref sizeMin); DA.GetData(6, ref sizeMax); DA.GetData(7, ref where); DA.GetData(8, ref spacing);
            DA.GetData(9, ref pace); DA.GetData(10, ref reset);
            if (h == null || !h.IsValid) { DA.SetData(0, "Connect the Bubble Solver's Foam output."); return; }

            // (Re)start: Reset pressed, a new simulation (solver rebuilt), or a different Seed.
            if ((reset && !lastReset) || h != lastHandle || seed != lastSeedInput || rng == null)
            {
                seedUsed = seed >= 0 ? seed : (Environment.TickCount & 0x7fffffff) % 1000000;
                rng = new Random(seedUsed);
                added = 0; lastBatchStep = int.MinValue; lastPoints.Clear(); lastRadii.Clear();
                status = "ready";
            }
            lastReset = reset; lastHandle = h; lastSeedInput = seed;

            if (run && added < total)
            {
                bool rested = h.Settled || (pace > 0 && h.Step - lastBatchStep >= pace);
                if (h.Busy) status = "waiting: the solver is inserting / rearranging";
                else if (!rested) status = "waiting for the foam to relax (" + (pace > 0 ? (h.Step - lastBatchStep) + "/" + pace + " steps" : "until settled") + ")";
                else
                {
                    var set = new BorderDetailSettings
                    {
                        PerBatch = Math.Max(1, Math.Min(perBatch, total - added)), SizeMin = sizeMin, SizeMax = Math.Max(sizeMin, sizeMax),
                        Where = where, Spacing = spacing,
                    };
                    var picks = BorderDetail.Choose(h.Solver, set, rng);
                    if (picks.Count == 0) status = "no room left on the chosen borders (try lower Spacing or SizeMin)";
                    else
                    {
                        lastPoints = picks.Select(q => new Point3d(q.At.X, q.At.Y, q.At.Z)).ToList();
                        lastRadii = picks.Select(q => q.Radius).ToList();
                        h.AddBatch(lastPoints, lastRadii, "border detail, seed " + seedUsed);
                        added += picks.Count;
                        lastBatchStep = h.Step;
                        status = "added a batch of " + picks.Count;
                    }
                }
            }
            else if (added >= total) status = "done";
            else status = "off (set Run)";

            DA.SetData(0, string.Format(CultureInfo.InvariantCulture, "seed {0}  (type it into Seed to repeat this)\nadded {1} / {2}\n{3}",
                seedUsed, added, total, status));
            DA.SetDataList(1, lastPoints);
            DA.SetDataList(2, lastRadii);
        }
    }
}

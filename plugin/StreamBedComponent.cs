using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BubbleClusters
{
    // Generator: a stream. Bubbles are spawned upstream around Source, carried along Flow, and stick when
    // they touch the bed (the solver's host) or the stuck cluster; the rest float on and are removed past
    // Exit. Floating bubbles are dropped in without a rebuild; joining happens through the normal contact
    // rebuild. Best with a HostPlane / HostMesh on the solver (the bed); without one, the largest cluster
    // is what catches them.
    public class StreamBedComponent : GH_Component
    {
        Random rng;
        StreamBed.State state;
        int seedUsed = -1, lastSeedInput = int.MinValue;
        bool lastReset, wasRunning;
        FoamHandle lastHandle;
        string status = "", setKey = "";

        static string SetKey(StreamSettings s)
        {
            var ci = CultureInfo.InvariantCulture;
            Func<Vec3, string> v = q => q.X.ToString("R", ci) + " " + q.Y.ToString("R", ci) + " " + q.Z.ToString("R", ci);
            return "source " + v(s.Source) + " flow " + v(s.Flow) + " spread " + s.Spread.ToString("R", ci) + " every " + s.Every +
                   " rmin " + s.RMin.ToString("R", ci) + " rmax " + s.RMax.ToString("R", ci) + " sink " + s.Sink.ToString("R", ci) +
                   " exit " + s.Exit.ToString("R", ci) + " maxfree " + s.MaxFree;
        }

        public StreamBedComponent()
            : base("Stream Bed", "StreamBed",
                   "Generator: bubbles float down a stream from Source along Flow and collect on the bed (the solver's host) or the cluster.",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("d5a7c4e3-0b6f-4d80-9c1e-3f4051627384"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.secondary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Foam", "Foam", "The Bubble Solver's Foam output.", GH_ParamAccess.item);
            p.AddBooleanParameter("Run", "Run", "Keep the stream going.", GH_ParamAccess.item, false);
            p.AddIntegerParameter("Seed", "Seed", "Random seed; -1 = a new random seed on every Reset (shown in Info).", GH_ParamAccess.item, -1);
            p.AddPointParameter("Source", "Source", "Where bubbles appear (upstream).", GH_ParamAccess.item);
            p.AddVectorParameter("Flow", "Flow", "Flow direction; its length is the strength (~0.3 gentle, ~1 brisk).", GH_ParamAccess.item, new Vector3d(0.5, 0, 0));
            p.AddNumberParameter("Spread", "Spread", "Radius of the spawn disc around Source, across the flow.", GH_ParamAccess.item, 1.5);
            p.AddIntegerParameter("Every", "Every", "Solver steps between spawns.", GH_ParamAccess.item, 80);
            p.AddNumberParameter("RMin", "RMin", "Smallest spawned radius.", GH_ParamAccess.item, 0.3);
            p.AddNumberParameter("RMax", "RMax", "Largest spawned radius.", GH_ParamAccess.item, 0.6);
            p.AddNumberParameter("Sink", "Sink", "Drift toward the bed (same units as Flow; 0 = they only stick where they bump into something).", GH_ParamAccess.item, 0.25);
            p.AddNumberParameter("Exit", "Exit", "Floating bubbles further than this downstream of Source are removed.", GH_ParamAccess.item, 10);
            p.AddIntegerParameter("MaxFree", "MaxFree", "At most this many floating at once.", GH_ParamAccess.item, 6);
            p.AddBooleanParameter("Reset", "Reset", "Start over: counts from 0, new seed if Seed is -1.", GH_ParamAccess.item, false);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("Info", "Info", "Seed used, counts, state.", GH_ParamAccess.item);
            p.AddPointParameter("Spawned", "Spawned", "Where the latest bubble appeared.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object o = null;
            DA.GetData(0, ref o);
            var gw = o as GH_ObjectWrapper;
            var h = (gw != null ? gw.Value : o) as FoamHandle;
            bool run = false, reset = false;
            int seed = -1, every = 80, maxFree = 6;
            Point3d source = Point3d.Unset; Vector3d flow = new Vector3d(0.5, 0, 0);
            double spread = 1.5, rmin = 0.3, rmax = 0.6, sink = 0.25, exit = 10;
            DA.GetData(1, ref run); DA.GetData(2, ref seed); DA.GetData(3, ref source); DA.GetData(4, ref flow);
            DA.GetData(5, ref spread); DA.GetData(6, ref every); DA.GetData(7, ref rmin); DA.GetData(8, ref rmax);
            DA.GetData(9, ref sink); DA.GetData(10, ref exit); DA.GetData(11, ref maxFree); DA.GetData(12, ref reset);
            if (h == null || !h.IsValid) { DA.SetData(0, "Connect the Bubble Solver's Foam output."); return; }
            if (!source.IsValid || Params.Input[3].SourceCount == 0) { DA.SetData(0, "Connect a Source point (where bubbles appear)."); return; }

            if ((reset && !lastReset) || h != lastHandle || seed != lastSeedInput || rng == null)
            {
                seedUsed = seed >= 0 ? seed : (Environment.TickCount & 0x7fffffff) % 1000000;
                rng = new Random(seedUsed);
                state = new StreamBed.State();
            }
            lastReset = reset; lastHandle = h; lastSeedInput = seed;

            var spawned = new List<Point3d>();
            if (run)
            {
                var set = new StreamSettings
                {
                    Source = new Vec3(source.X, source.Y, source.Z), Flow = new Vec3(flow.X, flow.Y, flow.Z),
                    Spread = Math.Max(0, spread), Every = Math.Max(1, every), RMin = Math.Max(1e-3, rmin), RMax = Math.Max(rmin, rmax),
                    Sink = sink, Exit = exit, MaxFree = Math.Max(1, maxFree),
                };
                if (!wasRunning || setKey != SetKey(set))
                {
                    setKey = SetKey(set);
                    h.Note("stream generator seed " + seedUsed + " " + setKey);
                }
                // Respect the solver's triangle limit: stop spawning near it (floating ones keep going).
                if (h.Triangles + h.EstimateTriangles(set.RMax) > h.TriangleLimit) set.MaxFree = 0;
                status = StreamBed.Tick(h.Solver, set, rng, state);
                if (set.MaxFree == 0) status += string.Format(CultureInfo.InvariantCulture, "\npaused spawning: the mesh is at {0:N0} triangles (limit {1:N0})", h.Triangles, h.TriangleLimit);
                spawned = state.LastSpawn.Select(q => new Point3d(q.X, q.Y, q.Z)).ToList();
                // Keep the solver running: it stops ticking once settled, and this generator only runs on its ticks.
                if (h.Settled || state.LastSpawn.Count > 0) h.Wake();
                wasRunning = true;
            }
            else
            {
                if (wasRunning) { h.Solver.ExtForce = null; h.Note("stream generator off"); h.Wake(); }   // stop the flow
                wasRunning = false;
                status = "off (set Run)";
            }
            DA.SetData(0, string.Format(CultureInfo.InvariantCulture, "seed {0}  (type it into Seed to repeat this)\n{1}", seedUsed, status));
            DA.SetDataList(1, spawned);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BubbleClusters
{
    // The dual of a Bubble Solver foam (core/FoamDual.cs): one point per bubble (its volume centroid), one edge per
    // shared film. Arcs = true: each edge is the circular arc centre A -> a point on the shared film -> centre B, so it
    // never cuts through an unrelated neighbour; false: straight lines. Stubs: edges from each skin bubble out
    // through its outer film. Downstream of the solver, so it updates live.
    public class DualComponent : GH_Component
    {
        public DualComponent()
            : base("Bubble Dual", "Dual",
                   "The dual of a Bubble Solver foam: bubble centres connected through their shared films (arcs or straight lines), plus stubs out through the outer films.",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("3c1d9a72-5e84-4b0f-9a61-7d2e8f4b1c05"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.tertiary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Foam", "Foam", "The Bubble Solver's Foam output.", GH_ParamAccess.item);
            p.AddBooleanParameter("Arcs", "Arcs", "True: arcs through the shared films. False: straight lines between centres.", GH_ParamAccess.item, true);
            p.AddBooleanParameter("Stubs", "Stubs", "Include stubs from skin bubbles out through their outer films.", GH_ParamAccess.item, true);
            p.AddNumberParameter("StubLength", "StubLength", "How far stubs reach past the outer film, x mean bubble radius (0 = end on the film).", GH_ParamAccess.item, 0.35);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPointParameter("Centres", "Centres", "One point per bubble (volume centroid).", GH_ParamAccess.list);
            p.AddCurveParameter("Edges", "Edges", "One curve per pair of touching bubbles (arc or line).", GH_ParamAccess.list);
            p.AddCurveParameter("Stubs", "Stubs", "From each skin bubble out through its outer film.", GH_ParamAccess.list);
            p.AddPointParameter("FilmPoints", "FilmPoints", "Per edge: the point on the shared film the arc passes through.", GH_ParamAccess.list);
            p.AddNumberParameter("Weights", "Weights", "Per edge: the shared film's area (e.g. for strut thickness).", GH_ParamAccess.list);
            p.AddTextParameter("Pairs", "Pairs", "Per edge: the two bubble ids, \"a-b\".", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Counts.", GH_ParamAccess.item);
        }

        static Point3d P(Vec3 v) { return new Point3d(v.X, v.Y, v.Z); }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object o = null;
            DA.GetData(0, ref o);
            var gw = o as GH_ObjectWrapper;
            var h = (gw != null ? gw.Value : o) as FoamHandle;
            if (h == null || !h.IsValid) { DA.SetData(6, "Connect the Bubble Solver's Foam output."); return; }
            bool arcs = true, stubs = true; double stubLen = 0.35;
            DA.GetData(1, ref arcs); DA.GetData(2, ref stubs); DA.GetData(3, ref stubLen);

            var s = h.Solver;
            var dual = FoamDual.Build(s.Mesh, k => s.IsDead(k), Math.Max(0, stubLen));
            var centres = dual.Nodes.Select(n => P(n.Center)).ToList();
            var edges = new List<Curve>(); var filmPts = new List<Point3d>(); var weights = new List<double>(); var pairs = new List<string>();
            var stubCurves = new List<Curve>();
            int straightFallback = 0;
            foreach (var e in dual.Edges)
            {
                var a = P(dual.ByBubble[e.A].Center);
                if (e.IsStub)
                {
                    if (stubs) stubCurves.Add(new LineCurve(a, P(e.StubEnd)));
                    continue;
                }
                var b = P(dual.ByBubble[e.B].Center); var f = P(e.FilmPoint);
                Curve c = null;
                if (arcs)
                {
                    var arc = new Arc(a, f, b);
                    if (arc.IsValid) c = new ArcCurve(arc); else straightFallback++;
                }
                edges.Add(c ?? new LineCurve(a, b));
                filmPts.Add(f); weights.Add(e.Area); pairs.Add(e.A + "-" + e.B);
            }
            string info = string.Format(CultureInfo.InvariantCulture, "{0} bubbles, {1} edges ({2}), {3} stubs{4}",
                centres.Count, edges.Count, arcs ? "arcs" : "straight", stubs ? stubCurves.Count : 0,
                straightFallback > 0 ? "; " + straightFallback + " nearly straight arcs drawn as lines" : "");
            if (!h.Settled) info += "\nthe foam is still relaxing: the dual will move a little until it settles";
            DA.SetDataList(0, centres); DA.SetDataList(1, edges); DA.SetDataList(2, stubCurves);
            DA.SetDataList(3, filmPts); DA.SetDataList(4, weights); DA.SetDataList(5, pairs); DA.SetData(6, info);
        }
    }
}

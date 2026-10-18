using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BubbleClusters
{
    // Clean Plateau borders for fabrication: smoothed, and replaced by exact circular arcs where they are
    // within tolerance of one (most are: a border is where two near-spherical films meet). Every border
    // ends exactly on its nodes, so the network is connected for MultiPipe / mesh thickening.
    public class BorderCurvesComponent : GH_Component
    {
        public BorderCurvesComponent()
            : base("Border Curves", "BorderCurves",
                   "Clean Plateau borders from a Bubble Solver: exact arcs where they fit, smooth curves elsewhere; nodes shared exactly.",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("e6b8d5f4-1c70-4e91-8d2f-405162738495"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.tertiary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Foam", "Foam", "The Bubble Solver's Foam output.", GH_ParamAccess.item);
            p.AddIntegerParameter("Smooth", "Smooth", "Smoothing passes before the arc test (0 = none).", GH_ParamAccess.item, 12);
            p.AddNumberParameter("ArcTol", "ArcTol", "Use an exact arc when the border is within this x CellSize of one (0 = never).", GH_ParamAccess.item, 0.5);
            p.AddNumberParameter("Spacing", "Spacing", "Polyline point spacing (0 = CellSize). Arcs also get at least one point per 10 degrees.", GH_ParamAccess.item, 0);
            p.AddBooleanParameter("Host", "Host", "Include the outlines where bubbles sit on a host.", GH_ParamAccess.item, true);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter("Curves", "Curves", "Arcs / circles where they fit, smooth degree-3 curves elsewhere.", GH_ParamAccess.list);
            p.AddCurveParameter("Polylines", "Polylines", "The same borders as evenly resampled polylines.", GH_ParamAccess.list);
            p.AddPointParameter("Nodes", "Nodes", "Points where borders meet (each shared exactly by its borders).", GH_ParamAccess.list);
            p.AddBooleanParameter("IsArc", "IsArc", "Per border: replaced by an exact arc.", GH_ParamAccess.list);
            p.AddBooleanParameter("Outer", "Outer", "Per border: touches the outside air (the visible ones).", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Counts and fit quality.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object o = null;
            DA.GetData(0, ref o);
            var gw = o as GH_ObjectWrapper;
            var h = (gw != null ? gw.Value : o) as FoamHandle;
            if (h == null || !h.IsValid) { DA.SetData(5, "Connect the Bubble Solver's Foam output."); return; }
            int smooth = 12; double arcTol = 0.5, spacing = 0; bool host = true;
            DA.GetData(1, ref smooth); DA.GetData(2, ref arcTol); DA.GetData(3, ref spacing); DA.GetData(4, ref host);

            var set = new BorderCleanSettings { Iterations = Math.Max(0, smooth), ArcTolerance = Math.Max(0, arcTol), Spacing = spacing, IncludeHost = host };
            var borders = BorderClean.Clean(h.Solver.Mesh, h.CellSize, set);
            var curves = new List<Curve>(); var polys = new List<Curve>();
            var isArc = new List<bool>(); var outer = new List<bool>();
            var nodeIds = new HashSet<int>(); var nodes = new List<Point3d>();
            foreach (var b in borders)
            {
                var pts = b.Points.Select(q => new Point3d(q.X, q.Y, q.Z)).ToList();
                var pl = new Polyline(pts);
                if (b.Closed) pl.Add(pl[0]);
                polys.Add(new PolylineCurve(pl));
                Curve crv = null;
                if (b.IsArc)
                {
                    if (b.Closed)
                        crv = new ArcCurve(new Circle(new Plane(new Point3d(b.Center.X, b.Center.Y, b.Center.Z), new Vector3d(b.Normal.X, b.Normal.Y, b.Normal.Z)), b.Radius));
                    else
                    {
                        var arc = new Arc(pts[0], pts[pts.Count / 2], pts[pts.Count - 1]);
                        if (arc.IsValid) crv = new ArcCurve(arc);
                    }
                }
                if (crv == null) crv = b.Closed ? Curve.CreateInterpolatedCurve(pl, 3, CurveKnotStyle.ChordPeriodic) : Curve.CreateInterpolatedCurve(pts, 3);
                curves.Add(crv ?? new PolylineCurve(pl));
                isArc.Add(b.IsArc);
                outer.Add(b.Bubbles.Contains(0));
                foreach (int nid in new[] { b.StartNode, b.EndNode })
                    if (nid >= 0 && nodeIds.Add(nid)) { var q = h.Solver.Mesh.V[nid]; nodes.Add(new Point3d(q.X, q.Y, q.Z)); }
            }
            var devs = borders.Where(b => !double.IsNaN(b.ArcDeviation)).Select(b => b.ArcDeviation / h.CellSize).OrderBy(x => x).ToList();
            string info = string.Format(CultureInfo.InvariantCulture, "{0} borders: {1} exact arcs, {2} smooth curves; {3} nodes; {4} outer",
                borders.Count, borders.Count(b => b.IsArc), borders.Count(b => !b.IsArc), nodes.Count, outer.Count(x => x));
            if (devs.Count > 0)
                info += string.Format(CultureInfo.InvariantCulture, "\nborder vs arc (x CellSize): median {0:F2}, worst {1:F2}", devs[devs.Count / 2], devs[devs.Count - 1]);
            if (!h.Settled) info += "\nthe foam is still relaxing: borders will move a little until it settles";
            DA.SetDataList(0, curves); DA.SetDataList(1, polys); DA.SetDataList(2, nodes);
            DA.SetDataList(3, isArc); DA.SetDataList(4, outer); DA.SetData(5, info);
        }
    }
}

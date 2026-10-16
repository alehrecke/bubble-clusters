using System;
using System.Collections;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace BubbleClusters
{
    // The bubble solver as a compiled component. Same inputs, outputs and behaviour as the paste-in
    // script (gh/BubbleSolver.cs): both run BubbleEngine. Input/output names matter: the engine looks
    // them up by name to tell wired from unwired (see BubbleEngine.InputWired / Wired).
    public class BubbleSolverComponent : GH_Component
    {
        readonly BubbleEngine engine = new BubbleEngine();

        public BubbleSolverComponent()
            : base("Bubble Solver", "Bubbles",
                   "Soap-bubble cluster at equilibrium: minimum film area with each bubble's air volume fixed. " +
                   "Live resize, drag, add, pop and gather; bubbles can rest on a host plane or mesh.",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("b3e5a2c1-8f4d-4b6e-a7c9-1d2e3f405162"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.primary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter("Centers", "Centers", "Starting bubble centres. Moving one later drags its bubble (when Pull > 0).", GH_ParamAccess.list);
            p.AddNumberParameter("Radii", "Radii", "Bubble radii (each keeps the air of a free sphere of that radius). Changing one inflates/deflates it live.", GH_ParamAccess.list);
            p.AddNumberParameter("CellSize", "CellSize", "Mesh edge length; <= 0: smallest radius / 6.", GH_ParamAccess.item, 0);
            p.AddBooleanParameter("Pack", "Pack", "Slide centres apart so no two overlap more than a double bubble.", GH_ParamAccess.item, false);
            p.AddBooleanParameter("Reset", "Reset", "Rebuild from the inputs.", GH_ParamAccess.item, false);
            p.AddBooleanParameter("Run", "Run", "Keep relaxing; stops by itself once settled.", GH_ParamAccess.item, false);
            p.AddNumberParameter("Pull", "Pull", "Drag strength (0 = off and releases all; ~1 soft, ~5 stiff).", GH_ParamAccess.item, 0);
            p.AddBooleanParameter("Add", "Add", "Button: add a bubble (at AddAt, or a random interior node).", GH_ParamAccess.item, false);
            p.AddNumberParameter("AddRadius", "AddRadius", "Radius of added bubbles (<= 0: mean of Radii).", GH_ParamAccess.item, 0);
            p.AddBooleanParameter("Pop", "Pop", "Button: pop the bubble at PopAt.", GH_ParamAccess.item, false);
            p.AddPointParameter("PopAt", "PopAt", "A point inside the bubble to pop.", GH_ParamAccess.item);
            p.AddPointParameter("AddAt", "AddAt", "Where Add puts the bubble (inside the foam: nearest border; outside: right there).", GH_ParamAccess.item);
            p.AddNumberParameter("Gather", "Gather", "Every bubble drifts toward GatherTo (0 = off; ~0.2 gentle, ~1 firm).", GH_ParamAccess.item, 0);
            p.AddPointParameter("GatherTo", "GatherTo", "Gather target (not connected: the cluster centre, on the host if any).", GH_ParamAccess.item);
            p.AddPlaneParameter("HostPlane", "HostPlane", "A wet surface the bubbles rest on; its Z axis points to the air.", GH_ParamAccess.item);
            p.AddMeshParameter("HostMesh", "HostMesh", "A wet mesh the bubbles rest on; normals point to the air.", GH_ParamAccess.item);
            for (int i = 2; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter("Films", "Films", "One mesh per film (walls and outer caps).", GH_ParamAccess.list);
            p.AddMeshParameter("Bubbles", "Bubbles", "One closed mesh per bubble, normals outward.", GH_ParamAccess.list);
            p.AddCurveParameter("Borders", "Borders", "Plateau borders (polylines).", GH_ParamAccess.list);
            p.AddPointParameter("Nodes", "Nodes", "Points where four borders meet.", GH_ParamAccess.list);
            p.AddNumberParameter("Pressures", "Pressures", "Per bubble, relative to outside.", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Diagnostics.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var centers = new List<Point3d>(); var radii = new List<double>();
            DA.GetDataList(0, centers);
            DA.GetDataList(1, radii);
            double cell = 0, pull = 0, addRadius = 0, gather = 0;
            bool pack = false, reset = false, run = false, add = false, pop = false;
            Point3d popAt = Point3d.Unset, addAt = Point3d.Unset, gatherTo = Point3d.Unset;
            Plane hostPlane = Plane.Unset; Mesh hostMesh = null;
            DA.GetData(2, ref cell); DA.GetData(3, ref pack); DA.GetData(4, ref reset); DA.GetData(5, ref run);
            DA.GetData(6, ref pull); DA.GetData(7, ref add); DA.GetData(8, ref addRadius); DA.GetData(9, ref pop);
            DA.GetData(10, ref popAt); DA.GetData(11, ref addAt); DA.GetData(12, ref gather); DA.GetData(13, ref gatherTo);
            DA.GetData(14, ref hostPlane); DA.GetData(15, ref hostMesh);

            engine.Component = this;
            engine.GrasshopperDocument = OnPingDocument();
            object films = null, bubbles = null, borders = null, nodes = null, pressures = null, info = null;
            engine.Solve(centers, radii, cell, pack, reset, run, pull, add, addRadius, pop, popAt, addAt, gather, gatherTo,
                         hostPlane, hostMesh, ref films, ref bubbles, ref borders, ref nodes, ref pressures, ref info);
            if (films != null) DA.SetDataList(0, (IEnumerable)films);
            if (bubbles != null) DA.SetDataList(1, (IEnumerable)bubbles);
            if (borders != null) DA.SetDataList(2, (IEnumerable)borders);
            if (nodes != null) DA.SetDataList(3, (IEnumerable)nodes);
            if (pressures != null) DA.SetDataList(4, (IEnumerable)pressures);
            if (info != null) DA.SetData(5, info.ToString());
        }

        public override BoundingBox ClippingBox { get { return engine.ClippingBox; } }
        public override void DrawViewportMeshes(IGH_PreviewArgs args) { engine.DrawViewportMeshes(args); }
        public override void DrawViewportWires(IGH_PreviewArgs args) { engine.DrawViewportWires(args); }
    }
}

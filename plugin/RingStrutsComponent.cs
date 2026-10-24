using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BubbleClusters
{
    // Ring Struts (core/Struts.cs): the Plateau borders of a Bubble Solver foam as round metal wire, checked live
    // against casting limits (defaults: Shapeways Silver 925), and on request as one watertight solid for export.
    // Near the Bore (Ring Seed's HostRing) wires are pushed out so the bore is cut flat and round with 0.8 x Wire of
    // metal left. Live outputs are cached per solver step; the solid only builds once the foam has settled.
    public class RingStrutsComponent : GH_Component
    {
        public RingStrutsComponent()
            : base("Ring Struts", "Struts",
                   "Plateau borders as castable wire: live pipes and casting warnings (thin, too close, closed openings); with Solid on, one watertight mesh to send to Shapeways.",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("5f2b8c19-6d4e-4a73-9e0b-c81d3a7f6e52"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.tertiary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Foam", "Foam", "The Bubble Solver's Foam output.", GH_ParamAccess.item);
            p.AddNumberParameter("Wire", "Wire", "Wire diameter (mm). Shapeways silver: 0.8 minimum; 1.0 recommended.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("NodeScale", "NodeScale", "Node size x the wire (rounded, slightly thicker joints).", GH_ParamAccess.item, 1.35);
            p.AddCircleParameter("Bore", "Bore", "The finger (Ring Seed's HostRing): wires are kept out of it and the bore is cut round.", GH_ParamAccess.item);
            p.AddBooleanParameter("Host", "Host", "Include the borders lying on the finger (inner rails).", GH_ParamAccess.item, true);
            p.AddNumberParameter("MinOpening", "MinOpening", "Warn when an opening between wires is narrower than this (mm).", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("Clearance", "Clearance", "Minimum air between wires that don't meet (mm). Shapeways: 0.3.", GH_ParamAccess.item, 0.3);
            p.AddBooleanParameter("Solid", "Solid", "Build the watertight mesh for export (once the foam has settled; a few seconds).", GH_ParamAccess.item, false);
            p.AddNumberParameter("Voxel", "Voxel", "Solid resolution (mm); 0 = Wire / 8.", GH_ParamAccess.item, 0);
            for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter("Wires", "Wires", "Wire centrelines (after keeping them out of the bore).", GH_ParamAccess.list);
            p.AddMeshParameter("Pipes", "Pipes", "Live preview of the wire as pipes with round nodes (overlapping, not for export).", GH_ParamAccess.item);
            p.AddMeshParameter("Solid", "Solid", "One closed mesh (with Solid on): export as STL / OBJ for Shapeways.", GH_ParamAccess.item);
            p.AddPointParameter("Warnings", "Warnings", "Where a casting rule is broken.", GH_ParamAccess.list);
            p.AddTextParameter("Kinds", "Kinds", "Per warning: closed (opening filled with metal), tight (opening < MinOpening), clearance (wires too close), short (border shorter than the wire: a blob).", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Counts, weight, size.", GH_ParamAccess.item);
        }

        // Caches: live results per (step, settings); the solid per (step, settings).
        string liveKey, solidKey;
        List<Curve> cWires; Mesh cPipes; List<Point3d> cWarn; List<string> cKinds; string cInfo;
        Mesh cSolid; string cSolidInfo;

        static Point3d P(Vec3 v) { return new Point3d(v.X, v.Y, v.Z); }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object o = null;
            DA.GetData(0, ref o);
            var gw = o as GH_ObjectWrapper;
            var h = (gw != null ? gw.Value : o) as FoamHandle;
            if (h == null || !h.IsValid) { DA.SetData(5, "Connect the Bubble Solver's Foam output."); return; }
            double wire = 1.0, nodeScale = 1.35, minOpen = 1.0, clearance = 0.3, voxel = 0; bool host = true, solid = false;
            Circle bore = Circle.Unset;
            DA.GetData(1, ref wire); DA.GetData(2, ref nodeScale);
            if (!DA.GetData(3, ref bore)) bore = Circle.Unset;
            DA.GetData(4, ref host); DA.GetData(5, ref minOpen); DA.GetData(6, ref clearance); DA.GetData(7, ref solid); DA.GetData(8, ref voxel);
            if (!(wire > 0)) { DA.SetData(5, "Wire must be positive."); return; }

            CylinderHost cyl = null;
            if (bore.IsValid && bore.Radius > 0)
                cyl = new CylinderHost(new Vec3(bore.Center.X, bore.Center.Y, bore.Center.Z), new Vec3(bore.Normal.X, bore.Normal.Y, bore.Normal.Z), bore.Radius);
            var s = h.Solver;
            string settings = string.Join("|", wire, nodeScale, cyl != null ? bore.Center + " " + bore.Normal + " " + bore.Radius : "-", host, minOpen, clearance);
            string key = h.Step + "|" + h.Triangles + "|" + settings;

            List<CleanBorder> borders = null; List<List<Vec3>> wires = null; List<Vec3> nodes = null;
            Action prepare = () =>
            {
                if (wires != null) return;
                borders = BorderClean.Clean(s.Mesh, h.CellSize, new BorderCleanSettings { Spacing = Math.Max(0.25 * wire, h.CellSize * 0.5), IncludeHost = host });
                wires = Struts.Prepare(borders, wire, cyl);
                nodes = Struts.Nodes(borders, wires);
            };

            if (key != liveKey)
            {
                prepare();
                var warn = Struts.Check(s.Mesh, borders, wires, nodes, wire, clearance, minOpen);
                cWires = wires.Select(l => (Curve)new PolylineCurve(l.Select(P))).ToList();
                var pipes = new Mesh();
                foreach (var c in cWires)
                {
                    var pm = Mesh.CreateFromCurvePipe(c, wire / 2, 8, 1, MeshPipeCapStyle.Dome, false, null);
                    if (pm != null) pipes.Append(pm);
                }
                foreach (var n in nodes) pipes.Append(Mesh.CreateFromSphere(new Sphere(P(n), wire / 2 * Math.Max(1, nodeScale)), 10, 8));
                pipes.Normals.ComputeNormals();
                cPipes = pipes;
                cWarn = warn.Select(x => P(x.At)).ToList();
                cKinds = warn.Select(x => x.Kind + string.Format(CultureInfo.InvariantCulture, " {0:F2} mm", x.Value)).ToList();
                // Length of wire and rough weight (pipes + node spheres; overlaps counted twice, so a slight overestimate).
                double len = wires.Sum(l => { double t = 0; for (int i = 1; i < l.Count; i++) t += (l[i] - l[i - 1]).Length; return t; });
                double vol = len * Math.PI * wire * wire / 4;
                var bb = new BoundingBox(wires.SelectMany(l => l.Select(P)));
                cInfo = string.Format(CultureInfo.InvariantCulture,
                    "{0} wires, {1} nodes, {2:F0} mm of wire at {3:F2} mm\nabout {4:F1} g sterling (before the solid)\nsize {5:F1} x {6:F1} x {7:F1} mm{8}\nwarnings: {9}",
                    wires.Count, nodes.Count, len, wire, vol / 1000 * 10.36,
                    bb.Diagonal.X + wire, bb.Diagonal.Y + wire, bb.Diagonal.Z + wire,
                    wire < 0.8 ? "\nWIRE BELOW 0.8 mm: Shapeways silver will reject it" : "",
                    warn.Count == 0 ? "none" : string.Join(", ", warn.GroupBy(x => x.Kind).Select(g => g.Count() + " " + g.Key)));
                if (bb.Diagonal.X > 89 || bb.Diagonal.Y > 89 || bb.Diagonal.Z > 100) cInfo += "\nlarger than Shapeways silver's 89 x 89 x 100 mm";
                if (!h.Settled) cInfo += "\nthe foam is still relaxing";
                liveKey = key;
            }

            Mesh solidMesh = null; string solidInfo = solid ? "" : "";
            if (solid)
            {
                if (!h.Settled) solidInfo = "\nSolid: waiting for the foam to settle";
                else
                {
                    string sk = key + "|" + voxel;
                    if (sk != solidKey)
                    {
                        prepare();
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var r = Struts.Solid(wires, nodes, wire, nodeScale, voxel, cyl);
                            var m = new Mesh();
                            foreach (var v in r.V) m.Vertices.Add(v.X, v.Y, v.Z);
                            foreach (var q in r.Quads) m.Faces.AddFace(q[0], q[1], q[2], q[3]);
                            m.Normals.ComputeNormals();
                            m.Compact();
                            cSolid = m;
                            cSolidInfo = string.Format(CultureInfo.InvariantCulture, "\nSolid: {0:N0} faces, {1:F1} mm3 = {2:F2} g sterling ({3} ms){4}",
                                m.Faces.Count, r.Volume, r.Volume / 1000 * 10.36, sw.ElapsedMilliseconds,
                                m.IsClosed ? ", closed" : ", NOT closed: check before upload");
                        }
                        catch (Exception ex) { cSolid = null; cSolidInfo = "\nSolid: " + ex.Message; }
                        solidKey = sk;
                    }
                    solidMesh = cSolid; solidInfo = cSolidInfo;
                }
            }

            DA.SetDataList(0, cWires); DA.SetData(1, cPipes); DA.SetData(2, solidMesh);
            DA.SetDataList(3, cWarn); DA.SetDataList(4, cKinds); DA.SetData(5, cInfo + solidInfo);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BubbleClusters
{
    // Starting arrangement as a crystal lattice (or a graded random fill), for the Bubble Solver's Centers / Radii.
    // The solver relaxes it into the matching foam: BCC -> Kelvin cells, WP -> Weaire-Phelan, etc.
    public class LatticeComponent : GH_Component
    {
        public LatticeComponent()
            : base("Bubble Lattice", "Lattice",
                   "Starting bubbles on a crystal lattice (or a graded random fill) for the Bubble Solver. Wire Centers and Radii into the solver (Pack off for a crystal).",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("5e2a7c91-3f4b-4d6e-9a8c-1b2d3e4f5a61"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.secondary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("Kind", "Kind",
                "BCC (Kelvin: 14-face truncated octahedra), WP (Weaire-Phelan: 12 + 14 faces, the best known foam), FCC (rhombic dodecahedra; unstable, needs Jitter to rearrange), " +
                "HCP, SC (cubes; unstable, rearranges), Hex (one honeycomb layer: a raft on a host plane), Random (blue-noise fill, sizes from Field).",
                GH_ParamAccess.item, "BCC");
            p.AddNumberParameter("Spacing", "Spacing", "Distance between neighbouring centres (Random: about the typical bubble diameter).", GH_ParamAccess.item, 2);
            p.AddPlaneParameter("Plane", "Plane", "Lattice origin and orientation (e.g. tilt it to grow the crystal along another axis).", GH_ParamAccess.item, Plane.WorldXY);
            p.AddBrepParameter("Region", "Region", "Optional closed Brep: keep the sites inside it (any shape).", GH_ParamAccess.item);
            p.AddNumberParameter("Radius", "Radius", "Without a Region: a ball of this radius around the plane origin (0: a box of Count spacings per side).", GH_ParamAccess.item, 5);
            p.AddIntegerParameter("Count", "Count", "Box size in spacings per side (when Radius is 0 and there is no Region).", GH_ParamAccess.item, 3);
            p.AddNumberParameter("Jitter", "Jitter", "Random offset, as a fraction of Spacing. A little (0.03) lets unstable crystals (FCC, SC) rearrange like a real foam.", GH_ParamAccess.item, 0);
            p.AddIntegerParameter("Seed", "Seed", "Random seed (Jitter, Random).", GH_ParamAccess.item, 1);
            p.AddGenericParameter("Field", "Field", "Optional Size Field: scales each radius by the field at its centre (Random: sets the local bubble size).", GH_ParamAccess.item);
            for (int i = 3; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPointParameter("Centers", "Centers", "Bubble centres, for the Bubble Solver.", GH_ParamAccess.list);
            p.AddNumberParameter("Radii", "Radii", "Bubble radii, for the Bubble Solver.", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "Count, sizes, expected cells, rough mesh size.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string kindText = "BCC"; double spacing = 2, radius = 5, jitter = 0; int count = 3, seed = 1;
            Plane pl = Plane.WorldXY; Brep region = null; object fo = null;
            DA.GetData(0, ref kindText); DA.GetData(1, ref spacing); DA.GetData(2, ref pl); DA.GetData(3, ref region);
            DA.GetData(4, ref radius); DA.GetData(5, ref count); DA.GetData(6, ref jitter); DA.GetData(7, ref seed); DA.GetData(8, ref fo);
            var fw = fo as GH_ObjectWrapper;
            var field = (fw != null ? fw.Value : fo) as SizeField;
            if (spacing <= 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Spacing must be > 0."); return; }

            string k = (kindText ?? "BCC").Trim().ToUpperInvariant();
            bool random = k == "RANDOM" || k == "SCATTER";
            LatticeKind kind = LatticeKind.BCC;
            if (!random)
            {
                if (k == "KELVIN") k = "BCC";
                if (k == "WEAIRE-PHELAN" || k == "WEAIREPHELAN" || k == "A15") k = "WP";
                if (k == "HONEYCOMB" || k == "HEX2D" || k == "LAYER") k = "HEX";
                if (!Enum.TryParse(k, true, out kind)) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Unknown Kind '" + kindText + "'."); return; }
            }

            Func<Vec3, bool> inside; Vec3 bmin, bmax;
            Vec3 o = new Vec3(pl.Origin.X, pl.Origin.Y, pl.Origin.Z);
            if (region != null)
            {
                if (!region.IsSolid) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Region is not a closed Brep; inside tests may be wrong.");
                var bb = region.GetBoundingBox(true);
                double tol = Rhino.RhinoDoc.ActiveDoc != null ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.001;
                var rg = region;
                inside = q => rg.IsPointInside(new Point3d(q.X, q.Y, q.Z), tol, false);
                bmin = new Vec3(bb.Min.X, bb.Min.Y, bb.Min.Z); bmax = new Vec3(bb.Max.X, bb.Max.Y, bb.Max.Z);
            }
            else if (radius > 0)
            {
                double R = radius;
                inside = q => (q - o).LengthSq <= R * R;
                bmin = o - new Vec3(R, R, R); bmax = o + new Vec3(R, R, R);
            }
            else
            {
                inside = null; bmin = bmax = o;
            }

            List<Vec3> c; List<double> r;
            string expect;
            if (random)
            {
                if (inside == null)
                {
                    double L = Math.Max(1, count) * spacing;
                    Vec3 X = V(pl.XAxis), Y = V(pl.YAxis), Z = V(pl.ZAxis);
                    inside = q => { var d = q - o; double u = Vec3.Dot(d, X), v = Vec3.Dot(d, Y), w = Vec3.Dot(d, Z); return u >= 0 && v >= 0 && w >= 0 && u <= L && v <= L && w <= L; };
                    var cs = new[] { o, o + X * L, o + Y * L, o + Z * L, o + (X + Y) * L, o + (X + Z) * L, o + (Y + Z) * L, o + (X + Y + Z) * L };
                    bmin = new Vec3(cs.Min(q => q.X), cs.Min(q => q.Y), cs.Min(q => q.Z)); bmax = new Vec3(cs.Max(q => q.X), cs.Max(q => q.Y), cs.Max(q => q.Z));
                }
                Seeding.Scatter(inside, bmin, bmax, spacing / 2, field, seed, 5000, out c, out r);
                // A blue-noise fill leaves gaps (outside air) between bubbles: pull it together into a cluster.
                ClusterPacker.Pack(c, r, 1000, 0.01);
                expect = "irregular foam (Random fill, packed)" + (field != null ? ", graded by the Field" : "");
            }
            else
            {
                var set = new LatticeSettings
                {
                    Kind = kind, Spacing = spacing, Origin = o, X = V(pl.XAxis), Y = V(pl.YAxis), Z = V(pl.ZAxis),
                    Jitter = Math.Max(0, jitter), Seed = seed, Count = Math.Max(1, count),
                };
                if (region != null) { set.Inside = inside; set.BoundsMin = bmin; set.BoundsMax = bmax; }
                else if (radius > 0) set.BallRadius = radius;
                Seeding.Lattice(set, out c, out r);
                if (field != null)
                {
                    for (int i = 0; i < c.Count; i++) r[i] *= field.Eval(c[i]);
                    // Mixed sizes on a fixed lattice leave gaps / big overlaps: let them settle into a packing.
                    ClusterPacker.Pack(c, r, 1000, 0.01);
                }
                expect = Expect(kind, jitter) + (field != null ? "; radii scaled by the Field, so the crystal is graded / distorted" : "");
            }

            if (c.Count == 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No sites inside the region: make it bigger or the Spacing smaller."); }
            DA.SetDataList(0, c.Select(q => new Point3d(q.X, q.Y, q.Z)));
            DA.SetDataList(1, r);
            if (c.Count > 0)
            {
                // Rough mesh size at the solver's default CellSize (smallest radius / 6); films are shared, ~0.6.
                double h = r.Min() / 6, tris = 0.6 * r.Sum(x => 4 * Math.PI * x * x) / (0.433 * h * h);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} bubbles, radius {1:G3}..{2:G3}", c.Count, r.Min(), r.Max()));
                sb.AppendLine("Expect: " + expect);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Mesh at the default CellSize ({0:G3}): ~{1:N0} triangles{2}", h, tris,
                    tris > 100000 ? " - over the solver's 100k limit: set the solver's CellSize to about " +
                        (h * Math.Sqrt(tris / 80000)).ToString("G3", CultureInfo.InvariantCulture) + ", or use fewer / bigger bubbles" : ""));
                sb.Append(random || field != null ? "Solver: Pack can stay off (already packed)." : "Solver: keep Pack off (it would distort the crystal).");
                DA.SetData(2, sb.ToString());
                if (tris > 100000) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Large: raise the solver's CellSize (see Info).");
            }
        }

        static Vec3 V(Vector3d v) { return new Vec3(v.X, v.Y, v.Z); }

        static string Expect(LatticeKind k, double jitter)
        {
            switch (k)
            {
                case LatticeKind.BCC: return "Kelvin cells inside (14 faces: 6 squares + 8 hexagons)";
                case LatticeKind.WP: return "Weaire-Phelan inside (12-face dodecahedra and 14-face cells, 1 : 3)";
                case LatticeKind.FCC: return jitter > 0 ? "starts as rhombic dodecahedra, then rearranges (unstable 4-fold vertices) into a 12-15-face mix"
                                                        : "rhombic dodecahedra (12 faces), held by perfect symmetry; add Jitter ~0.03 to let it rearrange like a real foam";
                case LatticeKind.HCP: return "trapezo-rhombic dodecahedra, which rearrange (unstable) into a 12-15-face mix";
                case LatticeKind.SC: return "cubes, which rearrange (4 films on every edge is unstable) into a disordered foam";
                default: return "one honeycomb layer (hexagonal prisms); put it on a host plane for a raft";
            }
        }
    }
}

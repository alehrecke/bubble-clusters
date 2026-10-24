using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace BubbleClusters
{
    // Ring Seed (core/RingSeed.cs): bubbles for a ring, a single file around the underside of the finger swelling into a
    // small foam on top. Wire Centers/Radii into the Bubble Solver and HostRing into its HostRing input, so the bubbles
    // rest on the finger and the bore stays round. Change Seed for variations.
    public class RingSeedComponent : GH_Component
    {
        public RingSeedComponent()
            : base("Ring Seed", "RingSeed",
                   "Seed bubbles for a ring: a band around the underside of a finger that collects into a foam on top. Feed Centers, Radii and HostRing to the Bubble Solver.",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("8a4e2f61-93c7-4d5b-b0e8-1f6a7c2d9e34"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.secondary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPlaneParameter("Plane", "Plane", "Ring frame: origin = centre of the bore, Z = along the finger, Y = toward the top of the ring.", GH_ParamAccess.item, Plane.WorldXY);
            p.AddNumberParameter("Size", "Size", "US ring size (7 = 17.32 mm bore). Units: mm.", GH_ParamAccess.item, 7);
            p.AddNumberParameter("BandRadius", "BandRadius", "Bubble radius around the underside (mm).", GH_ParamAccess.item, 0.9);
            p.AddNumberParameter("TopRadius", "TopRadius", "Largest bubble on top (mm).", GH_ParamAccess.item, 2.2);
            p.AddNumberParameter("Spread", "Spread", "How far the swelling reaches round from the top, degrees either side.", GH_ParamAccess.item, 55);
            p.AddIntegerParameter("TopCount", "TopCount", "Extra bubbles collected on top.", GH_ParamAccess.item, 10);
            p.AddIntegerParameter("Seed", "Seed", "Variation.", GH_ParamAccess.item, 1);
            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPointParameter("Centers", "Centers", "Bubble centres, for the Bubble Solver.", GH_ParamAccess.list);
            p.AddNumberParameter("Radii", "Radii", "Bubble radii, for the Bubble Solver.", GH_ParamAccess.list);
            p.AddCircleParameter("HostRing", "HostRing", "The bore circle, for the Bubble Solver's HostRing (the finger).", GH_ParamAccess.item);
            p.AddBrepParameter("Finger", "Finger", "Preview of the finger (a short cylinder at the bore).", GH_ParamAccess.item);
            p.AddTextParameter("Info", "Info", "Counts and sizes.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Plane pl = Plane.WorldXY; double size = 7, band = 0.9, top = 2.2, spread = 55; int topCount = 10, seed = 1;
            DA.GetData(0, ref pl); DA.GetData(1, ref size); DA.GetData(2, ref band); DA.GetData(3, ref top);
            DA.GetData(4, ref spread); DA.GetData(5, ref topCount); DA.GetData(6, ref seed);
            if (!(band > 0) || !(top > 0)) { DA.SetData(4, "BandRadius and TopRadius must be positive."); return; }

            var s = new RingSeedSettings
            {
                Size = size, BandRadius = band, TopRadius = Math.Max(band, top), Spread = spread, TopCount = Math.Max(0, topCount), Seed = seed,
                Origin = new Vec3(pl.Origin.X, pl.Origin.Y, pl.Origin.Z),
                Axis = new Vec3(pl.ZAxis.X, pl.ZAxis.Y, pl.ZAxis.Z),
                Up = new Vec3(pl.YAxis.X, pl.YAxis.Y, pl.YAxis.Z),
            };
            List<Vec3> c; List<double> r;
            RingSeed.Build(s, out c, out r);
            double bore = RingSeed.BoreDiameter(size);
            var circle = new Circle(pl, bore / 2);
            double len = 4 * Math.Max(band, top);
            var finger = new Cylinder(new Circle(new Plane(pl.Origin - pl.ZAxis * len / 2, pl.XAxis, pl.YAxis), bore / 2), len).ToBrep(true, true);

            DA.SetDataList(0, c.Select(v => new Point3d(v.X, v.Y, v.Z)));
            DA.SetDataList(1, r);
            DA.SetData(2, circle);
            DA.SetData(3, finger);
            int nBand = c.Count - s.TopCount;
            DA.SetData(4, string.Format(CultureInfo.InvariantCulture,
                "US size {0}: bore {1:F2} mm\n{2} band bubbles + {3} on top, r {4:F2}..{5:F2} mm\nsuggested CellSize ~0.2 mm",
                size, bore, nBand, s.TopCount, r.Min(), r.Max()));
        }
    }
}

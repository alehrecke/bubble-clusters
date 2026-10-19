using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace BubbleClusters
{
    // A bubble-size multiplier over space: attractor points / curves, an axis gradient, and noise (they multiply).
    // Feed Field into Bubble Lattice, or give it Centers + Radii to scale any radii directly.
    public class SizeFieldComponent : GH_Component
    {
        public SizeFieldComponent()
            : base("Size Field", "SizeField",
                   "Bubble size over space: small near attractors, a gradient along an axis, noise. Feed Field into Bubble Lattice, or scale Radii directly.",
                   "Pneuhaus", "Bubbles")
        { }

        public override Guid ComponentGuid { get { return new Guid("8b3c1d4e-6f7a-4b9c-8d2e-3f4a5b6c7d82"); } }
        public override GH_Exposure Exposure { get { return GH_Exposure.secondary; } }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter("Points", "Points", "Attractor points.", GH_ParamAccess.list);
            p.AddCurveParameter("Curves", "Curves", "Attractor curves.", GH_ParamAccess.list);
            p.AddNumberParameter("Near", "Near", "Size multiplier at an attractor.", GH_ParamAccess.item, 0.4);
            p.AddNumberParameter("Far", "Far", "Size multiplier at Falloff distance and beyond.", GH_ParamAccess.item, 1);
            p.AddNumberParameter("Falloff", "Falloff", "Distance over which Near blends to Far (smooth).", GH_ParamAccess.item, 5);
            p.AddLineParameter("Axis", "Axis", "Optional gradient axis: AxisStart at its start, AxisEnd at its end.", GH_ParamAccess.item);
            p.AddNumberParameter("AxisStart", "AxisStart", "Size multiplier at the axis start.", GH_ParamAccess.item, 1);
            p.AddNumberParameter("AxisEnd", "AxisEnd", "Size multiplier at the axis end.", GH_ParamAccess.item, 1);
            p.AddNumberParameter("Noise", "Noise", "Noise strength (0 off; 0.3: sizes vary roughly x0.75..x1.35).", GH_ParamAccess.item, 0);
            p.AddNumberParameter("NoiseScale", "NoiseScale", "Noise wavelength (world units).", GH_ParamAccess.item, 5);
            p.AddIntegerParameter("Seed", "Seed", "Noise seed.", GH_ParamAccess.item, 1);
            p.AddNumberParameter("Min", "Min", "Smallest multiplier.", GH_ParamAccess.item, 0.15);
            p.AddNumberParameter("Max", "Max", "Largest multiplier.", GH_ParamAccess.item, 4);
            p.AddPointParameter("Centers", "Centers", "Optional: centres to scale radii at.", GH_ParamAccess.list);
            p.AddNumberParameter("Radii", "Radii", "Optional: radii to scale (one, or one per centre).", GH_ParamAccess.list);
            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("Field", "Field", "The size field, for Bubble Lattice.", GH_ParamAccess.item);
            p.AddNumberParameter("Radii", "Radii", "Radii x the field at each centre (if Centers and Radii are given).", GH_ParamAccess.list);
            p.AddNumberParameter("Scale", "Scale", "The field's multiplier at each centre.", GH_ParamAccess.list);
            p.AddTextParameter("Info", "Info", "What's active, and the range of multipliers at the centres.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var pts = new List<Point3d>(); var crvs = new List<Curve>(); var cen = new List<Point3d>(); var rad = new List<double>();
            double near = 0.4, far = 1, falloff = 5, a0 = 1, a1 = 1, noise = 0, nscale = 5, mn = 0.15, mx = 4; int seed = 1;
            Line axis = Line.Unset;
            DA.GetDataList(0, pts); DA.GetDataList(1, crvs); DA.GetData(2, ref near); DA.GetData(3, ref far); DA.GetData(4, ref falloff);
            bool hasAxis = DA.GetData(5, ref axis) && axis.IsValid && axis.Length > 0;
            DA.GetData(6, ref a0); DA.GetData(7, ref a1); DA.GetData(8, ref noise); DA.GetData(9, ref nscale); DA.GetData(10, ref seed);
            DA.GetData(11, ref mn); DA.GetData(12, ref mx); DA.GetDataList(13, cen); DA.GetDataList(14, rad);

            var f = new SizeField
            {
                Near = near, Far = far, Falloff = Math.Max(1e-6, falloff), Noise = noise, NoiseScale = Math.Max(1e-6, nscale), Seed = seed,
                Min = Math.Max(1e-3, Math.Min(mn, mx)), Max = Math.Max(mn, mx),
            };
            foreach (var q in pts) f.Points.Add(new Vec3(q.X, q.Y, q.Z));
            foreach (var cv in crvs)
            {
                if (cv == null) continue;
                double[] ts = cv.DivideByCount(Math.Max(16, (int)Math.Min(400, cv.GetLength() / Math.Max(1e-6, falloff) * 20)), true);
                if (ts == null) continue;
                var pl = ts.Select(t => { var q = cv.PointAt(t); return new Vec3(q.X, q.Y, q.Z); }).ToList();
                if (cv.IsClosed && pl.Count > 0) pl.Add(pl[0]);
                f.Polylines.Add(pl);
            }
            if (hasAxis) { f.HasAxis = true; f.AxisFrom = new Vec3(axis.FromX, axis.FromY, axis.FromZ); f.AxisTo = new Vec3(axis.ToX, axis.ToY, axis.ToZ); f.AxisStart = a0; f.AxisEnd = a1; }
            DA.SetData(0, new GH_ObjectWrapper(f));

            var parts = new List<string>();
            if (f.HasAttractors) parts.Add(string.Format(CultureInfo.InvariantCulture, "attractors: {0} point(s), {1} curve(s), x{2:G3} near -> x{3:G3} beyond {4:G3}", pts.Count, crvs.Count, near, far, falloff));
            if (hasAxis) parts.Add(string.Format(CultureInfo.InvariantCulture, "axis: x{0:G3} -> x{1:G3}", a0, a1));
            if (noise != 0) parts.Add(string.Format(CultureInfo.InvariantCulture, "noise {0:G3} at wavelength {1:G3}", noise, nscale));
            if (parts.Count == 0) parts.Add("nothing active (multiplier 1 everywhere)");
            if (cen.Count > 0)
            {
                var sc = cen.Select(q => f.Eval(new Vec3(q.X, q.Y, q.Z))).ToList();
                DA.SetDataList(2, sc);
                if (rad.Count > 0) DA.SetDataList(1, sc.Select((s, i) => s * rad[Math.Min(i, rad.Count - 1)]));
                parts.Add(string.Format(CultureInfo.InvariantCulture, "at the {0} centres: x{1:G3} .. x{2:G3}", cen.Count, sc.Min(), sc.Max()));
            }
            DA.SetData(3, string.Join("\n", parts));
        }
    }
}

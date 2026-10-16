// FoamSeeder Phase 2 — Poisson-disk bubble placement
//
// INPUTS  (auto-configured from RunScript signature on compile):
//   BBox  : Box    — seeding volume
//   Count : int    — target bubble count
//   RMin  : double — minimum bubble radius
//   RMax  : double — maximum bubble radius
//   Seed  : int    — random seed (integer slider, 1–999)
//
// OUTPUTS (auto-configured):
//   Centers : Point3d list — bubble centers  →  wire to BubbleCluster.Centers
//   Radii   : double  list — bubble radii    →  wire to BubbleCluster.Radii
//
// KEY: minimum separation uses the Glassner equilibrium distance
//   dAB = sqrt(rA² + rB² - rA·rB)
// This is ~0.5–0.7 × (rA+rB), placing bubbles at real foam packing density
// so BubbleCluster finds adjacencies correctly.

#region Usings
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

public class Script_Instance : GH_ScriptInstance
{
    #region Notes
    /*
      Members:
        RhinoDoc RhinoDocument
        GH_Document GrasshopperDocument
        IGH_Component Component
        int Iteration
    */
    #endregion

    private void RunScript(Box BBox, int Count, double RMin, double RMax, int Seed, ref object Centers, ref object Radii)
    {
        if (!BBox.IsValid || Count < 2 || RMin <= 0 || RMax < RMin)
        {
            Print("Check inputs: valid Box, Count>=2, 0 < RMin <= RMax.");
            return;
        }

        int cap      = Math.Max(Count, 2);
        var rng      = new Random(Seed < 1 ? 42 : Seed);
        int maxTries = cap * 200;   // higher budget because foam packing is dense

        double muLog    = Math.Log((RMin + RMax) * 0.5);
        double sigmaLog = (Math.Log(RMax) - Math.Log(RMin)) * 0.35;

        var ptList = new List<Point3d>();
        var rList  = new List<double>();

        BoundingBox bb   = BBox.BoundingBox;
        Point3d     bMin = bb.Min;
        Point3d     bMax = bb.Max;

        for (int tries = 0; ptList.Count < cap && tries < maxTries; tries++)
        {
            // Log-normal radius — few large bubbles, many small (matches real foam)
            double u1 = Math.Max(rng.NextDouble(), 1e-12);
            double u2 = rng.NextDouble();
            double rv = Math.Exp(muLog + sigmaLog * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            rv = Math.Max(RMin, Math.Min(RMax, rv));

            var cand = new Point3d(
                bMin.X + rng.NextDouble() * (bMax.X - bMin.X),
                bMin.Y + rng.NextDouble() * (bMax.Y - bMin.Y),
                bMin.Z + rng.NextDouble() * (bMax.Z - bMin.Z));

            // Clearance from box walls
            if (cand.X - rv < bMin.X || cand.X + rv > bMax.X) continue;
            if (cand.Y - rv < bMin.Y || cand.Y + rv > bMax.Y) continue;
            if (cand.Z - rv < bMin.Z || cand.Z + rv > bMax.Z) continue;

            // Poisson-disk using equilibrium foam distance as minimum separation.
            // dAB = sqrt(rA² + rB² - rA·rB) is the center distance at which two
            // bubbles form a proper 120° junction (Glassner 2000).
            // Using 0.98 * dAB so placed pairs are just inside the adjacency threshold.
            bool ok = true;
            for (int k = 0; k < ptList.Count && ok; k++)
            {
                double dEquil = Math.Sqrt(rv * rv + rList[k] * rList[k] - rv * rList[k]);
                if (cand.DistanceTo(ptList[k]) < dEquil * 0.98) ok = false;
            }
            if (!ok) continue;

            ptList.Add(cand);
            rList.Add(rv);
        }

        if (ptList.Count == 0)
        {
            Print("No bubbles placed. Try: smaller box, fewer bubbles, or smaller RMax.");
            return;
        }

        Print($"Placed {ptList.Count}/{cap} bubbles. r {rList.Min():F2}–{rList.Max():F2}");
        Centers = ptList;
        Radii   = rList;
    }
}

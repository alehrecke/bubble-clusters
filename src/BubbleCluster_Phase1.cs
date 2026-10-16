// BubbleCluster Phase 1 — Bubble Adjacency + Neck Circles
//
// INPUTS:   Centers (Point3d list), Radii (double list)
// OUTPUTS:  Borders (Circle list), Nodes (Point3d list), Walls (Sphere list)
//
// WHAT THE OUTPUTS ARE:
//   Borders — neck circles: the actual sphere-sphere intersection circle for
//             each adjacent pair. This is the ring where two bubble surfaces
//             physically meet. Pipe these for the jewelry wire network.
//   Nodes   — triple junction points: where three neck circles converge on a
//             shared sphere surface. These are the actual Plateau border locations.
//   Walls   — interface spheres (Glassner rC formula): the curved wall surface
//             between each adjacent pair, for visual reference only.
//
// GEOMETRY NOTE:
//   Glassner's "centers C, E, F are collinear" fact only holds when bubble
//   positions are derived from radii (A at origin, B on X-axis, D by triangulation
//   — all co-planar). For freely-placed 3D seeds, neck circles are the correct
//   geometric output. The full Glassner analytic cluster requires either the
//   analytic placement or Kangaroo constraint relaxation (Phase 4).

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

    private void RunScript(List<Point3d> Centers, List<double> Radii, ref object Borders, ref object Nodes, ref object Walls)
    {
        if (Centers == null || Radii == null || Centers.Count < 2 || Centers.Count != Radii.Count)
        {
            Print("Need >= 2 Centers with matching Radii.");
            return;
        }

        int n = Centers.Count;

        // ── Step 1: adjacency, neck circles, interface spheres ───────────────
        var adjPairs  = new List<int[]>();
        var necks     = new List<Circle>();   // sphere-sphere intersection circles
        var walls     = new List<Sphere>();   // Glassner interface spheres (visual)
        int sFar = 0, sIn = 0, sDom = 0;

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double ri = Radii[i], rj = Radii[j];
                double d  = Centers[i].DistanceTo(Centers[j]);

                if (d >= ri + rj - 1e-6)           { sFar++; continue; }
                if (d <= Math.Abs(ri - rj) + 1e-6) { sIn++;  continue; }

                // Power-diagram: junction point must not be dominated by bubble k
                double   t     = (d * d + ri * ri - rj * rj) / (2.0 * d);
                Vector3d dir   = (Centers[j] - Centers[i]) / d;
                Point3d  jPt   = Centers[i] + dir * t;
                double   powIJ = t * t - ri * ri;

                bool dominated = false;
                for (int k = 0; k < n && !dominated; k++)
                {
                    if (k == i || k == j) continue;
                    double dk = Centers[k].DistanceTo(jPt);
                    if (dk * dk - Radii[k] * Radii[k] < powIJ - 1e-6) dominated = true;
                }
                if (dominated) { sDom++; continue; }

                adjPairs.Add(new int[] { i, j });

                // Neck circle: actual intersection of sphere i and sphere j
                double r2 = ri * ri - t * t;
                if (r2 > 0)
                    necks.Add(new Circle(new Plane(Centers[i] + dir * t, dir), Math.Sqrt(r2)));
                else
                    necks.Add(new Circle());  // shouldn't happen given overlap checks above

                // Interface sphere (Glassner) — visual only, not used for borders
                walls.Add(InterfaceSphere(Centers[i], ri, Centers[j], rj, d));
            }
        }

        int np = adjPairs.Count;
        Print($"Adjacent pairs: {np}  ({sFar} far, {sIn} contained, {sDom} dominated)");

        // ── Step 2: triple junction points ──────────────────────────────────
        // At triple junction (i, j, k): the point where three neck circles meet
        // on a shared sphere surface. Found by intersecting the two radical planes
        // (of pairs i-j and i-k) with sphere i.
        var pairIdx = new Dictionary<long, int>(np);
        for (int p = 0; p < np; p++)
            pairIdx[(long)adjPairs[p][0] * n + adjPairs[p][1]] = p;

        var nodes = new List<Point3d>();

        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            if (!pairIdx.ContainsKey((long)i * n + j)) continue;
            for (int k = j + 1; k < n; k++)
            {
                if (!pairIdx.ContainsKey((long)i * n + k)) continue;
                if (!pairIdx.ContainsKey((long)j * n + k)) continue;

                // Intersection of radical plane of (i,j) and radical plane of (i,k)
                // with sphere i gives the triple junction point(s)
                Point3d[] pts = TripleJunctionPoints(
                    Centers[i], Radii[i],
                    Centers[j], Radii[j],
                    Centers[k], Radii[k]);

                foreach (Point3d p in pts)
                    nodes.Add(p);
            }
        }

        Print($"Neck circles: {necks.Count(c => c.Radius > 0)}   Triple junction points: {nodes.Count}");

        Borders = necks.Where(c => c.Radius > 1e-6).ToList();
        Nodes   = nodes;
        Walls   = walls;
    }

    // ── Interface sphere (Glassner rC formula) ───────────────────────────────
    // The curved wall between two bubbles. Center lies past the smaller bubble
    // along the AB axis. Visual reference only for this component.
    private Sphere InterfaceSphere(Point3d cA, double rA, Point3d cB, double rB, double d)
    {
        if (rA < rB) { var tc = cA; cA = cB; cB = tc; var tr = rA; rA = rB; rB = tr; }
        if (rA - rB < 1e-4)
        {
            return new Sphere(new Point3d((cA.X+cB.X)*0.5,(cA.Y+cB.Y)*0.5,(cA.Z+cB.Z)*0.5), 1e6);
        }
        double rC = rA * rB / (rA - rB);
        double t  = (d * d + rA * rA - rB * rB) / (2.0 * d);
        double xC = rA * (2.0 * rA + rC) / (2.0 * t);
        return new Sphere(cA + (cB - cA) / d * xC, rC);
    }

    // ── Triple junction points ────────────────────────────────────────────────
    // Find points on sphere A (cA, rA) that lie simultaneously in:
    //   - the radical plane of A and B  (plane of neck circle AB)
    //   - the radical plane of A and C  (plane of neck circle AC)
    // These are the actual Plateau border locations at this three-bubble junction.
    //
    // Method: the two radical planes intersect in a line L;
    //         sphere A ∩ line L = 0, 1, or 2 points.
    private Point3d[] TripleJunctionPoints(
        Point3d cA, double rA,
        Point3d cB, double rB,
        Point3d cC, double rC)
    {
        double dAB = cA.DistanceTo(cB);
        double dAC = cA.DistanceTo(cC);
        if (dAB < 1e-8 || dAC < 1e-8) return new Point3d[0];

        // Unit normals of the two radical planes (= unit vectors along AB, AC)
        Vector3d nAB = (cB - cA) / dAB;
        Vector3d nAC = (cC - cA) / dAC;

        // Signed distances from cA to each radical plane
        double tAB = (dAB * dAB + rA * rA - rB * rB) / (2.0 * dAB);
        double tAC = (dAC * dAC + rA * rA - rC * rC) / (2.0 * dAC);

        // Points on each plane (along each axis from cA)
        Point3d pAB = cA + nAB * tAB;
        Point3d pAC = cA + nAC * tAC;

        // Direction of line L = nAB × nAC
        Vector3d Ldir = Vector3d.CrossProduct(nAB, nAC);
        double Llen = Ldir.Length;
        if (Llen < 1e-8) return new Point3d[0];  // planes parallel (bubbles collinear)
        Ldir /= Llen;

        // Find a point on L by solving the 2-plane linear system
        double cosA   = nAB * nAC;
        double sinAsq = 1.0 - cosA * cosA;
        if (sinAsq < 1e-12) return new Point3d[0];

        double d1 = nAB * (Vector3d)pAB;
        double d2 = nAC * (Vector3d)pAC;
        double cv = (d1 - cosA * d2) / sinAsq;
        double dv = (d2 - cosA * d1) / sinAsq;

        Point3d Lpt = new Point3d(cv * nAB.X + dv * nAC.X,
                                   cv * nAB.Y + dv * nAC.Y,
                                   cv * nAB.Z + dv * nAC.Z);

        // Intersect line L with sphere A
        Vector3d toA  = cA - Lpt;
        double   proj = Ldir * toA;
        double   disc = rA * rA - (toA.SquareLength - proj * proj);

        if (disc < 0)          return new Point3d[0];
        if (disc < 1e-10)      return new Point3d[] { Lpt + Ldir * proj };

        double sq = Math.Sqrt(disc);
        return new Point3d[]
        {
            Lpt + Ldir * (proj + sq),
            Lpt + Ldir * (proj - sq)
        };
    }
}

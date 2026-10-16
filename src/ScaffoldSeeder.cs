// ScaffoldSeeder — places bubble seeds on a mesh scaffold surface
//
// Bubbles sit tangent to the surface: each center is placed at
//   surface_point + face_normal * (radius + Offset)
// so the bubble just touches (or is lifted off by Offset) the mesh.
//
// INPUTS (add before pasting):
//   Surface : Mesh   item  — the scaffold mesh (ring top, wrist, etc.)
//   Count   : int    item  — target bubble count
//   RMin    : double item  — minimum bubble radius
//   RMax    : double item  — maximum bubble radius
//   Seed    : int    item  — random seed
//   Offset  : double item  — extra gap between bubble bottom and surface (0 = tangent)
//
// OUTPUTS (add before pasting):
//   Centers : Point3d list — bubble centers  → wire to BubblePowerDiagram.Centers
//   Radii   : double  list — bubble radii    → wire to BubblePowerDiagram.Radii
//
// COMBINING WITH BoxSeeder / FoamSeeder
//   Merge the two Centers lists and two Radii lists with GH "Merge" components,
//   then feed the combined lists into BubblePowerDiagram.
//   This gives surface-attached bubbles mixed with floating volume bubbles.

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
      Members: RhinoDoc RhinoDocument, GH_Document GrasshopperDocument,
               IGH_Component Component, int Iteration
    */
    #endregion

    private void RunScript(Mesh Surface, int Count, double RMin, double RMax,
                           int Seed, double Offset, ref object Centers, ref object Radii)
    {
        if (Surface == null || !Surface.IsValid || Count < 1 || RMin <= 0 || RMax < RMin)
        {
            Print("Need a valid mesh, Count>=1, 0<RMin<=RMax.");
            return;
        }

        Surface.FaceNormals.ComputeFaceNormals();
        Surface.Normals.ComputeNormals();

        int    cap      = Math.Max(Count, 1);
        var    rng      = new Random(Seed < 1 ? 42 : Seed);
        int    maxTries = cap * 200;

        double muLog    = Math.Log((RMin + RMax) * 0.5);
        double sigmaLog = (Math.Log(RMax) - Math.Log(RMin)) * 0.35;

        // Build a cumulative face-area distribution for area-weighted sampling
        int    nFaces   = Surface.Faces.Count;
        var    faceArea = new double[nFaces];
        double totalArea = 0;
        for (int fi = 0; fi < nFaces; fi++)
        {
            faceArea[fi] = FaceArea(Surface, fi);
            totalArea   += faceArea[fi];
        }
        var cumArea = new double[nFaces];
        cumArea[0] = faceArea[0];
        for (int fi = 1; fi < nFaces; fi++)
            cumArea[fi] = cumArea[fi - 1] + faceArea[fi];

        var ptList = new List<Point3d>();
        var rList  = new List<double>();

        for (int tries = 0; ptList.Count < cap && tries < maxTries; tries++)
        {
            // Sample radius
            double u1 = Math.Max(rng.NextDouble(), 1e-12);
            double u2 = rng.NextDouble();
            double rv = Math.Exp(muLog + sigmaLog * Math.Sqrt(-2.0 * Math.Log(u1))
                                        * Math.Cos(2.0 * Math.PI * u2));
            rv = Math.Max(RMin, Math.Min(RMax, rv));

            // Area-weighted face sample
            int fi = SampleFace(cumArea, totalArea, rng.NextDouble());
            if (fi < 0) continue;

            // Random point on face using barycentric coords
            Point3d surfPt = RandomPointOnFace(Surface, fi, rng);

            // Face normal at that point
            Vector3d fn = (Vector3d)Surface.FaceNormals[fi];
            fn.Unitize();

            // Bubble center: offset outward from surface
            Point3d cand = surfPt + fn * (rv + Offset);

            // Poisson-disk rejection using equilibrium foam spacing
            // dAB = sqrt(ri² + rj² - ri*rj) ensures adjacency in power diagram
            bool ok = true;
            for (int k = 0; k < ptList.Count && ok; k++)
            {
                double ri = rList[k];
                double dEquil = Math.Sqrt(rv*rv + ri*ri - rv*ri);
                if (cand.DistanceTo(ptList[k]) < dEquil * 0.98) ok = false;
            }
            if (!ok) continue;

            ptList.Add(cand);
            rList.Add(rv);
        }

        Print($"Scaffold: placed {ptList.Count}/{cap} bubbles on {nFaces}-face mesh.");
        Centers = ptList;
        Radii   = rList;
    }

    // Area-weighted face index using binary search on cumulative areas
    private int SampleFace(double[] cumArea, double total, double rand)
    {
        double target = rand * total;
        int lo = 0, hi = cumArea.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (cumArea[mid] < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // Random point uniformly distributed on a triangular or quad face
    private Point3d RandomPointOnFace(Mesh m, int fi, Random rng)
    {
        MeshFace face = m.Faces[fi];
        Point3f  p0   = m.Vertices[face.A];
        Point3f  p1   = m.Vertices[face.B];
        Point3f  p2   = m.Vertices[face.C];

        double a = rng.NextDouble(), b = rng.NextDouble();
        if (a + b > 1.0) { a = 1.0 - a; b = 1.0 - b; }
        double c = 1.0 - a - b;

        return new Point3d(
            a*p0.X + b*p1.X + c*p2.X,
            a*p0.Y + b*p1.Y + c*p2.Y,
            a*p0.Z + b*p1.Z + c*p2.Z);
    }

    // Triangle face area (use first triangle of quad)
    private double FaceArea(Mesh m, int fi)
    {
        MeshFace face = m.Faces[fi];
        Point3f  p0   = m.Vertices[face.A];
        Point3f  p1   = m.Vertices[face.B];
        Point3f  p2   = m.Vertices[face.C];
        var ab = new Vector3d(p1.X-p0.X, p1.Y-p0.Y, p1.Z-p0.Z);
        var ac = new Vector3d(p2.X-p0.X, p2.Y-p0.Y, p2.Z-p0.Z);
        return Vector3d.CrossProduct(ab, ac).Length * 0.5;
    }
}

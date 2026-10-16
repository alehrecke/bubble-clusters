// LayerSeeder — adds secondary bubble layers in the outer concavities of an existing foam
//
// Each convex-hull triangle of the existing foam's Delaunay triangulation defines
// a concave "cradle" between three outer bubbles. A secondary bubble nestles into
// this cradle, tangent to all three.  Repeat to add further layers.
//
// INPUTS (add before pasting):
//   Centers : Point3d list  — existing bubble centers (from any seeder)
//   Radii   : double  list  — existing bubble radii
//   Layers  : int     item  — how many additional layers to grow (1 = one shell)
//   Scale   : double  item  — secondary bubble radius as fraction of cradle average (0.5–0.9)
//   Seed    : int     item  — random seed
//
// OUTPUTS (add before pasting):
//   Centers : Point3d list  — ALL bubble centers (original + secondary layers)
//   Radii   : double  list  — ALL bubble radii
//   NewOnly : Point3d list  — only the newly added centers (for visualising growth)

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

    private void RunScript(List<Point3d> Centers, List<double> Radii,
                           int Layers, double Scale, int Seed,
                           ref object AllCenters, ref object AllRadii, ref object NewOnly)
    {
        if (Centers == null || Radii == null || Centers.Count < 4 || Centers.Count != Radii.Count)
        {
            Print("Need >= 4 Centers with matching Radii.");
            return;
        }

        double scale  = (Scale > 0.1 && Scale < 1.5) ? Scale : 0.7;
        int    layers = Math.Max(1, Layers);
        var    rng    = new Random(Seed < 1 ? 42 : Seed);

        var pts  = new List<Point3d>(Centers);
        var rads = new List<double>(Radii);
        var newPts = new List<Point3d>();

        for (int layer = 0; layer < layers; layer++)
        {
            int n = pts.Count;

            // Normalise to unit scale
            double avgR2 = rads.Average();
            double scl2  = 1.0 / avgR2;
            var    ctr2  = new Point3d(pts.Average(p=>p.X), pts.Average(p=>p.Y), pts.Average(p=>p.Z));
            var NC2 = pts.Select(p => new Point3d((p.X-ctr2.X)*scl2,(p.Y-ctr2.Y)*scl2,(p.Z-ctr2.Z)*scl2)).ToList();
            var W   = rads.Select(r => (r*scl2)*(r*scl2)).ToList();

            // ── Weighted Delaunay tetrahedra ──────────────────────────────
            var tetIdx = new List<int[]>();
            var tetCtr = new List<Point3d>(); // original space

            for (int a = 0; a < n-3; a++)
            for (int b = a+1; b < n-2; b++)
            for (int c = b+1; c < n-1; c++)
            for (int d = c+1; d < n; d++)
            {
                double ori = TetOri(a,b,c,d,NC2);
                if (Math.Abs(ori) < 1e-10) continue;
                int[] idx = ori > 0 ? new[]{a,b,c,d} : new[]{a,b,d,c};

                bool ok = true;
                for (int m = 0; m < n && ok; m++)
                {
                    if (m==idx[0]||m==idx[1]||m==idx[2]||m==idx[3]) continue;
                    if (PowerTest(idx[0],idx[1],idx[2],idx[3],m,NC2,W) > 1e-8) ok = false;
                }
                if (!ok) continue;

                Point3d ccN = WCircumcenter(idx[0],idx[1],idx[2],idx[3],NC2,W);
                if (!ccN.IsValid) continue;
                tetIdx.Add(idx);
                tetCtr.Add(new Point3d(ccN.X/scl2+ctr2.X, ccN.Y/scl2+ctr2.Y, ccN.Z/scl2+ctr2.Z));
            }

            // ── Build face → tet adjacency, find hull triangles ───────────
            var faceMap = new Dictionary<string, List<int>>();
            for (int t = 0; t < tetIdx.Count; t++)
            {
                int[] v = tetIdx[t];
                RegFace(faceMap, v[0],v[1],v[2], t);
                RegFace(faceMap, v[0],v[1],v[3], t);
                RegFace(faceMap, v[0],v[2],v[3], t);
                RegFace(faceMap, v[1],v[2],v[3], t);
            }

            // Hull triangles: faces adjacent to exactly ONE tetrahedron
            var candidates = new List<Point3d>();
            var candRads   = new List<double>();

            foreach (var kv in faceMap)
            {
                if (kv.Value.Count != 1) continue;  // interior face, skip

                string[] parts = kv.Key.Split('_');
                int ia = int.Parse(parts[0]);
                int ib = int.Parse(parts[1]);
                int ic = int.Parse(parts[2]);

                double ra = rads[ia], rb = rads[ib], rc = rads[ic];

                // Centroid of the three supporting bubble centers
                Point3d ca = pts[ia], cb = pts[ib], cc2 = pts[ic];
                Point3d centroid = new Point3d(
                    (ca.X+cb.X+cc2.X)/3.0,
                    (ca.Y+cb.Y+cc2.Y)/3.0,
                    (ca.Z+cb.Z+cc2.Z)/3.0);

                // Outward normal: normal of triangle (a,b,c) pointing AWAY from the adjacent tet
                int tetT = kv.Value[0];
                // The fourth vertex of the tet (not ia, ib, ic)
                int id = tetIdx[tetT].First(v => v!=ia&&v!=ib&&v!=ic);
                Vector3d ab = cb - ca, ac2 = cc2 - ca;
                Vector3d norm = Vector3d.CrossProduct(ab, ac2);
                norm.Unitize();
                // Ensure norm points away from the fourth vertex
                if (norm * (pts[id] - centroid) > 0) norm = -norm;

                // New bubble radius: scale × average supporting radius
                double avgR   = (ra + rb + rc) / 3.0;
                double newR   = avgR * scale;

                // New bubble center: above triangle centroid by (avgR + newR)
                // so it is approximately tangent to the supporting bubbles
                double lift = avgR + newR;
                Point3d cand = centroid + norm * lift;

                candidates.Add(cand);
                candRads.Add(newR);
            }

            // ── Poisson-disk filter against ALL existing seeds ─────────────
            var addedThisLayer = new List<Point3d>();
            var addedRads      = new List<double>();

            // Shuffle candidates for unbiased selection
            var order = Enumerable.Range(0, candidates.Count).OrderBy(_ => rng.NextDouble()).ToList();

            foreach (int ci in order)
            {
                Point3d cand = candidates[ci];
                double  rv   = candRads[ci];

                bool ok2 = true;
                // Check against all existing + already-added this layer
                foreach ((Point3d p, double r) in pts.Zip(rads, (p,r)=>(p,r))
                    .Concat(addedThisLayer.Zip(addedRads, (p,r)=>(p,r))))
                {
                    double dEquil = Math.Sqrt(rv*rv + r*r - rv*r);
                    if (cand.DistanceTo(p) < dEquil * 0.95) { ok2 = false; break; }
                }
                if (!ok2) continue;

                addedThisLayer.Add(cand);
                addedRads.Add(rv);
                newPts.Add(cand);
            }

            pts.AddRange(addedThisLayer);
            rads.AddRange(addedRads);

            Print($"Layer {layer+1}: +{addedThisLayer.Count} bubbles → {pts.Count} total");
        }

        AllCenters = pts;
        AllRadii   = rads;
        NewOnly    = newPts;
    }

    // ── Shared math (duplicated from BubblePowerDiagram for self-containment) ──

    private double TetOri(int i, int j, int k, int l, List<Point3d> P)
    {
        double ax=P[j].X-P[i].X, ay=P[j].Y-P[i].Y, az=P[j].Z-P[i].Z;
        double bx=P[k].X-P[i].X, by=P[k].Y-P[i].Y, bz=P[k].Z-P[i].Z;
        double cx=P[l].X-P[i].X, cy=P[l].Y-P[i].Y, cz=P[l].Z-P[i].Z;
        return ax*(by*cz-bz*cy) - ay*(bx*cz-bz*cx) + az*(bx*cy-by*cx);
    }

    private double PowerTest(int i, int j, int k, int l, int m,
                             List<Point3d> P, List<double> W)
    {
        double lm=Lft(m,P,W), li=Lft(i,P,W)-lm, lj=Lft(j,P,W)-lm;
        double lk=Lft(k,P,W)-lm, ll=Lft(l,P,W)-lm;
        return D4(P[i].X-P[m].X,P[i].Y-P[m].Y,P[i].Z-P[m].Z,li,
                  P[j].X-P[m].X,P[j].Y-P[m].Y,P[j].Z-P[m].Z,lj,
                  P[k].X-P[m].X,P[k].Y-P[m].Y,P[k].Z-P[m].Z,lk,
                  P[l].X-P[m].X,P[l].Y-P[m].Y,P[l].Z-P[m].Z,ll);
    }

    private double Lft(int i, List<Point3d> P, List<double> W)
        => P[i].X*P[i].X + P[i].Y*P[i].Y + P[i].Z*P[i].Z - W[i];

    private Point3d WCircumcenter(int i, int j, int k, int l,
                                  List<Point3d> P, List<double> W)
    {
        double li=Lft(i,P,W), lj=Lft(j,P,W), lk=Lft(k,P,W), ll=Lft(l,P,W);
        double a00=2*(P[j].X-P[i].X), a01=2*(P[j].Y-P[i].Y), a02=2*(P[j].Z-P[i].Z);
        double a10=2*(P[k].X-P[i].X), a11=2*(P[k].Y-P[i].Y), a12=2*(P[k].Z-P[i].Z);
        double a20=2*(P[l].X-P[i].X), a21=2*(P[l].Y-P[i].Y), a22=2*(P[l].Z-P[i].Z);
        double b0=lj-li, b1=lk-li, b2=ll-li;
        double det=D3(a00,a01,a02,a10,a11,a12,a20,a21,a22);
        if (Math.Abs(det)<1e-10) return Point3d.Unset;
        double x=D3(b0,a01,a02,b1,a11,a12,b2,a21,a22)/det;
        double y=D3(a00,b0,a02,a10,b1,a12,a20,b2,a22)/det;
        double z=D3(a00,a01,b0,a10,a11,b1,a20,a21,b2)/det;
        return new Point3d(x,y,z);
    }

    private void RegFace(Dictionary<string,List<int>> map, int a, int b, int c, int t)
    {
        int[] s = new[]{a,b,c}; Array.Sort(s);
        string k = $"{s[0]}_{s[1]}_{s[2]}";
        if (!map.ContainsKey(k)) map[k] = new List<int>();
        map[k].Add(t);
    }

    private double D3(double a,double b,double c,double d,double e,double f,
                      double g,double h,double i)
        => a*(e*i-f*h) - b*(d*i-f*g) + c*(d*h-e*g);

    private double D4(double a0,double a1,double a2,double a3,
                      double b0,double b1,double b2,double b3,
                      double c0,double c1,double c2,double c3,
                      double d0,double d1,double d2,double d3)
    {
        double c0_ = D3(b1,b2,b3,c1,c2,c3,d1,d2,d3);
        double c1_ =-D3(b0,b2,b3,c0,c2,c3,d0,d2,d3);
        double c2_ = D3(b0,b1,b3,c0,c1,c3,d0,d1,d3);
        double c3_ =-D3(b0,b1,b2,c0,c1,c2,d0,d1,d2);
        return a0*c0_ + a1*c1_ + a2*c2_ + a3*c3_;
    }
}

// BubblePowerDiagram — Phase 1 weighted Voronoi skeleton
//
// Computes the 3D power diagram (weighted Voronoi) of bubble seeds.
// Outputs the Plateau border approximations as line segments and the
// tetrahedral node points, both immediately pipeable.
//
// INPUTS (add before pasting):
//   Centers : Point3d  list  — bubble seed positions
//   Radii   : double   list  — bubble radii (parallel to Centers)
//   Clip    : Mesh     item  — optional comfort boundary; segments inside are removed
//
// OUTPUTS (add before pasting):
//   Borders : Line     list  — Plateau border line segments (pipe these)
//   Nodes   : Point3d  list  — tetrahedral junction points
//   Info    : string   item  — diagnostic
//
// HOW IT WORKS
//   Weighted Delaunay lifting map: point (x,y,z) with weight r² lifts to
//   (x, y, z, x²+y²+z²−r²) in R⁴. The lower convex hull of lifted points
//   is the weighted Delaunay triangulation. Its dual is the power diagram.
//   Each internal Delaunay triangle face (shared by 2 tetrahedra) → one
//   border segment connecting the weighted circumcenters of those tetrahedra.
//   N≤50 runs in well under 1 second via brute-force C(N,4) enumeration.

#region Usings
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

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

    private void RunScript(List<Point3d> Centers, List<double> Radii, Mesh Clip,
                           ref object Borders, ref object Nodes, ref object Info)
    {
        if (Centers == null || Radii == null || Centers.Count < 4 || Centers.Count != Radii.Count)
        {
            Info = "Need >= 4 Centers with matching Radii.";
            return;
        }

        int n = Centers.Count;

        // ── Normalise to unit scale to fix floating-point precision ──────────
        // The 4×4 power-sphere determinant uses x²+y²+z²−r². At real-world
        // coords (e.g. x≈100 mm) those terms reach ~30 000, and their 4-way
        // products hit 10¹⁷ — right at double precision limits. Normalising so
        // average radius ≈ 1 keeps individual terms small and determinants
        // accurate. Circumcenters are mapped back to original space afterwards.
        double avgR = Radii.Average();
        double scl  = 1.0 / avgR;
        var ctr = new Point3d(Centers.Average(p => p.X),
                              Centers.Average(p => p.Y),
                              Centers.Average(p => p.Z));

        var NC = Centers.Select(p => new Point3d((p.X-ctr.X)*scl,
                                                  (p.Y-ctr.Y)*scl,
                                                  (p.Z-ctr.Z)*scl)).ToList();
        var W  = Radii.Select(r => (r*scl)*(r*scl)).ToList(); // normalised weights

        // ── Step 1: weighted Delaunay tetrahedra (brute force, fine for N≤50) ──
        var tetIdx  = new List<int[]>();
        var tetCtr  = new List<Point3d>(); // stored in ORIGINAL space

        for (int a = 0; a < n - 3; a++)
        for (int b = a + 1; b < n - 2; b++)
        for (int c = b + 1; c < n - 1; c++)
        for (int d = c + 1; d < n; d++)
        {
            double ori = TetOrientation(a, b, c, d, NC);
            if (Math.Abs(ori) < 1e-10) continue;

            int[] idx = ori > 0
                ? new[] { a, b, c, d }
                : new[] { a, b, d, c };

            bool ok = true;
            for (int m = 0; m < n && ok; m++)
            {
                if (m == idx[0] || m == idx[1] || m == idx[2] || m == idx[3]) continue;
                if (PowerSphereTest(idx[0], idx[1], idx[2], idx[3], m, NC, W) > 1e-8)
                    ok = false;
            }
            if (!ok) continue;

            Point3d ccN = WeightedCircumcenter(idx[0], idx[1], idx[2], idx[3], NC, W);
            if (!ccN.IsValid) continue;

            // Map circumcenter back to original space
            Point3d cc = new Point3d(ccN.X/scl + ctr.X,
                                     ccN.Y/scl + ctr.Y,
                                     ccN.Z/scl + ctr.Z);
            tetIdx.Add(idx);
            tetCtr.Add(cc);
        }

        // ── Step 2: face → tetrahedra map ────────────────────────────────────
        // Key: sorted "a_b_c" string; value: list of tet indices sharing this face
        var faceMap = new Dictionary<string, List<int>>();

        for (int t = 0; t < tetIdx.Count; t++)
        {
            int[] v = tetIdx[t];
            RegisterFace(faceMap, v[0], v[1], v[2], t);
            RegisterFace(faceMap, v[0], v[1], v[3], t);
            RegisterFace(faceMap, v[0], v[2], v[3], t);
            RegisterFace(faceMap, v[1], v[2], v[3], t);
        }

        // ── Step 3: build cluster bounding filter ────────────────────────────
        // Boundary tetrahedra have circumcenters far outside the cluster.
        // Filter: keep only circumcenters within (clusterBB expanded by maxRadius).
        double maxR   = Radii.Max();
        var    seedBB = new BoundingBox(Centers);
        seedBB.Inflate(maxR * 1.5);   // generous padding — real nodes sit just inside cluster

        // Also build a per-node "nearest seed distance" for a tighter secondary filter
        // Reject any circumcenter farther than maxR*3 from its nearest seed
        double cullDist = maxR * 3.0;

        // ── Step 4: internal faces → border segments ─────────────────────────
        var borders  = new List<Line>();
        int culled   = 0;

        foreach (var kv in faceMap)
        {
            if (kv.Value.Count != 2) continue;  // boundary face — skip

            Point3d pa = tetCtr[kv.Value[0]];
            Point3d pb = tetCtr[kv.Value[1]];
            if (!pa.IsValid || !pb.IsValid) continue;
            if (pa.DistanceTo(pb) < 1e-8)   continue;

            // Cull circumcenters outside the padded cluster bounding box
            if (!seedBB.Contains(pa) || !seedBB.Contains(pb)) { culled++; continue; }

            // Secondary: cull if either node is too far from every seed
            if (NearestSeedDist(pa, Centers) > cullDist ||
                NearestSeedDist(pb, Centers) > cullDist)  { culled++; continue; }

            var seg = new Line(pa, pb);

            if (Clip != null && Clip.IsValid)
            {
                seg = ClipOutside(seg, Clip);
                if (!seg.IsValid || seg.Length < 1e-8) continue;
            }

            borders.Add(seg);
        }

        Borders = borders;
        Nodes   = tetCtr.Where(p => p.IsValid && seedBB.Contains(p)).ToList();
        Info    = $"{n} seeds → {tetIdx.Count} tets → {borders.Count} borders ({culled} boundary culled)";
    }

    // Minimum distance from point p to any seed center
    private double NearestSeedDist(Point3d p, List<Point3d> seeds)
    {
        double best = double.MaxValue;
        foreach (Point3d s in seeds)
        {
            double d = p.DistanceTo(s);
            if (d < best) best = d;
        }
        return best;
    }

    // ── Orientation of tetrahedron (i,j,k,l): sign of 3×3 det of (pj-pi, pk-pi, pl-pi) ──
    private double TetOrientation(int i, int j, int k, int l, List<Point3d> P)
    {
        double ax = P[j].X-P[i].X, ay = P[j].Y-P[i].Y, az = P[j].Z-P[i].Z;
        double bx = P[k].X-P[i].X, by = P[k].Y-P[i].Y, bz = P[k].Z-P[i].Z;
        double cx = P[l].X-P[i].X, cy = P[l].Y-P[i].Y, cz = P[l].Z-P[i].Z;
        return ax*(by*cz - bz*cy) - ay*(bx*cz - bz*cx) + az*(bx*cy - by*cx);
    }

    // ── Weighted Delaunay incircle test ──────────────────────────────────────
    // Returns positive if point m lies inside the power circumsphere of tet (i,j,k,l).
    // Uses the 4×4 determinant of the lifting-map differences.
    // Assumes (i,j,k,l) is positively oriented.
    private double PowerSphereTest(int i, int j, int k, int l, int m,
                                   List<Point3d> P, List<double> W)
    {
        double lm = Lift(m, P, W);
        double li = Lift(i, P, W) - lm;
        double lj = Lift(j, P, W) - lm;
        double lk = Lift(k, P, W) - lm;
        double ll = Lift(l, P, W) - lm;

        // 4×4 matrix rows: (xi-xm, yi-ym, zi-zm, λi-λm) for rows i,j,k,l
        return Det4(
            P[i].X-P[m].X, P[i].Y-P[m].Y, P[i].Z-P[m].Z, li,
            P[j].X-P[m].X, P[j].Y-P[m].Y, P[j].Z-P[m].Z, lj,
            P[k].X-P[m].X, P[k].Y-P[m].Y, P[k].Z-P[m].Z, lk,
            P[l].X-P[m].X, P[l].Y-P[m].Y, P[l].Z-P[m].Z, ll);
    }

    // λ(p) = |p|² − w  (the lifting coordinate for the power diagram)
    private double Lift(int i, List<Point3d> P, List<double> W)
    {
        return P[i].X*P[i].X + P[i].Y*P[i].Y + P[i].Z*P[i].Z - W[i];
    }

    // ── Weighted circumcenter (power diagram vertex) ─────────────────────────
    // Solves the 3×3 system: 2(pj-pi)·x = λj−λi (and cyclic for k,l).
    private Point3d WeightedCircumcenter(int i, int j, int k, int l,
                                         List<Point3d> P, List<double> W)
    {
        double li = Lift(i, P, W), lj = Lift(j, P, W);
        double lk = Lift(k, P, W), ll = Lift(l, P, W);

        // 3×3 system  A·x = b
        double a00 = 2*(P[j].X-P[i].X), a01 = 2*(P[j].Y-P[i].Y), a02 = 2*(P[j].Z-P[i].Z);
        double a10 = 2*(P[k].X-P[i].X), a11 = 2*(P[k].Y-P[i].Y), a12 = 2*(P[k].Z-P[i].Z);
        double a20 = 2*(P[l].X-P[i].X), a21 = 2*(P[l].Y-P[i].Y), a22 = 2*(P[l].Z-P[i].Z);
        double b0 = lj-li, b1 = lk-li, b2 = ll-li;

        double det = Det3(a00,a01,a02, a10,a11,a12, a20,a21,a22);
        if (Math.Abs(det) < 1e-10) return Point3d.Unset;

        double x = Det3(b0,a01,a02, b1,a11,a12, b2,a21,a22) / det;
        double y = Det3(a00,b0,a02, a10,b1,a12, a20,b2,a22) / det;
        double z = Det3(a00,a01,b0, a10,a11,b1, a20,a21,b2) / det;

        return new Point3d(x, y, z);
    }

    // ── Clip line: keep the portion outside the mesh ─────────────────────────
    private Line ClipOutside(Line line, Mesh mesh)
    {
        bool aIn = mesh.IsPointInside(line.From, 1e-6, false);
        bool bIn = mesh.IsPointInside(line.To,   1e-6, false);

        if (!aIn && !bIn) return line;
        if ( aIn &&  bIn) return Line.Unset;

        Point3d[] hits = Intersection.MeshLine(mesh, line, out _);
        if (hits == null || hits.Length == 0) return line;

        Point3d outside = aIn ? line.To : line.From;
        Point3d hit = hits.OrderBy(p => p.DistanceTo(outside)).First();

        return aIn ? new Line(hit, line.To) : new Line(line.From, hit);
    }

    // ── Face registration helper ─────────────────────────────────────────────
    private void RegisterFace(Dictionary<string, List<int>> map, int a, int b, int c, int t)
    {
        int[] s = new[] { a, b, c };
        Array.Sort(s);
        string key = $"{s[0]}_{s[1]}_{s[2]}";
        if (!map.ContainsKey(key)) map[key] = new List<int>();
        map[key].Add(t);
    }

    // ── Determinants ─────────────────────────────────────────────────────────
    private double Det3(double a00, double a01, double a02,
                        double a10, double a11, double a12,
                        double a20, double a21, double a22)
    {
        return a00*(a11*a22 - a12*a21)
             - a01*(a10*a22 - a12*a20)
             + a02*(a10*a21 - a11*a20);
    }

    private double Det4(double a00, double a01, double a02, double a03,
                        double a10, double a11, double a12, double a13,
                        double a20, double a21, double a22, double a23,
                        double a30, double a31, double a32, double a33)
    {
        double c0 =  Det3(a11,a12,a13, a21,a22,a23, a31,a32,a33);
        double c1 = -Det3(a10,a12,a13, a20,a22,a23, a30,a32,a33);
        double c2 =  Det3(a10,a11,a13, a20,a21,a23, a30,a31,a33);
        double c3 = -Det3(a10,a11,a12, a20,a21,a22, a30,a31,a32);
        return a00*c0 + a01*c1 + a02*c2 + a03*c3;
    }
}

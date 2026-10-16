// BubbleVoronoiMesh — Saye-style grid-based foam mesh with guaranteed shared vertices
//
// Implements Stage 1 of: Saye (2015) "An Algorithm to Mesh Interconnected Surfaces
// via the Voronoi Interface."
//
// Core principle:
//   All interface polygons are extracted from the same underlying 3-D grid.
//   Each grid edge has ONE crossing point per interface pair.
//   Adjacent cubes sharing a grid edge reference the SAME crossing point →
//   vertices at triple junctions are automatically shared between all surfaces
//   meeting there.  No post-weld step needed.
//
// φᵢ(x) = rᵢ² − |x − cᵢ|²   (positive inside bubble i)
// Interface Γᵢⱼ: where φᵢ(x) = φⱼ(x) ≥ φₖ(x) for all k ≠ i,j
//
// INPUTS:
//   Centers     : Point3d list — bubble centers
//   Radii       : double  list — bubble radii
//   GridSpacing : double       — grid cell size (1-2 mm typical for jewelry scale)
//
// OUTPUTS:
//   Meshes   : Mesh list  — one mesh per interface pair; all share vertices at junctions
//   Info     : string

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

    private void RunScript(List<Point3d> Centers, List<double> Radii, double GridSpacing,
                           ref object Meshes, ref object Info)
    {
        if (Centers == null || Radii == null || Centers.Count < 2 || Centers.Count != Radii.Count)
        { Info = "Need >= 2 Centers with matching Radii."; return; }

        int n = Centers.Count;
        double h = Math.Max(GridSpacing, 0.2);

        // ── Adjacency ─────────────────────────────────────────────────────
        var adjSet = new HashSet<long>(); // packed i*10000+j with i<j
        int pairsFound = 0;
        for (int i=0; i<n; i++)
        for (int j=i+1; j<n; j++)
        {
            double ri=Radii[i], rj=Radii[j];
            double d = Centers[i].DistanceTo(Centers[j]);
            if (d >= ri+rj-1e-6 || d <= Math.Abs(ri-rj)+1e-6) continue;
            double t   = (d*d+ri*ri-rj*rj)/(2.0*d);
            Point3d jP = Centers[i] + (Centers[j]-Centers[i])/d*t;
            double pij = t*t - ri*ri;
            bool dom = false;
            for (int k=0; k<n && !dom; k++)
            { if (k==i||k==j) continue; double dk=Centers[k].DistanceTo(jP); if (dk*dk-Radii[k]*Radii[k]<pij-1e-6) dom=true; }
            if (!dom) { adjSet.Add((long)i*10000+j); pairsFound++; }
        }

        // ── Grid setup ────────────────────────────────────────────────────
        var bb = new BoundingBox(Centers);
        bb.Inflate(Radii.Max() * 1.5 + h);
        int NX = (int)Math.Ceiling((bb.Max.X-bb.Min.X)/h)+2;
        int NY = (int)Math.Ceiling((bb.Max.Y-bb.Min.Y)/h)+2;
        int NZ = (int)Math.Ceiling((bb.Max.Z-bb.Min.Z)/h)+2;
        long NG = (long)NX*NY*NZ;

        // ── Cell labels at each grid vertex ───────────────────────────────
        // label[v] = index of bubble i whose φᵢ(v) is maximum (dominant cell)
        // label = -1 if outside all bubbles (optional: keep as -1)
        var labels = new short[NG];
        for (int iz=0; iz<NZ; iz++)
        for (int iy=0; iy<NY; iy++)
        for (int ix=0; ix<NX; ix++)
        {
            Point3d p = new Point3d(bb.Min.X+ix*h, bb.Min.Y+iy*h, bb.Min.Z+iz*h);
            double best = double.MinValue; short bi = -1;
            for (short k=0; k<n; k++)
            {
                double phi = Radii[k]*Radii[k] - p.DistanceToSquared(Centers[k]);
                if (phi > best) { best=phi; bi=k; }
            }
            labels[IX(ix,iy,iz,NX,NY)] = bi;
        }

        // ── Edge crossings ────────────────────────────────────────────────
        // Key: edge_global_key  →  (crossing_point, cell_a, cell_b)
        // 3 edge directions per vertex: 0=+X, 1=+Y, 2=+Z
        // Key = vertex_index * 3 + direction
        var crossings = new Dictionary<long, (Point3d pt, int ca, int cb)>();

        void ProcessEdge(int ix0, int iy0, int iz0, int dx, int dy, int dz)
        {
            long v0 = IX(ix0,iy0,iz0,NX,NY);
            long v1 = IX(ix0+dx,iy0+dy,iz0+dz,NX,NY);
            int  c0 = labels[v0], c1 = labels[v1];
            if (c0 == c1 || c0 < 0 || c1 < 0) return;
            int ci = Math.Min(c0,c1), cj = Math.Max(c0,c1);
            if (!adjSet.Contains((long)ci*10000+cj)) return;  // not adjacent

            // Crossing where φci(x) = φcj(x): linear in t along edge
            Point3d p0 = new Point3d(bb.Min.X+ix0*h, bb.Min.Y+iy0*h, bb.Min.Z+iz0*h);
            Point3d p1 = new Point3d(bb.Min.X+(ix0+dx)*h, bb.Min.Y+(iy0+dy)*h, bb.Min.Z+(iz0+dz)*h);
            double phi0 = Phi(ci,p0,Centers,Radii) - Phi(cj,p0,Centers,Radii);
            double phi1 = Phi(ci,p1,Centers,Radii) - Phi(cj,p1,Centers,Radii);
            if (Math.Abs(phi0-phi1) < 1e-12) return;
            double t = phi0/(phi0-phi1);
            t = Math.Max(0, Math.Min(1, t));

            int dir = dx==1 ? 0 : dy==1 ? 1 : 2;
            long key = v0*3 + dir;
            crossings[key] = (p0 + (p1-p0)*t, ci, cj);
        }

        for (int iz=0; iz<NZ; iz++)
        for (int iy=0; iy<NY; iy++)
        for (int ix=0; ix<NX; ix++)
        {
            if (ix<NX-1) ProcessEdge(ix,iy,iz, 1,0,0);
            if (iy<NY-1) ProcessEdge(ix,iy,iz, 0,1,0);
            if (iz<NZ-1) ProcessEdge(ix,iy,iz, 0,0,1);
        }

        // ── Per-interface mesh builders ───────────────────────────────────
        // One Mesh per adjacent pair; vertex index map ensures shared vertices.
        var ifMesh  = new Dictionary<long, Mesh>();
        var vertIdx = new Dictionary<long, Dictionary<long,int>>(); // pair→(edge_key→vtx_idx)

        foreach (long pk in adjSet)
        {
            ifMesh[pk]  = new Mesh();
            vertIdx[pk] = new Dictionary<long,int>();
        }

        int GetVtx(long pairKey, long edgeKey, Point3d pt)
        {
            var vmap = vertIdx[pairKey];
            if (vmap.ContainsKey(edgeKey)) return vmap[edgeKey];
            int idx = ifMesh[pairKey].Vertices.Count;
            ifMesh[pairKey].Vertices.Add(pt);
            vmap[edgeKey] = idx;
            return idx;
        }

        // ── Process each cube ─────────────────────────────────────────────
        // 12 edges of cube at (cx,cy,cz) as (origin_offset, direction):
        // X-edges: 4 along x
        // Y-edges: 4 along y
        // Z-edges: 4 along z
        int[,] cubeEdges = {
            // ix+dix, iy+diy, iz+diz, dir
            {0,0,0,0},{0,1,0,0},{0,0,1,0},{0,1,1,0},  // X-edges
            {0,0,0,1},{1,0,0,1},{0,0,1,1},{1,0,1,1},  // Y-edges
            {0,0,0,2},{1,0,0,2},{0,1,0,2},{1,1,0,2}   // Z-edges
        };

        int polysAdded = 0;

        for (int iz=0; iz<NZ-1; iz++)
        for (int iy=0; iy<NY-1; iy++)
        for (int cx=0; cx<NX-1; cx++)
        {
            // Collect crossing points in this cube, grouped by pair key
            var cubeCrossings = new Dictionary<long, List<(long edgeKey, Point3d pt)>>();

            for (int e=0; e<12; e++)
            {
                int lix=cx+cubeEdges[e,0], liy=iy+cubeEdges[e,1], liz=iz+cubeEdges[e,2];
                int dir=cubeEdges[e,3];
                long v0 = IX(lix,liy,liz,NX,NY);
                long ek = v0*3+dir;
                if (!crossings.ContainsKey(ek)) continue;
                var (pt,ca,cb) = crossings[ek];
                long pk = (long)ca*10000+cb;
                if (!cubeCrossings.ContainsKey(pk)) cubeCrossings[pk] = new List<(long,Point3d)>();
                cubeCrossings[pk].Add((ek,pt));
            }

            // For each pair with ≥3 crossings: form a polygon and triangulate
            foreach (var kv in cubeCrossings)
            {
                long pk = kv.Key;
                var cpts = kv.Value;
                if (cpts.Count < 3) continue;

                int ci = (int)(pk/10000), cj = (int)(pk%10000);

                // Sort crossing points by angle in the i-j radical plane
                Vector3d ax  = (Centers[cj]-Centers[ci]); ax.Unitize();
                Vector3d e1  = Perp(ax); e1.Unitize();
                Vector3d e2  = Vector3d.CrossProduct(ax,e1); e2.Unitize();
                Point3d  pIJ = Centers[ci] + ax*((Centers[ci].DistanceTo(Centers[cj])+Radii[ci]*Radii[ci]/Centers[ci].DistanceTo(Centers[cj])-Radii[cj]*Radii[cj]/Centers[ci].DistanceTo(Centers[cj]))*0.5);

                // Simple centroid for angle reference
                double sumX=0,sumY=0,sumZ=0;
                foreach (var (ek,pt) in cpts) { sumX+=pt.X; sumY+=pt.Y; sumZ+=pt.Z; }
                Point3d cen = new Point3d(sumX/cpts.Count, sumY/cpts.Count, sumZ/cpts.Count);

                var sorted = cpts
                    .Select(c => {
                        Vector3d d = c.pt-cen;
                        double u = d*e1, v2 = d*e2;
                        return (c.edgeKey, c.pt, Math.Atan2(v2,u));
                    })
                    .OrderBy(x => x.Item3)
                    .ToList();

                // Fan triangulation from centroid
                int cenV = ifMesh[pk].Vertices.Count;
                ifMesh[pk].Vertices.Add(cen);

                int nv = sorted.Count;
                var vIdxs = sorted.Select(s => GetVtx(pk, s.edgeKey, s.pt)).ToArray();
                for (int f=0; f<nv; f++)
                    ifMesh[pk].Faces.AddFace(cenV, vIdxs[f], vIdxs[(f+1)%nv]);

                polysAdded++;
            }
        }

        // Finalise meshes
        var outMeshes = new List<Mesh>();
        foreach (var kv in ifMesh)
        {
            if (kv.Value.Faces.Count == 0) continue;
            kv.Value.Normals.ComputeNormals();
            outMeshes.Add(kv.Value);
        }

        Meshes = outMeshes;
        Info   = $"{n} seeds | {pairsFound} adjacent pairs | {crossings.Count} edge crossings\n"
               + $"Grid {NX}×{NY}×{NZ} (h={h:F2}) | {polysAdded} cube polygons → {outMeshes.Count} interface meshes";
    }

    // φᵢ(x) = rᵢ² − |x−cᵢ|²
    private double Phi(int i, Point3d x, List<Point3d> C, List<double> R)
        => R[i]*R[i] - x.DistanceToSquared(C[i]);

    // Flat array index
    private long IX(int ix, int iy, int iz, int NX, int NY)
        => ix + (long)iy*NX + (long)iz*NX*NY;

    private Vector3d Perp(Vector3d v)
    {
        Vector3d w = Math.Abs(v.X)<0.9 ? Vector3d.XAxis : Vector3d.YAxis;
        var p = Vector3d.CrossProduct(v,w); p.Unitize(); return p;
    }
}

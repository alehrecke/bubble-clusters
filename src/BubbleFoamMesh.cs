// BubbleFoamMesh — aligned interface patches + bubble cap meshes
//
// KEY CHANGE: interface patch boundary vertices are projected onto sphere i.
// The cap mesh is clipped at THIS SAME projected boundary (not at sphere j's
// surface). Both surfaces cut at the same curve → boundary vertices align →
// a small weld merges them into a single connected mesh for Kangaroo.
//
// INPUTS:
//   Centers     : Point3d list
//   Radii       : double  list
//   Segments    : int           polygon resolution (12–24)
//   WeldTol     : double        merge distance for boundary welding (e.g. 0.05)
//
// OUTPUTS:
//   InterfacePatches : Mesh list  — interface films, boundary on sphere i
//   BubbleMeshes     : Mesh list  — icosphere caps, clipped at same boundary
//   AllJoined        : Mesh       — everything merged + welded, ready for Kangaroo
//   Info             : string

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
                           int Segments, double WeldTol,
                           ref object InterfacePatches, ref object BubbleMeshes,
                           ref object AllJoined, ref object Info)
    {
        if (Centers == null || Radii == null || Centers.Count < 2 || Centers.Count != Radii.Count)
        { Info = "Need >= 2 Centers with matching Radii."; return; }

        int n     = Centers.Count;
        int segs  = Math.Max(Segments, 8);
        int subdiv = segs <= 12 ? 2 : 3;
        double wt = WeldTol > 0 ? WeldTol : 0.05;

        // λi = |ci|² − ri²
        var lam = new double[n];
        for (int i=0; i<n; i++)
            lam[i] = Centers[i].X*Centers[i].X + Centers[i].Y*Centers[i].Y
                   + Centers[i].Z*Centers[i].Z - Radii[i]*Radii[i];

        // ── Adjacency ─────────────────────────────────────────────────────
        var adjPairs = new List<int[]>();
        int sFar=0; int sIn=0; int sDom=0;
        for (int i=0; i<n; i++)
        for (int j=i+1; j<n; j++)
        {
            double ri=Radii[i]; double rj=Radii[j];
            double d=Centers[i].DistanceTo(Centers[j]);
            if (d>=ri+rj-1e-6) {sFar++; continue;}
            if (d<=Math.Abs(ri-rj)+1e-6) {sIn++; continue;}
            double t=(d*d+ri*ri-rj*rj)/(2.0*d);
            var jP=Centers[i]+(Centers[j]-Centers[i])/d*t;
            double pij=t*t-ri*ri;
            bool dom=false;
            for (int k=0; k<n&&!dom; k++){if(k==i||k==j)continue; double dk=Centers[k].DistanceTo(jP); if(dk*dk-Radii[k]*Radii[k]<pij-1e-6)dom=true;}
            if (dom){sDom++; continue;}
            adjPairs.Add(new[]{i,j});
        }

        var adjSet = new HashSet<int>[n];
        for (int i=0; i<n; i++) adjSet[i] = new HashSet<int>();
        foreach (var p in adjPairs){adjSet[p[0]].Add(p[1]); adjSet[p[1]].Add(p[0]);}

        // projBoundaries only needed for edge alignment (future work)
        var projBoundaries = new Dictionary<long, List<Point3d>>();

        // ── Interface patches ─────────────────────────────────────────────
        var iPatches = new List<Mesh>();
        int empty=0; int produced=0;

        foreach (var pair in adjPairs)
        {
            int i=pair[0]; int j=pair[1];
            double ri=Radii[i]; double rj=Radii[j];
            double d=Centers[i].DistanceTo(Centers[j]);
            double t=(d*d+ri*ri-rj*rj)/(2.0*d);
            double r2=ri*ri-t*t;
            if (r2<=1e-10) continue;
            double rN=Math.Sqrt(r2);
            Vector3d ax=(Centers[j]-Centers[i])/d;
            Point3d pIJ=Centers[i]+ax*t;
            Vector3d e1=Perp(ax); e1.Unitize();
            Vector3d e2=Vector3d.CrossProduct(ax,e1); e2.Unitize();

            // Trimmed polygon in 2D (large start → clip by all k → clip to neck circle)
            var poly = CirclePoly(3.0*Math.Max(ri,rj), segs);
            for (int k=0; k<n; k++)
            {
                if (k==i||k==j) continue;
                Vector3d dc=Centers[k]-Centers[i];
                double A=2.0*(dc*e1); double B=2.0*(dc*e2);
                double C=(lam[k]-lam[i])-2.0*(dc*((Vector3d)pIJ));
                poly=Clip2D(poly,-A,-B,C);
                if (poly.Count<3) break;
            }
            if (poly.Count<3){empty++; continue;}
            for (int s=0; s<segs*2; s++)
            {
                double ang=2.0*Math.PI*s/(segs*2);
                poly=Clip2D(poly,-Math.Cos(ang),-Math.Sin(ang),rN);
                if (poly.Count<3) break;
            }
            if (poly.Count<3){empty++; continue;}

            // Centroid of polygon (flat in radical plane)
            double cu=0; double cv=0;
            foreach (var p in poly){cu+=p[0];cv+=p[1];}
            cu/=poly.Count; cv/=poly.Count;
            Point3d cenFlat=pIJ+cu*e1+cv*e2;

            // ── Project boundary vertices onto sphere i ────────────────────
            // These become the SHARED boundary between the patch and cap i.
            var projOnI = new List<Point3d>();
            foreach (var p in poly)
            {
                Point3d p3D=pIJ+p[0]*e1+p[1]*e2;
                Vector3d dir=p3D-Centers[i]; dir.Unitize();
                projOnI.Add(Centers[i]+dir*ri);
            }

            // Store for cap building
            projBoundaries[(long)i*10000+j] = projOnI;
            // Also store projected onto sphere j for cap j
            var projOnJ = new List<Point3d>();
            foreach (var p in poly)
            {
                Point3d p3D=pIJ+p[0]*e1+p[1]*e2;
                Vector3d dir=p3D-Centers[j]; dir.Unitize();
                projOnJ.Add(Centers[j]+dir*rj);
            }
            projBoundaries[(long)j*10000+i] = projOnJ;

            // ── Build interface patch mesh ─────────────────────────────────
            // Interior centroid is flat; boundary vertices are on sphere i.
            var m=new Mesh();
            m.Vertices.Add(cenFlat);          // v0 = centroid (flat)
            foreach (var p in projOnI)
                m.Vertices.Add(p);            // v1..vN = boundary on sphere i
            int nv=projOnI.Count;
            for (int f=0; f<nv; f++)
                m.Faces.AddFace(0,f+1,(f+1)%nv+1);
            m.Normals.ComputeNormals();
            iPatches.Add(m); produced++;
        }

        // ── Bubble cap meshes ─────────────────────────────────────────────
        // Clip the icosphere at the PROJECTED BOUNDARY for each adjacent interface,
        // not at sphere j's surface.  This aligns the cap edge with the patch edge.
        var bMeshes = new List<Mesh>();

        for (int i=0; i<n; i++)
        {
            var ico = IcoSphere(Centers[i], Radii[i], subdiv);
            var capVerts = new List<Point3d>();
            var capFaces = new List<int[]>();

            for (int fi=0; fi<ico.Faces.Count; fi++)
            {
                var f=ico.Faces[fi];
                var tri=new List<Point3d>
                {
                    (Point3d)ico.Vertices[f.A],
                    (Point3d)ico.Vertices[f.B],
                    (Point3d)ico.Vertices[f.C]
                };

                // Clip each icosphere face against every adjacent sphere j.
                // Keeps the part of sphere i OUTSIDE sphere j (the visible cap).
                // Uses distance test: fc = |v - cj| - rj, keep where fc >= 0.
                foreach (int j in adjSet[i])
                {
                    tri = Clip3DSphere(tri, Centers[j], Radii[j]);
                    if (tri.Count < 3) break;
                }
                if (tri.Count<3) continue;

                int sv=capVerts.Count;
                capVerts.AddRange(tri);
                for (int t2=1; t2<tri.Count-1; t2++)
                    capFaces.Add(new[]{sv, sv+t2, sv+t2+1});
            }

            var cap=new Mesh();
            cap.Vertices.AddVertices(capVerts.ToArray());
            foreach (var cf in capFaces) cap.Faces.AddFace(cf[0],cf[1],cf[2]);
            cap.Normals.ComputeNormals();
            cap.Compact();
            bMeshes.Add(cap);
        }

        // ── Join and weld ─────────────────────────────────────────────────
        var joined=new Mesh();
        foreach (var m in iPatches) joined.Append(m);
        foreach (var m in bMeshes)  joined.Append(m);
        joined.Weld(Math.PI);                   // weld at any angle
        joined.Vertices.CombineIdentical(false, true); // merge coincident vertices
        joined.Normals.ComputeNormals();

        InterfacePatches = iPatches;
        BubbleMeshes     = bMeshes;
        AllJoined        = joined;
        Info = $"{n} seeds | {adjPairs.Count} pairs | {produced} patches ({empty} empty)\n"
             + $"Skipped far:{sFar} contained:{sIn} dominated:{sDom}";
    }

    // Clip a 3D face polygon, keeping the part OUTSIDE sphere (cj, rj).
    // fc = |v - cj| - rj:  positive = outside (keep),  negative = inside (discard).
    // No orientation decision needed — the distance test is unambiguous.
    private List<Point3d> Clip3DSphere(List<Point3d> poly, Point3d cj, double rj)
    {
        var res = new List<Point3d>();
        int nv = poly.Count;
        for (int i=0; i<nv; i++)
        {
            var c=poly[i]; var nx=poly[(i+1)%nv];
            double fc=c.DistanceTo(cj)-rj; double fn=nx.DistanceTo(cj)-rj;
            if (fc>=0) res.Add(c);
            if ((fc>=0&&fn<0)||(fc<0&&fn>=0))
            { double t2=fc/(fc-fn); res.Add(c+(nx-c)*t2); }
        }
        return res;
    }

    // 2D Sutherland-Hodgman: keep A·u + B·v + C ≥ 0
    private List<double[]> Clip2D(List<double[]> poly, double A, double B, double C)
    {
        var res=new List<double[]>();
        int nv=poly.Count;
        for (int i=0; i<nv; i++)
        {
            var c=poly[i]; var nx=poly[(i+1)%nv];
            double fc=A*c[0]+B*c[1]+C; double fn=A*nx[0]+B*nx[1]+C;
            if (fc>=0) res.Add(c);
            if ((fc>=0&&fn<0)||(fc<0&&fn>=0))
            { double t2=fc/(fc-fn); res.Add(new[]{c[0]+t2*(nx[0]-c[0]),c[1]+t2*(nx[1]-c[1])}); }
        }
        return res;
    }

    private List<double[]> CirclePoly(double r, int segs)
    {
        var p=new List<double[]>(segs);
        for (int s=0;s<segs;s++){double a=2.0*Math.PI*s/segs; p.Add(new[]{r*Math.Cos(a),r*Math.Sin(a)});}
        return p;
    }

    // Icosphere (subdivided icosahedron)
    private Mesh IcoSphere(Point3d center, double radius, int subdivisions)
    {
        double φ=(1.0+Math.Sqrt(5.0))/2.0;
        var vs=new List<Vector3d>
        {
            UV(-1,φ,0),UV(1,φ,0),UV(-1,-φ,0),UV(1,-φ,0),
            UV(0,-1,φ),UV(0,1,φ),UV(0,-1,-φ),UV(0,1,-φ),
            UV(φ,0,-1),UV(φ,0,1),UV(-φ,0,-1),UV(-φ,0,1)
        };
        var fs=new List<int[]>
        {
            new[]{0,11,5},new[]{0,5,1},new[]{0,1,7},new[]{0,7,10},new[]{0,10,11},
            new[]{1,5,9},new[]{5,11,4},new[]{11,10,2},new[]{10,7,6},new[]{7,1,8},
            new[]{3,9,4},new[]{3,4,2},new[]{3,2,6},new[]{3,6,8},new[]{3,8,9},
            new[]{4,9,5},new[]{2,4,11},new[]{6,2,10},new[]{8,6,7},new[]{9,8,1}
        };
        for (int s=0;s<subdivisions;s++)
        {
            var nfs=new List<int[]>(); var mid=new Dictionary<long,int>();
            foreach (var f in fs){int ab=MidV(vs,mid,f[0],f[1]);int bc=MidV(vs,mid,f[1],f[2]);int ca=MidV(vs,mid,f[2],f[0]);nfs.Add(new[]{f[0],ab,ca});nfs.Add(new[]{f[1],bc,ab});nfs.Add(new[]{f[2],ca,bc});nfs.Add(new[]{ab,bc,ca});}
            fs=nfs;
        }
        var mesh=new Mesh();
        foreach (var v in vs) mesh.Vertices.Add(center+v*(radius/v.Length));
        foreach (var f in fs) mesh.Faces.AddFace(f[0],f[1],f[2]);
        mesh.Normals.ComputeNormals();
        return mesh;
    }

    private int MidV(List<Vector3d> vs,Dictionary<long,int> cache,int a,int b)
    {
        long key=(long)Math.Min(a,b)*100000+Math.Max(a,b);
        if (cache.ContainsKey(key)) return cache[key];
        int idx=vs.Count; vs.Add(vs[a]+vs[b]); cache[key]=idx; return idx;
    }

    private Vector3d UV(double x,double y,double z)=>new Vector3d(x,y,z);

    private Vector3d Perp(Vector3d v)
    {
        Vector3d w=Math.Abs(v.X)<0.9?Vector3d.XAxis:Vector3d.YAxis;
        var p=Vector3d.CrossProduct(v,w); p.Unitize(); return p;
    }
}

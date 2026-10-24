using System;
using System.Collections.Generic;
using System.Linq;

// The dual of a foam (2026-10-24): every bubble becomes a vertex (its volume centroid), every film shared by two
// bubbles an edge between them. Each edge also carries a point ON the shared film (the film vertex nearest the film's
// area centroid), so a curve from centre A through that point to centre B stays inside A and B and never cuts through
// an unrelated neighbour: the "arcing" dual. Bubbles on the skin get stubs, edges from the centre out through their
// outer film (option 2 of the outside question): ending StubLength x mean radius beyond the film.
// Films on a host (the solid) are not edges. Faces (borders -> triangles) and tets (nodes) are a later step.
public class DualNode
{
    public int Bubble;
    public Vec3 Center;
    public double Volume;
}

public class DualEdge
{
    public int A, B;               // bubble ids; B = 0 for a stub (A's film with the outside air)
    public Vec3 FilmPoint;         // on the shared film
    public double Area;            // film area (thicker struts for bigger films)
    public bool IsStub { get { return B == 0; } }
    public Vec3 StubEnd;           // stubs: where the stub ends, outside the film
}

public class FoamDual
{
    public List<DualNode> Nodes = new List<DualNode>();
    public List<DualEdge> Edges = new List<DualEdge>();
    public Dictionary<int, DualNode> ByBubble = new Dictionary<int, DualNode>();

    public static FoamDual Build(FoamMesh m, Func<int, bool> skip = null, double stubLength = 0.35)
    {
        var d = new FoamDual();
        int nb = m.BubbleCount;
        var vol = new double[nb + 1];
        var mom = new Vec3[nb + 1];
        for (int k = 0; k <= nb; k++) mom[k] = Vec3.Zero;
        // Per film (region pair): area, area-weighted centroid, and its triangles (to find a point on the film).
        var area = new Dictionary<long, double>();
        var cen = new Dictionary<long, Vec3>();
        var tris = new Dictionary<long, List<int>>();
        for (int i = 0; i < m.T.Count; i++)
        {
            var t = m.T[i];
            Vec3 a = m.V[t.A], b = m.V[t.B], c = m.V[t.C];
            // Volume centroid by signed tetrahedra from the origin (the Neg side is inside, as FoamMesh.Volumes).
            double v = Vec3.Dot(a, Vec3.Cross(b, c)) / 6.0;
            var tc = (a + b + c) * 0.25;
            if (t.Neg > 0) { vol[t.Neg] += v; mom[t.Neg] = mom[t.Neg] + tc * v; }
            if (t.Pos > 0) { vol[t.Pos] -= v; mom[t.Pos] = mom[t.Pos] - tc * v; }
            if (FoamMesh.IsSolid(t) || t.Neg < 0 || t.Pos < 0) continue;
            long key = FoamMesh.EdgeKey(Math.Min(t.Neg, t.Pos), Math.Max(t.Neg, t.Pos));
            double ar = 0.5 * Vec3.Cross(b - a, c - a).Length;
            var cc = (a + b + c) / 3.0;
            double prev; area.TryGetValue(key, out prev); area[key] = prev + ar;
            Vec3 pc; cen[key] = cen.TryGetValue(key, out pc) ? pc + cc * ar : cc * ar;
            List<int> l; if (!tris.TryGetValue(key, out l)) tris[key] = l = new List<int>(); l.Add(i);
        }
        double rSum = 0; int rCnt = 0;
        for (int k = 1; k <= nb; k++)
        {
            if (vol[k] <= 1e-12 || (skip != null && skip(k))) continue;
            var node = new DualNode { Bubble = k, Center = mom[k] / vol[k], Volume = vol[k] };
            d.Nodes.Add(node); d.ByBubble[k] = node;
            rSum += Math.Pow(3 * vol[k] / (4 * Math.PI), 1.0 / 3); rCnt++;
        }
        double rMean = rCnt > 0 ? rSum / rCnt : 1;
        foreach (var kv in area)
        {
            int lo = (int)(kv.Key >> 32), hi = (int)(kv.Key & 0xffffffff);
            if (lo == hi) continue;
            if (lo != 0 && !d.ByBubble.ContainsKey(lo)) continue;
            if (!d.ByBubble.ContainsKey(hi)) continue;
            var ac = cen[kv.Key] / kv.Value;
            if (lo == 0)
            {
                // Stub: the outer film point straight out from the centre (the film triangle furthest along the
                // direction centre -> film centroid), then a little beyond.
                var node = d.ByBubble[hi];
                var dir = (ac - node.Center).Normalized();
                Vec3 best = ac; double bd = double.MinValue;
                foreach (int ti in tris[kv.Key])
                {
                    var t = m.T[ti];
                    var cc = (m.V[t.A] + m.V[t.B] + m.V[t.C]) / 3.0;
                    // Furthest along dir, among points close to the ray (so it exits where the ray does).
                    var off = cc - node.Center;
                    double along = Vec3.Dot(off, dir), side = (off - dir * along).Length;
                    double score = along - 2 * side;
                    if (score > bd) { bd = score; best = cc; }
                }
                var outDir = (best - node.Center).Normalized();
                d.Edges.Add(new DualEdge { A = hi, B = 0, FilmPoint = best, Area = kv.Value, StubEnd = best + outDir * (stubLength * rMean) });
            }
            else
            {
                Vec3 best = ac; double bd = double.MaxValue;
                foreach (int ti in tris[kv.Key])
                {
                    var t = m.T[ti];
                    var cc = (m.V[t.A] + m.V[t.B] + m.V[t.C]) / 3.0;
                    double dd = (cc - ac).LengthSq;
                    if (dd < bd) { bd = dd; best = cc; }
                }
                d.Edges.Add(new DualEdge { A = lo, B = hi, FilmPoint = best, Area = kv.Value });
            }
        }
        d.Edges = d.Edges.OrderBy(e => e.IsStub ? 1 : 0).ThenBy(e => e.A).ThenBy(e => e.B).ToList();
        return d;
    }

    // A circular arc through p0, p1, p2 as a polyline (straight if they are nearly collinear); for headless use.
    public static List<Vec3> Arc3(Vec3 p0, Vec3 p1, Vec3 p2, int segments = 24)
    {
        var a = p0 - p2; var b = p1 - p2;
        var axb = Vec3.Cross(a, b);
        double den = 2 * axb.LengthSq;
        if (den < 1e-14 * Math.Max(1e-30, a.LengthSq * b.LengthSq)) return new List<Vec3> { p0, p2 };
        var cc = p2 + Vec3.Cross(b * a.LengthSq - a * b.LengthSq, axb) / den;
        var u = (p0 - cc); var w = (p2 - cc); var mid = (p1 - cc);
        double r = u.Length;
        var n = axb.Normalized();
        var e1 = u.Normalized(); var e2 = Vec3.Cross(n, e1);
        Func<Vec3, double> ang = q => { double t = Math.Atan2(Vec3.Dot(q, e2), Vec3.Dot(q, e1)); return t < 0 ? t + 2 * Math.PI : t; };
        double tEnd = ang(w), tMid = ang(mid);
        if (tMid > tEnd) tEnd -= 2 * Math.PI;      // go the way that passes through p1
        var res = new List<Vec3>();
        for (int i = 0; i <= segments; i++)
        {
            double t = tEnd * i / segments;
            res.Add(cc + (e1 * Math.Cos(t) + e2 * Math.Sin(t)) * r);
        }
        return res;
    }
}

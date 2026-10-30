using System;
using System.Collections.Generic;
using System.Linq;

// Peel (Alex, 2026-10-29: "omit all of the outer large cells of a cluster ... just to see what that would look like").
// A cut-away of a settled cluster, no new simulation: the cells on the outside are removed and the walls they shared
// with the cells inside become the new skin, so the inner cells show as the flat-faced polyhedra they are inside a foam.
//   peel-all          every cell touching the outside air is removed
//   peel-large        only the outer cells larger than the median outer cell
//   peel-large-open   peel-large, drawn as Mist Open
// The faces left are not in equilibrium (they were walls, now exposed); this is a view, not a new foam.
public static partial class Edition
{
    static bool IsPeel(string kind) { return kind.StartsWith("peel"); }

    static FoamSolver Peeled(FoamSolver s, string kind, out int removed, out int kept)
    {
        var m = s.Mesh;
        var vol = m.Volumes();
        var outer = new HashSet<int>();
        foreach (var t in m.T)
        {
            if (t.Neg == 0 && t.Pos > 0) outer.Add(t.Pos);
            if (t.Pos == 0 && t.Neg > 0) outer.Add(t.Neg);
        }
        var drop = new HashSet<int>(outer);
        if (kind.StartsWith("peel-large"))
        {
            var r = outer.ToDictionary(k => k, k => Math.Pow(3 * Math.Max(0, vol[k]) / (4 * Math.PI), 1.0 / 3));
            double med = r.Values.OrderBy(x => x).ElementAt(r.Count / 2);
            drop = new HashSet<int>(outer.Where(k => r[k] > med));
        }
        var nm = new FoamMesh { BubbleCount = m.BubbleCount };
        nm.V.AddRange(m.V);
        foreach (var t in m.T)
        {
            int a = drop.Contains(t.Neg) ? 0 : t.Neg, b = drop.Contains(t.Pos) ? 0 : t.Pos;
            if (a == b) continue;                       // both sides gone (or now both outside)
            if (a <= 0 && b <= 0) continue;             // nothing left on either side
            nm.T.Add(new FoamTri(t.A, t.B, t.C, a, b));
        }
        removed = drop.Count;
        kept = Enumerable.Range(1, m.BubbleCount).Count(k => vol[k] > 0 && !drop.Contains(k));
        return new FoamSolver(nm, new SolverSettings());
    }
}

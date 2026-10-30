using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Reframe (Alex, 2026-10-29: chains and clusters with satellites "read so small ... flat and small in the distance").
// Re-renders saved gallery clusters (no new simulation) with a closer composition:
//   main body   the camera fits the largest connected piece only; satellites fall where they fall, cropped by the
//               tile if need be
//   long axis   the view is rolled (the cluster turned about the view direction) so the main body's long axis runs
//               along the tile's long side
//   margins     the frame uses ~90% of the tile (was 79% x 72%)
//   lens        chains, branches and rings get a perspective warp (nearer parts larger, a camera ~2.2 radii away):
//               p' = c + (x R + y U) * D / (D + z) + z F in camera coordinates, then the usual orthographic render
// Everything outside the tile is clipped so tiles don't spill into each other.
public static partial class Edition
{
    static double[] FrameOverride;
    static List<Vec3> FitOverride;

    public static Plot ReframedTile(string objPath, string shape, int seed, HatchStyle hs, EditionSettings e, MarkSettings ms, bool lens)
    {
        var s = new FoamSolver(FoamAnalysis.FromObj(File.ReadAllText(objPath)), new SolverSettings());
        var view = ShapeView(s, shape, seed);
        var m = s.Mesh;
        var F = view.Forward.Normalized();
        var R = Vec3.Cross(F, new Vec3(0, 0, 1)); if (R.LengthSq < 1e-9) R = new Vec3(1, 0, 0); R = R.Normalized();
        var U = Vec3.Cross(R, F).Normalized();

        // Main piece: the connected piece with the most bubbles.
        bool[] onHost; var piece = Contacts.Pieces(m, out onHost);
        int main = Enumerable.Range(1, m.BubbleCount).Where(k => k < piece.Length && piece[k] > 0)
                             .GroupBy(k => piece[k]).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var mainVerts = new HashSet<int>();
        foreach (var t in m.T)
        {
            bool inMain = (t.Neg > 0 && t.Neg < piece.Length && piece[t.Neg] == main) || (t.Pos > 0 && t.Pos < piece.Length && piece[t.Pos] == main);
            if (inMain) { mainVerts.Add(t.A); mainVerts.Add(t.B); mainVerts.Add(t.C); }
        }
        var c = mainVerts.Aggregate(Vec3.Zero, (a, i) => a + m.V[i]) / Math.Max(1, mainVerts.Count);

        // Roll: principal axis of the main body on screen -> vertical (the tile is portrait).
        double sxx = 0, syy = 0, sxy = 0;
        foreach (int i in mainVerts) { var d = m.V[i] - c; double x = Vec3.Dot(d, R), y = Vec3.Dot(d, U); sxx += x * x; syy += y * y; sxy += x * y; }
        double phi = 0.5 * Math.Atan2(2 * sxy, sxx - syy);          // long axis angle from R
        double aspect = Math.Sqrt(Math.Max(1e-12, (sxx + syy + Math.Sqrt((sxx - syy) * (sxx - syy) + 4 * sxy * sxy)) /
                                             Math.Max(1e-12, sxx + syy - Math.Sqrt((sxx - syy) * (sxx - syy) + 4 * sxy * sxy))));
        double roll = aspect > 1.15 ? Math.PI / 2 - phi : 0;
        double cr = Math.Cos(roll), sr = Math.Sin(roll);
        double rMain = mainVerts.Max(i => (m.V[i] - c).Length), D = 2.2 * rMain;
        for (int i = 0; i < m.V.Count; i++)
        {
            var d = m.V[i] - c;
            double x = Vec3.Dot(d, R), y = Vec3.Dot(d, U), z = Vec3.Dot(d, F);
            double x2 = cr * x - sr * y, y2 = sr * x + cr * y;           // rotate about F
            if (lens) { double k = D / Math.Max(0.25 * D, D + z); x2 *= k; y2 *= k; }
            m.V[i] = c + R * x2 + U * y2 + F * z;
        }

        var p = new Plot { Width = e.SheetW, Height = e.SheetH };
        try
        {
            FrameOverride = new[] { 0.05, 0.045, 0.90, 0.87 };
            FitOverride = mainVerts.Select(i => m.V[i]).ToList();
            p = MarkOverview(s, view, hs, e, ms);
        }
        finally { FrameOverride = null; FitOverride = null; }
        // Keep everything inside the tile (labels too).
        double mx = 0.02 * p.Width, my = 0.02 * p.Height;
        foreach (var name in p.LayerOrder.ToList())
        {
            if (Plot.PreviewOnly(name)) continue;
            var clipped = ClipRect(p.Layers[name], mx, my, p.Width - mx, p.Height - 0.06 * p.Height);
            p.Layers[name].Clear(); p.Layers[name].AddRange(clipped);
        }
        return p;
    }
}

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
        double rMain = mainVerts.Max(i => (m.V[i] - c).Length);
        // Camera coordinates after the roll.
        Func<Vec3, Vec3> toCam = q => { var d = q - c; double x = Vec3.Dot(d, R), y = Vec3.Dot(d, U), z = Vec3.Dot(d, F); return new Vec3(cr * x - sr * y, sr * x + cr * y, z); };
        var cam = m.V.Select(toCam).ToList();

        // Round 2: pull the satellites in (rigid moves of pieces that touch nothing: the physics is unchanged), to a
        // ring just outside the main body on screen, each at its own depth, so nothing has to be cropped.
        var rng = new Random(seed * 41 + 3);
        var vertPiece = new int[m.V.Count];
        foreach (var t in m.T)
        {
            int k = t.Neg > 0 ? t.Neg : t.Pos;
            if (k > 0 && k < piece.Length) { vertPiece[t.A] = piece[k]; vertPiece[t.B] = piece[k]; vertPiece[t.C] = piece[k]; }
        }
        // Round 3: an ellipse around the main body (its screen half-extents), not a circle of its longest reach: on a
        // long chain the circle sent satellites far up and down the tile and shrank everything again.
        double ex = mainVerts.Max(i => Math.Abs(cam[i].X)), ey = mainVerts.Max(i => Math.Abs(cam[i].Y));
        var placed = new List<KeyValuePair<Vec3, double>>();      // (centre, radius) in camera coords
        foreach (var grp in Enumerable.Range(0, m.V.Count).Where(i => vertPiece[i] != 0 && vertPiece[i] != main).GroupBy(i => vertPiece[i]))
        {
            var idx = grp.ToList();
            var pc = idx.Aggregate(Vec3.Zero, (a, i) => a + cam[i]) / idx.Count;
            double pr = idx.Max(i => (cam[i] - pc).Length);
            double ang = Math.Atan2(pc.Y, pc.X);
            Vec3 target = pc, last = pc;
            bool found = false;
            for (int tries = 0; tries < 60; tries++)
            {
                double f = (0.95 + 0.4 * rng.NextDouble()) * (1 + 0.02 * tries);   // widen slowly if crowded
                double z = (rng.NextDouble() * 2 - 1) * 0.8 * rMain;
                var cand = new Vec3(Math.Cos(ang) * (ex * f + pr * 1.3), Math.Sin(ang) * (ey * f + pr * 1.3), z);
                bool ok = placed.All(q => (q.Key - cand).Length > q.Value + pr + 0.15 * rMain);
                // Clear of the main body in 3D too.
                ok = ok && mainVerts.All(i => (cam[i] - cand).Length > pr * 1.1);
                last = cand;
                if (ok) { target = cand; found = true; break; }
                ang += 0.45;
            }
            // No clear spot: still bring it in (last try, just outside the ring) rather than leave it far out (7516).
            if (!found) target = last;
            placed.Add(new KeyValuePair<Vec3, double>(target, pr));
            var shift = target - pc;
            foreach (int i in idx) cam[i] = cam[i] + shift;
        }

        // Lens: a perspective warp. Strong for long shapes seen side-on, gentle for the rest (an elongated body pointing
        // at the camera turned into one giant near bubble with a strong lens: 7531 in round 1).
        double lx = 0, ly = 0, lz = 0, lxx = 0, lyy = 0, lzz = 0, lxy = 0, lxz = 0, lyz = 0;
        foreach (int i in mainVerts) { var q = cam[i]; lxx += q.X * q.X; lyy += q.Y * q.Y; lzz += q.Z * q.Z; lxy += q.X * q.Y; lxz += q.X * q.Z; lyz += q.Y * q.Z; }
        double[] ax = { 1, 0.2, 0.1 };
        for (int it = 0; it < 50; it++)
        {
            double[] nx = { lxx * ax[0] + lxy * ax[1] + lxz * ax[2], lxy * ax[0] + lyy * ax[1] + lyz * ax[2], lxz * ax[0] + lyz * ax[1] + lzz * ax[2] };
            double l = Math.Sqrt(nx.Sum(z => z * z)); ax = nx.Select(z => z / l).ToArray();
        }
        bool sideOn = Math.Abs(ax[2]) < 0.4;
        double D = (lens && sideOn ? 2.6 : 4.5) * rMain;
        // Satellites are scaled as a whole (by the lens factor at their centre) so they stay round; the main body
        // gets the full warp.
        var pieceK = new Dictionary<int, KeyValuePair<Vec3, double>>();
        foreach (var grp in Enumerable.Range(0, m.V.Count).Where(i => vertPiece[i] != 0 && vertPiece[i] != main).GroupBy(i => vertPiece[i]))
        {
            var pc = grp.Aggregate(Vec3.Zero, (a, i) => a + cam[i]) / grp.Count();
            pieceK[grp.Key] = new KeyValuePair<Vec3, double>(pc, D / Math.Max(0.3 * D, D + pc.Z));
        }
        for (int i = 0; i < m.V.Count; i++)
        {
            var q = cam[i];
            KeyValuePair<Vec3, double> pk;
            if (vertPiece[i] != 0 && vertPiece[i] != main && pieceK.TryGetValue(vertPiece[i], out pk))
            {
                var pc = pk.Key; double k = pk.Value;
                var r2 = q - pc;
                m.V[i] = c + R * (pc.X * k + r2.X * k) + U * (pc.Y * k + r2.Y * k) + F * q.Z;
            }
            else
            {
                double k = D / Math.Max(0.3 * D, D + q.Z);
                m.V[i] = c + R * (q.X * k) + U * (q.Y * k) + F * q.Z;
            }
        }
        var p = new Plot { Width = e.SheetW, Height = e.SheetH };
        try
        {
            // Fit to everything (nothing cropped) now that the satellites sit close.
            FrameOverride = new[] { 0.07, 0.06, 0.86, 0.82 };
            FitOverride = m.V.ToList();
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

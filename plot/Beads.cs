using System;
using System.Collections.Generic;
using System.Linq;

// Satellite bubbles at the Plateau borders (Alex, 2026-10-20: "smaller thinner bubbles forming at the meniscus
// without having to simulate them, as a post-processing step"). Not simulated: small spheres are placed in 3D
// along the cleaned borders, clumped at the nodes and shrinking along each border away from them (like the
// little bubbles that collect in the liquid channels of a wet foam), packed so they don't overlap. Each is
// drawn as its outline circle with real depth: nearer beads hide the parts of farther ones they cover, the
// cluster's surface hides beads behind it (or not, in the transparent wire version), and a small gap is left
// on the side facing the light (a highlight).
//   mist-beads / mist-beads-dense   Mist (dots only) + beads
//   wire-beads / wire-beads-dense   the front border network only (round 3; the see-through one read as chaos) + beads
public class BeadSettings
{
    public double NodeRadius = 0.12;     // bead radius at a node, as a fraction of the mean bubble radius
    public double MinRadius = 0.018;     // smallest bead (same units)
    public double Decay = 0.22;          // radius halves over ~Decay x mean radius along a border
    public double BorderChance = 0.75;   // fraction of borders that carry beads
    public int NodeClump = 6;            // extra beads clumped around each node
}

public static partial class Edition
{
    static bool IsBeads(string kind) { return kind.StartsWith("mist-beads") || kind.StartsWith("wire-beads"); }

    static Plot BeadOverview(FoamSolver s, View v, HatchStyle baseSt, EditionSettings e, MarkSettings ms)
    {
        bool dense = ms.Kind.EndsWith("-dense"), wire = ms.Kind.StartsWith("wire");
        Plot p;
        Scene sc;
        if (wire)
        {
            p = NewPlot(e);
            sc = Frame(s, v, 0, OverviewFrame, p, e, null);
            p.Layer("outline").AddRange(Outlines(s, sc, 0));
        }
        else
        {
            var mm = new MarkSettings { Kind = "glass-pure" };
            p = MarkOverviewRaw(s, v, baseSt, e, mm);
            sc = Frame(s, v, 0, OverviewFrame, p, e, null);
        }
        // Round 2: bigger beads collecting at the nodes (fast falloff along the borders), like the meniscus.
        var bs = dense ? new BeadSettings { NodeRadius = 0.16, Decay = 0.32, BorderChance = 0.95, NodeClump = 10 } : new BeadSettings();
        List<KeyValuePair<V2, double>> discs;
        p.Layer("beads").AddRange(Beads(s, sc, bs, new Random(v.Seed * 7 + 17), true, out discs));
        if (!wire)
        {
            // Clear the stipple under each visible bead so it reads as a clean little sphere on the dotted skin.
            var cg = new Dictionary<long, List<int>>(LongKey.Comparer);
            double cc = 2.0;
            Func<double, double, long> ck = (x, y) => unchecked(((long)Math.Floor(x / cc) * 73856093L) ^ ((long)Math.Floor(y / cc) * 19349663L));
            for (int i = 0; i < discs.Count; i++)
            {
                long k = ck(discs[i].Key.X, discs[i].Key.Y);
                List<int> l; if (!cg.TryGetValue(k, out l)) cg[k] = l = new List<int>(); l.Add(i);
            }
            Func<V2, bool> covered = q =>
            {
                for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        List<int> l; if (!cg.TryGetValue(ck(q.X + dx * cc, q.Y + dy * cc), out l)) continue;
                        foreach (int j in l) if ((discs[j].Key - q).Length < discs[j].Value + 0.15) return true;
                    }
                return false;
            };
            foreach (var name in new[] { "hatch", "inner", "innerborders" })
            {
                if (!p.Layers.ContainsKey(name)) continue;
                var kept = p.Layers[name].Where(d => d.Count != 2 || !covered((d[0] + d[1]) * 0.5)).ToList();
                p.Layers[name].Clear(); p.Layers[name].AddRange(kept);
            }
        }
        return p;
    }

    static List<List<V2>> Beads(FoamSolver s, Scene sc, BeadSettings bs, Random rng, bool hideBehindSurface, out List<KeyValuePair<V2, double>> discs)
    {
        discs = new List<KeyValuePair<V2, double>>();
        var m = s.Mesh;
        var vol = m.Volumes();
        double rMean = Enumerable.Range(1, m.BubbleCount).Where(k => vol[k] > 0).Average(k => Math.Pow(3 * vol[k] / (4 * Math.PI), 1.0 / 3));
        double r0 = bs.NodeRadius * rMean, rMin = bs.MinRadius * rMean, decay = bs.Decay * rMean;
        var borders = BorderClean.Clean(m, s.TargetEdge, new BorderCleanSettings { Spacing = s.TargetEdge * 0.5, IncludeHost = false });

        var beads = new List<KeyValuePair<Vec3, double>>();
        double cell = 2 * r0;
        var grid = new Dictionary<long, List<int>>(LongKey.Comparer);
        Func<Vec3, long> gk = q => unchecked(((long)Math.Floor(q.X / cell) * 73856093L) ^ ((long)Math.Floor(q.Y / cell) * 19349663L) ^ ((long)Math.Floor(q.Z / cell) * 83492791L));
        Func<Vec3, double, bool> tryAdd = (c, rad) =>
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        List<int> l;
                        if (!grid.TryGetValue(gk(c + new Vec3(dx * cell, dy * cell, dz * cell)), out l)) continue;
                        foreach (int j in l) if ((beads[j].Key - c).Length < 0.9 * (beads[j].Value + rad)) return false;
                    }
            long k = gk(c);
            List<int> cl; if (!grid.TryGetValue(k, out cl)) grid[k] = cl = new List<int>();
            cl.Add(beads.Count); beads.Add(new KeyValuePair<Vec3, double>(c, rad));
            return true;
        };

        var nodes = new Dictionary<int, Vec3>();
        foreach (var b in borders)
        {
            if (b.Closed || b.Points.Count < 2) continue;
            nodes[b.StartNode] = b.Points[0]; nodes[b.EndNode] = b.Points[b.Points.Count - 1];
            if (rng.NextDouble() > bs.BorderChance) continue;
            // Walk in from each end: beads touching each other, shrinking with distance from the node.
            for (int end = 0; end < 2; end++)
            {
                var pts = end == 0 ? b.Points : Enumerable.Reverse(b.Points).ToList();
                var along = new double[pts.Count];
                for (int i = 1; i < pts.Count; i++) along[i] = along[i - 1] + (pts[i] - pts[i - 1]).Length;
                double half = along[pts.Count - 1] / 2, sPos = r0 * (0.8 + 0.6 * rng.NextDouble());
                double scale = 0.7 + 0.6 * rng.NextDouble();
                while (sPos < half)
                {
                    double rad = r0 * scale * Math.Pow(0.5, sPos / decay) * (0.75 + 0.5 * rng.NextDouble());
                    if (rad < rMin) break;
                    int i = Array.FindIndex(along, a => a >= sPos); if (i < 1) i = 1;
                    double u = (sPos - along[i - 1]) / Math.Max(1e-12, along[i] - along[i - 1]);
                    var c = pts[i - 1] + (pts[i] - pts[i - 1]) * u;
                    var t = (pts[i] - pts[i - 1]).Normalized();
                    var off = RandomUnit(rng); off = (off - t * Vec3.Dot(off, t)).Normalized();
                    tryAdd(c + off * (rad * 0.35 * rng.NextDouble()), rad);
                    sPos += rad * (1.7 + 0.8 * rng.NextDouble());
                }
            }
        }
        // A small clump at every node.
        foreach (var nd in nodes.Values)
            for (int k = 0; k < bs.NodeClump; k++)
            {
                double rad = r0 * (0.45 + 0.6 * rng.NextDouble());
                tryAdd(nd + RandomUnit(rng) * (r0 * (0.6 + 0.9 * rng.NextDouble())), rad);
            }

        // Draw: circle outlines in the view plane, nearer beads hiding farther ones, highlight gap toward the light.
        var light2 = new V2(Vec3.Dot(sc.Light, sc.R), -Vec3.Dot(sc.Light, sc.U)).Normalized();
        var proj = beads.Select(b => sc.Project(b.Key)).ToList();
        var depth = beads.Select(b => sc.Depth(b.Key)).ToList();
        double cell2 = Math.Max(0.5, 2 * r0 * sc.S);
        var g2 = new Dictionary<long, List<int>>(LongKey.Comparer);
        Func<double, double, long> k2 = (x, y) => unchecked(((long)Math.Floor(x / cell2) * 73856093L) ^ ((long)Math.Floor(y / cell2) * 19349663L));
        for (int i = 0; i < beads.Count; i++)
        {
            long k = k2(proj[i].X, proj[i].Y);
            List<int> l; if (!g2.TryGetValue(k, out l)) g2[k] = l = new List<int>(); l.Add(i);
        }
        // Visibility per bead, not per point (round 3: a bead sits on the surface at its border, so a per-point depth
        // test hid half of every circle): a bead is drawn whole when its front point (centre moved toward the
        // viewer by its radius) is in front of the surface, else not at all.
        double veps = 6 / (sc.S * sc.Res);
        var front = new bool[beads.Count];
        for (int i = 0; i < beads.Count; i++)
        {
            var fp = beads[i].Key - sc.F * beads[i].Value;
            front[i] = !hideBehindSurface || sc.Visible(fp, beads[i].Value);
            if (front[i]) discs.Add(new KeyValuePair<V2, double>(proj[i], beads[i].Value * sc.S));
        }
        var res = new List<List<V2>>();
        for (int i = 0; i < beads.Count; i++)
        {
            if (!front[i]) continue;
            double rs = beads[i].Value * sc.S;
            if (rs < 0.12) continue;
            int n = Math.Max(10, (int)(2 * Math.PI * rs / 0.12));
            double gapC = Math.Atan2(light2.Y, light2.X), gapW = 0.45;
            var cur = new List<V2>();
            for (int a = 0; a <= n; a++)
            {
                double ang = 2 * Math.PI * a / n;
                double dg = Math.Abs(Math.IEEERemainder(ang - gapC, 2 * Math.PI));
                var q2 = proj[i] + new V2(Math.Cos(ang), Math.Sin(ang)) * rs;
                bool hidden = dg < gapW;
                if (!hidden)
                    for (int dx = -1; dx <= 1 && !hidden; dx++)
                        for (int dy = -1; dy <= 1 && !hidden; dy++)
                        {
                            List<int> l; if (!g2.TryGetValue(k2(q2.X + dx * cell2, q2.Y + dy * cell2), out l)) continue;
                            foreach (int j in l)
                                if (j != i && front[j] && depth[j] < depth[i] && (proj[j] - q2).Length < beads[j].Value * sc.S - 0.04) { hidden = true; break; }
                        }
                if (hidden) { if (cur.Count >= 2) res.Add(cur); cur = new List<V2>(); continue; }
                cur.Add(q2);
            }
            if (cur.Count >= 2) res.Add(cur);
        }
        return res;
    }
}

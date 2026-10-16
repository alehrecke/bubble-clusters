using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Read-outs from a film mesh: Plateau borders, nodes, junction angles, sphere fits.
public static class FoamAnalysis
{
    public class Chain
    {
        public List<int> Verts = new List<int>();
        public bool Closed;
    }

    // Border edges joined into chains that run node to node (or close on themselves).
    public static List<Chain> BorderChains(FoamMesh m, out List<int> nodes)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (var kv in m.EdgeTris())
        {
            if (!m.IsBorder(kv.Value)) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            if (!adj.ContainsKey(a)) adj[a] = new List<int>();
            if (!adj.ContainsKey(b)) adj[b] = new List<int>();
            adj[a].Add(b); adj[b].Add(a);
        }
        nodes = adj.Where(kv => kv.Value.Count != 2).Select(kv => kv.Key).ToList();
        var used = new HashSet<long>(LongKey.Comparer);
        var chains = new List<Chain>();
        foreach (int s in nodes)
            foreach (int nb in adj[s])
            {
                if (used.Contains(FoamMesh.EdgeKey(s, nb))) continue;
                var c = new Chain();
                c.Verts.Add(s);
                int prev = s, cur = nb;
                used.Add(FoamMesh.EdgeKey(s, nb));
                while (true)
                {
                    c.Verts.Add(cur);
                    if (adj[cur].Count != 2) break;
                    int nx = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
                    if (used.Contains(FoamMesh.EdgeKey(cur, nx))) break;
                    used.Add(FoamMesh.EdgeKey(cur, nx));
                    prev = cur; cur = nx;
                }
                chains.Add(c);
            }
        // Closed loops with no nodes (e.g. the ring of a double bubble).
        foreach (var kv in adj)
            foreach (int nb in kv.Value)
            {
                if (used.Contains(FoamMesh.EdgeKey(kv.Key, nb))) continue;
                var c = new Chain { Closed = true };
                int start = kv.Key, prev = start, cur = nb;
                c.Verts.Add(start);
                used.Add(FoamMesh.EdgeKey(start, nb));
                while (cur != start)
                {
                    c.Verts.Add(cur);
                    int nx = adj[cur][0] == prev ? adj[cur][1] : adj[cur][0];
                    used.Add(FoamMesh.EdgeKey(cur, nx));
                    prev = cur; cur = nx;
                }
                chains.Add(c);
            }
        return chains;
    }

    public class AngleStats
    {
        public int Count;
        public double Mean, MeanAbsDev, MaxAbsDev;
        public override string ToString()
        {
            return Count == 0 ? "n/a" :
                string.Format(CultureInfo.InvariantCulture, "mean {0:F2}°, |dev| mean {1:F2}° max {2:F2}° (n={3})",
                              Mean, MeanAbsDev, MaxAbsDev, Count);
        }
    }

    static AngleStats Stats(List<double> angles, double target)
    {
        var s = new AngleStats { Count = angles.Count };
        if (angles.Count == 0) return s;
        s.Mean = angles.Average();
        s.MeanAbsDev = angles.Average(a => Math.Abs(a - target));
        s.MaxAbsDev = angles.Max(a => Math.Abs(a - target));
        return s;
    }

    // Angles between the three films at each border edge (target 120°). Edges touching a
    // node are skipped; the film direction is taken toward each triangle's far vertex.
    public static AngleStats BorderAngles(FoamMesh m)
    {
        List<int> nodes;
        BorderChains(m, out nodes);
        var nodeSet = new HashSet<int>(nodes);
        var angles = new List<double>();
        foreach (var kv in m.EdgeTris())
        {
            if (kv.Value.Count != 3) continue;
            if (kv.Value.Any(ti => FoamMesh.IsSolid(m.T[ti]))) continue;   // a wall standing on a host: see ContactAngles
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            if (nodeSet.Contains(a) || nodeSet.Contains(b)) continue;
            Vec3 e = (m.V[b] - m.V[a]).Normalized();
            Vec3 mid = (m.V[a] + m.V[b]) * 0.5;
            var dirs = new List<Vec3>();
            foreach (int ti in kv.Value)
            {
                var t = m.T[ti];
                int o = t.A != a && t.A != b ? t.A : (t.B != a && t.B != b ? t.B : t.C);
                Vec3 d = m.V[o] - mid;
                dirs.Add((d - e * Vec3.Dot(d, e)).Normalized());
            }
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    angles.Add(Deg(dirs[i], dirs[j]));
        }
        return Stats(angles, 120.0);
    }

    // Angles between the four borders at each valence-4 node (target 109.47°). Each border
    // direction is measured to the vertex `reach` steps along its chain.
    public static AngleStats NodeAngles(FoamMesh m, int reach)
    {
        List<int> nodes;
        var chains = BorderChains(m, out nodes);
        var dirsAt = new Dictionary<int, List<Vec3>>();
        foreach (var c in chains)
        {
            if (c.Closed || c.Verts.Count < 2) continue;
            int s = c.Verts[0], e = c.Verts[c.Verts.Count - 1];
            int k = Math.Min(reach, c.Verts.Count - 1);
            Add(dirsAt, s, (m.V[c.Verts[k]] - m.V[s]).Normalized());
            Add(dirsAt, e, (m.V[c.Verts[c.Verts.Count - 1 - k]] - m.V[e]).Normalized());
        }
        var angles = new List<double>();
        var onHost = new HashSet<int>();
        foreach (var t in m.T) if (FoamMesh.IsSolid(t)) { onHost.Add(t.A); onHost.Add(t.B); onHost.Add(t.C); }
        foreach (var kv in dirsAt)
        {
            if (kv.Value.Count != 4 || onHost.Contains(kv.Key)) continue;   // a border landing on a host isn't a tetrahedral node
            for (int i = 0; i < 4; i++)
                for (int j = i + 1; j < 4; j++)
                    angles.Add(Deg(kv.Value[i], kv.Value[j]));
        }
        return Stats(angles, 109.4712);
    }

    // Angle between each film and the host where it meets it (target 90 degrees): at every footprint
    // edge, the angle between the footprint triangle and each film triangle across that edge.
    public static AngleStats ContactAngles(FoamMesh m)
    {
        var angles = new List<double>();
        foreach (var kv in m.EdgeTris())
        {
            var tris = kv.Value;
            int foot = -1;
            foreach (int ti in tris) if (FoamMesh.IsSolid(m.T[ti])) { foot = ti; break; }
            if (foot < 0 || !m.IsBorder(tris)) continue;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            Vec3 e = (m.V[b] - m.V[a]).Normalized(), mid = (m.V[a] + m.V[b]) * 0.5;
            Func<int, Vec3> dir = ti =>
            {
                var t = m.T[ti];
                int o = t.A != a && t.A != b ? t.A : (t.B != a && t.B != b ? t.B : t.C);
                Vec3 d = m.V[o] - mid;
                return (d - e * Vec3.Dot(d, e)).Normalized();
            };
            Vec3 df = dir(foot);
            foreach (int ti in tris)
            {
                if (FoamMesh.IsSolid(m.T[ti])) continue;
                angles.Add(Deg(df, dir(ti)));
            }
        }
        return Stats(angles, 90.0);
    }

    static void Add(Dictionary<int, List<Vec3>> d, int k, Vec3 v)
    {
        List<Vec3> l;
        if (!d.TryGetValue(k, out l)) { l = new List<Vec3>(); d[k] = l; }
        l.Add(v);
    }

    static double Deg(Vec3 a, Vec3 b)
    {
        double c = Math.Max(-1, Math.Min(1, Vec3.Dot(a, b)));
        return Math.Acos(c) * 180.0 / Math.PI;
    }

    // Algebraic least-squares sphere: |x|^2 + D.x + E = 0.
    // Closest point to p on triangle abc (Ericson, Real-Time Collision Detection 5.1.5).
    public static Vec3 ClosestOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        Vec3 ab = b - a, ac = c - a, ap = p - a;
        double d1 = Vec3.Dot(ab, ap), d2 = Vec3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vec3 bp = p - b;
        double d3 = Vec3.Dot(ab, bp), d4 = Vec3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vec3 cp = p - c;
        double d5 = Vec3.Dot(ab, cp), d6 = Vec3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        double den = 1 / (va + vb + vc);
        return a + ab * (vb * den) + ac * (vc * den);
    }

    // Closest point to p on segment ab.
    public static Vec3 ClosestOnSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        Vec3 ab = b - a;
        double l2 = ab.LengthSq;
        if (l2 <= 0) return a;
        double t = Math.Max(0, Math.Min(1, Vec3.Dot(p - a, ab) / l2));
        return a + ab * t;
    }

    public static bool FitSphere(IList<Vec3> pts, out Vec3 center, out double radius)
    {
        var A = new double[4, 4];
        var b = new double[4];
        foreach (var p in pts)
        {
            double[] row = { p.X, p.Y, p.Z, 1 };
            double rhs = -p.LengthSq;
            for (int i = 0; i < 4; i++)
            {
                b[i] += row[i] * rhs;
                for (int j = 0; j < 4; j++) A[i, j] += row[i] * row[j];
            }
        }
        var x = LinAlg.Solve(A, b);
        center = new Vec3(-x[0] / 2, -x[1] / 2, -x[2] / 2);
        double r2 = center.LengthSq - x[3];
        radius = r2 > 0 ? Math.Sqrt(r2) : double.NaN;
        return r2 > 0;
    }

    // Vertices of the films between regions a and b.
    public static List<Vec3> PairVertices(FoamMesh m, int a, int b)
    {
        var set = new HashSet<int>();
        foreach (var t in m.T)
            if ((t.Neg == a && t.Pos == b) || (t.Neg == b && t.Pos == a)) { set.Add(t.A); set.Add(t.B); set.Add(t.C); }
        return set.Select(i => m.V[i]).ToList();
    }

    public static List<KeyValuePair<int, int>> Pairs(FoamMesh m)
    {
        var s = new HashSet<long>(LongKey.Comparer);
        foreach (var t in m.T) s.Add(FoamMesh.EdgeKey(t.Neg, t.Pos));
        return s.Select(k => new KeyValuePair<int, int>((int)(k >> 32), (int)(k & 0xffffffff)))
                .OrderBy(p => p.Key).ThenBy(p => p.Value).ToList();
    }

    // OBJ with one group per film ("g r<neg>_<pos>") so region labels survive a round trip.
    public static string ToObj(FoamMesh m)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.Append("# bubbles ").Append(m.BubbleCount).Append('\n');
        foreach (var v in m.V)
            sb.Append("v ").Append(v.X.ToString("R", ci)).Append(' ').Append(v.Y.ToString("R", ci))
              .Append(' ').Append(v.Z.ToString("R", ci)).Append('\n');
        foreach (var grp in m.T.GroupBy(t => FoamMesh.EdgeKey(t.Neg, t.Pos)))
        {
            var first = grp.First();
            int a = Math.Min(first.Neg, first.Pos), b = Math.Max(first.Neg, first.Pos);
            sb.Append("g r").Append(a).Append('_').Append(b).Append('\n');
            foreach (var t in grp)
            {
                // Store each triangle oriented from the lower region to the higher one.
                if (t.Neg == a) sb.Append("f ").Append(t.A + 1).Append(' ').Append(t.B + 1).Append(' ').Append(t.C + 1).Append('\n');
                else sb.Append("f ").Append(t.A + 1).Append(' ').Append(t.C + 1).Append(' ').Append(t.B + 1).Append('\n');
            }
        }
        return sb.ToString();
    }

    public static FoamMesh FromObj(string text)
    {
        var m = new FoamMesh();
        var ci = CultureInfo.InvariantCulture;
        int a = 0, b = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0] == "#" && p.Length >= 3 && p[1] == "bubbles") m.BubbleCount = int.Parse(p[2], ci);
            else if (p[0] == "v") m.V.Add(new Vec3(double.Parse(p[1], ci), double.Parse(p[2], ci), double.Parse(p[3], ci)));
            else if (p[0] == "g" && p[1].StartsWith("r"))
            {
                var ab = p[1].Substring(1).Split('_');
                a = int.Parse(ab[0], ci); b = int.Parse(ab[1], ci);
                m.BubbleCount = Math.Max(m.BubbleCount, b);
            }
            else if (p[0] == "f")
                m.T.Add(new FoamTri(int.Parse(p[1].Split('/')[0], ci) - 1, int.Parse(p[2].Split('/')[0], ci) - 1,
                                    int.Parse(p[3].Split('/')[0], ci) - 1, a, b));
        }
        return m;
    }

    // One-paragraph health report, used by both the GH Info output and the harness.
    public static string Report(FoamSolver s)
    {
        var m = s.Mesh;
        List<int> nodes;
        var chains = BorderChains(m, out nodes);
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendFormat(ci, "step {0}: {1} verts, {2} tris, {3} bubbles, {4} borders, {5} nodes\n",
                        s.Steps, m.V.Count, m.T.Count, m.BubbleCount, chains.Count, nodes.Count);
        sb.AppendFormat(ci, "area {0:G6}, residual {1:G3}, max volume error {2:P3}\n", s.Area, s.Residual, s.MaxVolumeError);
        sb.Append("border angles (120°): ").Append(BorderAngles(m)).Append('\n');
        sb.Append("node angles (109.47°): ").Append(NodeAngles(m, 2)).Append('\n');
        if (m.T.Any(FoamMesh.IsSolid)) sb.Append("contact angles on the host (90°): ").Append(ContactAngles(m)).Append('\n');
        sb.Append("pressures:");
        for (int k = 1; k <= m.BubbleCount; k++) sb.AppendFormat(ci, " {0}:{1:G4}", k, s.Pressure[k]);
        return sb.ToString();
    }
}

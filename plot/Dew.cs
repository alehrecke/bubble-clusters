using System;
using System.Collections.Generic;
using System.Linq;

// Dew (Alex, 2026-10-20: the soap beads looked "cartoonish and 2 dimensional ... they need to look a bit more
// settled on the skin of the bubbles rather than sitting on top. Little bits of crisp glare but some of the body
// stippled"). The border beads as small 3D spheres under the cluster's own light:
//   settled   each bead is sunk into the skin along the skin normal (about a third of its radius) and drawn per
//             point against the real surface depth, so the part under the film stays hidden: you see where the
//             bead meets the skin instead of a whole circle
//   body      stippled like the skin: Lambert from the same light, a darker film rim, nearly clear toward the light
//   glare     crisp marks only: a tiny highlight curve and a short reflection arc on the lit side
//   shadow    a stippled contact shadow on the skin beside each bead, away from the light
// The Mist stipple is cleared only where a bead surface is actually in front.
public static partial class Edition
{
    // Reflections (Reflect.cs, 2026-10-21): -refl = window + dark floor on the beads; -refl-body = also windows on
    // the bubbles; -refl-full = also the dark floor on the bubbles; mist-refl = the bubbles' reflections, no beads.
    static Plot DewOverview(FoamSolver s, View v, Plot p, Scene sc, bool dense, Random rng, string kind = "mist-dew")
    {
        bool beadsOn = kind != "mist-refl", beadRefl = kind.Contains("-refl") || kind == "mist-refl";
        bool bodyWin = kind.EndsWith("-refl-body") || kind.EndsWith("-refl-full") || kind == "mist-refl";
        bool bodyFloor = kind.EndsWith("-refl-full") || kind == "mist-refl";
        var env = new Env(sc);
        var bs = new BeadSettings { NodeRadius = dense ? 0.2 : 0.15, Decay = dense ? 0.36 : 0.28, BorderChance = kind.EndsWith("-nosil") ? 0.6 : 0.95, NodeClump = dense ? 12 : 8, Overlap = 1.1,
                                   Irregular = kind.EndsWith("-nosil") ? 0.8 : 0 };
        List<KeyValuePair<V2, double>> discs; List<KeyValuePair<Vec3, double>> placed;
        Beads(s, sc, bs, rng, true, out discs, out placed);

        // Round 2 (Alex, 2026-10-25: the larger beads "overlap with themselves in a way that flattens the image ... feel
        // a little too unreal"): beads no longer overlap each other (Overlap 1.1: dew drops would merge), sizes are
        // capped at 0.11 x the mean bubble radius, they sink deeper (0.42 r), and each gets a dark contact rim where it
        // meets the film, so it reads as sitting in the surface rather than on top of it.
        var vols = s.Mesh.Volumes();
        double rMeanB = Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => vols[k] > 0).Average(k => Math.Pow(3 * vols[k] / (4 * Math.PI), 1.0 / 3));
        double capR = 0.11 * rMeanB;
        // Sink each bead into the skin along the skin normal under its centre.
        var beads = new List<KeyValuePair<Vec3, double>>();
        foreach (var b in placed)
        {
            var q = sc.Project(b.Key);
            int si; Vec3 pos, n;
            var c = b.Key;
            if (sc.Query(q, out si, out pos, out n) && Math.Abs(sc.Depth(pos) - sc.Depth(b.Key)) < 2 * b.Value)
                c = pos - n * (0.42 * Math.Min(b.Value, capR));
            beads.Add(new KeyValuePair<Vec3, double>(c, Math.Min(b.Value, capR)));
        }
        if (!beadsOn) beads.Clear();
        var proj = beads.Select(b => sc.Project(b.Key)).ToList();
        Func<V2, Tuple<int, double, Vec3>> frontRef = null;
        var rad = beads.Select(b => b.Value * sc.S).ToList();
        double cc = Math.Max(1.0, rad.Count > 0 ? 3.2 * rad.Max() : 1);
        var g = new Dictionary<long, List<int>>(LongKey.Comparer);
        Func<double, double, long> ck = (x, y) => unchecked(((long)Math.Floor(x / cc) * 73856093L) ^ ((long)Math.Floor(y / cc) * 19349663L));
        for (int i = 0; i < beads.Count; i++)
        {
            long k = ck(proj[i].X, proj[i].Y);
            List<int> l; if (!g.TryGetValue(k, out l)) g[k] = l = new List<int>(); l.Add(i);
        }
        Func<V2, IEnumerable<int>> near = q =>
        {
            var res = new List<int>();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<int> l; if (g.TryGetValue(ck(q.X + dx * cc, q.Y + dy * cc), out l)) res.AddRange(l);
                }
            return res;
        };
        // Skin depth at a sheet point (double.MaxValue off the surface).
        Func<V2, double> skinDepth = q =>
        {
            int si; Vec3 pos, n;
            return sc.Query(q, out si, out pos, out n) ? sc.Depth(pos) : double.MaxValue;
        };
        // The bead surface in front at q: index, depth and normal (or -1).
        Func<V2, Vec3> unused = null;
        Func<V2, Tuple<int, double, Vec3>> front = q =>
        {
            int best = -1; double bd = double.MaxValue; Vec3 bn = Vec3.Zero;
            foreach (int i in near(q))
            {
                var d2 = q - proj[i]; double R = rad[i];
                double l2 = d2.X * d2.X + d2.Y * d2.Y;
                if (l2 >= R * R) continue;
                double a = d2.X / R, b = -d2.Y / R, w = -Math.Sqrt(Math.Max(0, 1 - a * a - b * b));
                double depth = sc.Depth(beads[i].Key) + w * beads[i].Value;
                if (depth < bd) { bd = depth; best = i; bn = sc.R * a + sc.U * b + sc.F * w; }
            }
            if (best < 0) return Tuple.Create(-1, 0.0, Vec3.Zero);
            if (skinDepth(q) < bd - 0.15 * beads[best].Value) return Tuple.Create(-1, 0.0, Vec3.Zero);   // under the film
            return Tuple.Create(best, bd, bn);
        };

        var light = sc.Light;
        var half = (light - sc.F).Normalized();                          // half vector (view direction is -F)
        var l2d = new V2(Vec3.Dot(light, sc.R), -Vec3.Dot(light, sc.U)).Normalized();

        // Which beads show at all (their centre point is a visible bead surface): only those cast contact shadows
        // and get glare (round 2: hidden beads behind the skin were casting shadows onto the front surface).
        var shows = new bool[beads.Count];
        for (int i = 0; i < beads.Count; i++) shows[i] = front(proj[i]).Item1 == i;

        // Clear the Mist stipple where a bead is in front.
        foreach (var name in new[] { "hatch", "inner", "innerborders" })
        {
            if (!p.Layers.ContainsKey(name)) continue;
            var kept = p.Layers[name].Where(d => d.Count != 2 || front((d[0] + d[1]) * 0.5).Item1 < 0).ToList();
            p.Layers[name].Clear(); p.Layers[name].AddRange(kept);
        }

        // Stipple: bead bodies + contact shadows, one Poisson pass over the bead neighbourhoods.
        var ms = new MarkSettings { DotMin = 0.24, DotMax = 2.6, DotGamma = 1.5, DotLength = 0.16 };
        var dots = new List<List<V2>>();
        double cell = ms.DotMin * 0.7;
        var dg = new Dictionary<long, List<V2>>(LongKey.Comparer);
        Func<long, long, long> key = (x, y) => unchecked((x * 73856093L) ^ (y * 19349663L));
        double area = rad.Sum(R => Math.PI * 2.6 * R * R);
        int tries = (int)(area / (ms.DotMin * ms.DotMin) * 4);
        var cum = new double[beads.Count]; double acc = 0;
        for (int i = 0; i < beads.Count; i++) { acc += rad[i] * rad[i]; cum[i] = acc; }
        for (int t = 0; t < tries && beads.Count > 0; t++)
        {
            int bi = Array.BinarySearch(cum, rng.NextDouble() * acc); if (bi < 0) bi = ~bi; bi = Math.Min(bi, beads.Count - 1);
            double ang = rng.NextDouble() * 2 * Math.PI, rr = 1.6 * rad[bi] * Math.Sqrt(rng.NextDouble());
            var q = proj[bi] + new V2(Math.Cos(ang), Math.Sin(ang)) * rr;
            double tone;
            var f = front(q);
            if (f.Item1 >= 0)
            {
                if (f.Item1 != bi) continue;                               // sampled once, from its own bead
                var n = f.Item3;
                double lam = Math.Max(0, Vec3.Dot(n, light));
                double spec = Math.Pow(Math.Max(0, Vec3.Dot(n, half)), 30);
                double dark = 0;
                if (beadRefl)
                {
                    var rv = env.Reflect(n);
                    if (env.Window(rv) < 1) continue;                      // the window: clean paper
                    dark = env.Dark(rv);                                   // the floor: darker
                }
                else if (spec > 0.35) continue;                            // glare: clean paper
                double rim = Math.Pow(1 - Math.Abs(Vec3.Dot(n, sc.F)), 3);
                // Contact rim: where the film lies just behind the bead surface, the bead meets the skin.
                double behind = skinDepth(q) - f.Item2;
                double contact = behind < 0.3 * beads[bi].Value ? 0.55 * (1 - Math.Max(0, behind) / (0.3 * beads[bi].Value)) : 0;
                tone = Math.Min(1, 0.85 * (1 - lam) + 0.45 * rim + 0.45 * dark + contact);
                tone = Math.Max(0, (tone - 0.12) / 0.88);
            }
            else
            {
                // Contact shadow on the skin, on the side away from the light, fading with distance.
                if (!shows[bi] || skinDepth(q) == double.MaxValue) continue;
                var d = q - proj[bi]; double dl = d.Length, R = rad[bi];
                if (dl < R || dl > 1.6 * R) continue;
                double away = -(d.X * l2d.X + d.Y * l2d.Y) / dl;
                if (away < 0.1) continue;
                tone = 0.75 * away * (1 - (dl - R) / (0.6 * R));
            }
            if (tone < 0.03) continue;
            double r = Math.Min(ms.DotMax, ms.DotMin / Math.Pow(tone, ms.DotGamma));
            if (r >= ms.DotMax) continue;
            long cx = (long)Math.Floor(q.X / cell), cy = (long)Math.Floor(q.Y / cell);
            int rq = (int)Math.Ceiling(r / cell);
            bool ok = true;
            for (long gy = cy - rq; gy <= cy + rq && ok; gy++)
                for (long gx = cx - rq; gx <= cx + rq && ok; gx++)
                {
                    List<V2> l; if (!dg.TryGetValue(key(gx, gy), out l)) continue;
                    foreach (var o in l) if ((o - q).Length < r) { ok = false; break; }
                }
            if (!ok) continue;
            List<V2> cl; long kk = key(cx, cy);
            if (!dg.TryGetValue(kk, out cl)) dg[kk] = cl = new List<V2>(2);
            cl.Add(q);
            double a2 = rng.NextDouble() * Math.PI, len = ms.DotLength * (0.7 + 0.6 * rng.NextDouble());
            var o2 = new V2(Math.Cos(a2), Math.Sin(a2)) * (len / 2);
            dots.Add(new List<V2> { q - o2, q + o2 });
        }
        p.Layer("beads").AddRange(dots);

        // Crisp glare: a tiny highlight curve around the specular point, and a short reflection arc inside the rim
        // on the lit side. Each mark only where its bead is the visible surface.
        var glare = new List<List<V2>>();
        for (int i = 0; i < beads.Count; i++)
        {
            // Round 2: tiny glints (the round-1 rings read as little letter Cs); the reflection arc only on big beads.
            double R = rad[i];
            if (beadRefl)
            {
                if (R < 0.3 || !shows[i]) continue;
                foreach (var mark in BeadReflectionMarks(sc, env, proj[i], R))
                    glare.AddRange(Runs(mark.Count, k => front(mark[k]).Item1 == i, k => mark[k]));
                continue;
            }
            if (R < 0.45 || !shows[i]) continue;
            var hp = proj[i] + new V2(Vec3.Dot(half, sc.R), -Vec3.Dot(half, sc.U)) * (0.92 * R);
            double hr = Math.Max(0.08, 0.09 * R);
            double ca = Math.Atan2(l2d.Y, l2d.X);
            var spot = new List<V2>();
            for (int k = 0; k <= 6; k++) { double a = ca + Math.PI * 0.6 + Math.PI * 0.8 * k / 6; spot.Add(hp + new V2(Math.Cos(a), Math.Sin(a)) * hr); }
            var marks = new List<List<V2>> { spot };
            if (R > 1.0)
            {
                var arc = new List<V2>();
                for (int k = 0; k <= 8; k++) { double a = ca + Math.PI - 0.35 + 0.7 * k / 8; arc.Add(proj[i] + new V2(Math.Cos(a), Math.Sin(a)) * (0.78 * R)); }
                marks.Add(arc);
            }
            foreach (var mark in marks)
                glare.AddRange(Runs(mark.Count, k => front(mark[k]).Item1 == i, k => mark[k]));
        }
        p.Layer("beads").AddRange(glare);
        if (bodyWin) BodyReflections(p, sc, env, bodyFloor, rng, q => front(q).Item1 >= 0);
        return p;
    }
}

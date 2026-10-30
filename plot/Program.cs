using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

// bubbleplot: headless pen-plot editions of bubble clusters.
//
//   bubbleplot preview --seed 4721 [--style short|long|both] [--field light|facet|border|all] [--bubble K]
//       the overview + one specimen + one context sheet (quick look)
//   bubbleplot edition --seed 4721 [--style short] [--field light] [--paper a4|a3] [--count N] [--detail B]
//       everything: overview, a specimen and a context sheet for every bubble, manifest.json
//       --paper a3: the bubble sheets come 4-up on A3 pages (each tile an A5 sheet), the overview as one A3 sheet
//       --tiles 9: 9-up instead (3x3 tiles, 99x140 mm);  --density f: hatching f times denser (any command)
//       --context visible|all|dash: context sheets show the cluster's front borders only (default), every border, or
//       every border with the back ones dashed
//   bubbleplot overviews --seeds 11,12,13,14 [--style short] [--field light]
//       four clusters' overviews 4-up on one A3 page
//
// Output: editions/<seed>/ next to this folder (git-ignored). Each sheet is an SVG with pen layers
// (hatch, outline, context, marks) plus a PNG preview on cream paper.
public static class Program
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    const double A3W = 297, A3H = 420;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: bubbleplot preview|edition --seed N [--style short|long|both] [--field light|facet|border|all] [--paper a4|a3] [--swirl 0..1] [--hidden solid|dash] [--light classic|dramatic] [--outline on|off] [--bubble K] [--count N] [--detail B]");
            Console.WriteLine("       bubbleplot overviews --seeds a,b,c,d [--style short] [--field light]");
            return 1;
        }
        string cmd = args[0];
        Func<string, string, string> opt = (name, def) => { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; };
        // Defaults = Alex's settings (2026-10-19): A3, 9-up bubble pages, mixed strokes, border field, dramatic light,
        // density 1.5, context sheets with the whole border network. Override any of them with the flag.
        string style = opt("style", "mixed");
        string field = opt("field", "border");
        bool a3 = opt("paper", "a3") == "a3";
        // --tiles 4|9: bubble sheets per A3 page (2x2 A5 tiles or 3x3 tiles of 99x140 mm).
        int perSide = opt("tiles", "9") == "9" ? 3 : 2, perPage = perSide * perSide;
        // --density f: hatching lines f times closer (all spacings / f); darkest spacing floored at the pen width.
        double density = double.Parse(opt("density", "1.5"), CI);
        Func<HatchStyle, HatchStyle> dense = h =>
        {
            if (density == 1) return h;
            h.SepMin = Math.Max(0.3, h.SepMin / density); h.SepMax /= density;
            h.HyperMin = Math.Max(0.3, h.HyperMin / density); h.HyperMax /= density;
            return h;
        };
        var styles = style == "both" ? new[] { "short", "long" } : style == "all" ? new[] { "short", "long", "mixed" } : new[] { style };
        string light = opt("light", "dramatic");
        bool outline = opt("outline", "on") != "off";
        var fields = field == "all" ? new[] { "light", "facet", "border" } : new[] { field };
        double swirl = double.Parse(opt("swirl", "0"), CI);
        string hidden = opt("hidden", null);
        string extra = (swirl > 0 ? "-swirl" + (int)Math.Round(swirl * 100) : "") + (hidden != null ? "-hidden" + (hidden == "dash" ? "dash" : "") : "")
                     + (light == "dramatic" ? "-dramatic" : "") + (outline ? "" : "-nooutline")
                     + (opt("density", "1.5") != "1.5" ? "-dense" + opt("density", "1.5") : "")
                     + (opt("context", "all") != "all" ? "-ctx" + opt("context", "all") : "");
        var variants = (from st in styles from f in fields select new { Style = st, Field = f, Tag = (f == "light" ? st : st + "-" + f) + extra }).ToList();

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "editions"));
        if (opt("out", null) != null) root = Path.GetFullPath(opt("out", null));
        var sw = Stopwatch.StartNew();
        Action<string> log = m => Console.WriteLine(string.Format(CI, "[{0,5:F1} s] {1}", sw.Elapsed.TotalSeconds, m));
        Func<int, EditionSettings> settings = seed => new EditionSettings
        {
            Seed = seed, Count = int.Parse(opt("count", "0")), DetailBatches = int.Parse(opt("detail", "0")),
            Light = light, Outline = outline, ContextLines = opt("context", "all"),
            RMin = double.Parse(opt("rmin", "0.3"), CI), RMax = double.Parse(opt("rmax", "1.9"), CI),
        };

        if (cmd == "overviews")
        {
            var seeds = opt("seeds", "1,2,3,4").Split(',').Select(x => int.Parse(x.Trim(), CI)).ToList();
            string dir0 = Path.Combine(root, "overviews_" + string.Join("-", seeds));
            Directory.CreateDirectory(dir0);
            var foams = seeds.Select(sd => { var e0 = settings(sd); return new { e = e0, s = Edition.Generate(e0, dir0, log) }; }).ToList();
            foreach (var va in variants)
            {
                var hs = dense(new HatchStyle { Short = va.Style == "short", Mixed = va.Style == "mixed", Hyper = light == "dramatic", Field = va.Field, Swirl = swirl, Hidden = hidden });
                var tiles = new List<Plot>();
                foreach (var f in foams)
                {
                    f.e.SheetW = A3W / 2; f.e.SheetH = A3H / 2;
                    tiles.Add(Edition.Overview(f.s, Edition.MakeView(f.e.Seed), hs, f.e));
                }
                var page = Edition.Tile(tiles, A3W, A3H, 2, 2);
                var info = Edition.Write(page, dir0, "a3_overviews_" + va.Tag, "Bubble clusters " + string.Join(", ", seeds) + " (" + va.Tag + ")", foams[0].e);
                log("A3 page (" + va.Tag + "): " + info["strokes"] + " strokes, ~" + info["estMinutes"] + " min");
            }
            log("done: " + dir0);
            return 0;
        }

        if (cmd == "reframe")
        {
            // Re-render saved gallery clusters with the closer composition (Reframe.cs): reframe --dir <gallery dir>
            // [--pages 4,8] [--marks glass-pure] [--lens chain,branch,ring]
            string gd = Path.GetFullPath(opt("dir", ""));
            var specs = File.ReadAllLines(Path.Combine(gd, "clusters.txt")).Where(l => l.Contains(":")).ToList();
            var pages = opt("pages", "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
            if (pages.Count == 0) pages = Enumerable.Range(1, (specs.Count + 3) / 4).ToList();
            string kind = MarkSettings.Resolve(opt("marks", "glass-pure"));
            var lensShapes = opt("lens", "chain,branch,ring").Split(',');
            foreach (int pg in pages)
            {
                var tiles = new List<Plot>();
                var seeds = new List<int>();
                foreach (var line in specs.Skip((pg - 1) * 4).Take(4))
                {
                    int seed = int.Parse(line.Split(':')[0], CI);
                    string shape = line.Split(':')[1].Split(',')[0].Trim();
                    var ge = settings(seed); ge.SheetW = A3W / 2; ge.SheetH = A3H / 2;
                    var hs = dense(new HatchStyle { Short = style == "short", Mixed = style == "mixed", Hyper = light == "dramatic", Field = field, Swirl = swirl, Hidden = hidden });
                    bool lens = lensShapes.Contains(shape);
                    var cells = System.Text.RegularExpressions.Regex.Match(line, @"(\d+) bubbles").Groups[1].Value;
                    var ms = new MarkSettings { Kind = kind, Tag = (shape != "ball" ? shape.ToUpperInvariant() + "  " : "") + cells + " CELLS" };
                    tiles.Add(Edition.ReframedTile(Path.Combine(gd, "cluster_" + seed + ".obj"), shape, seed, hs, ge, ms, lens));
                    seeds.Add(seed);
                    log("reframed " + seed + " (" + shape + (lens ? ", lens" : "") + ")");
                }
                var pe = settings(seeds[0]); pe.SheetW = A3W; pe.SheetH = A3H;
                var page = Edition.Tile(tiles, A3W, A3H, 2, 2);
                // Same names as the gallery pages: the reframed composition is the default since 2026-10-29.
                string pname = string.Format(CI, "a3_gallery_{0}_p{1:00}_seeds{2}-{3}", kind, pg, seeds.First(), seeds.Last());
                var pi = Edition.Write(page, gd, pname, "Bubble clusters reframed, page " + pg, pe);
                log("page " + pg + ": " + pi["strokes"] + " strokes, ~" + pi["estMinutes"] + " min");
            }
            return 0;
        }

        if (cmd == "anim")
        {
            // Foam forming, then cells dropped as rigid stones; forward + reverse loop as MP4 + GIF (Anim.cs).
            int seed = int.Parse(opt("seed", "8801"));
            Edition.Animate(seed, int.Parse(opt("count", "40")), double.Parse(opt("az", "35"), CI), double.Parse(opt("el", "20"), CI),
                            int.Parse(opt("fps", "24")), Path.Combine(root, "anim_" + seed + (opt("style", "lines") == "lines" ? "" : "_" + opt("style", "lines"))), log,
                            opt("style", "lines"), int.Parse(opt("from", "-1")), int.Parse(opt("to", "-1")));
            return 0;
        }
        if (cmd == "rafts")
        {
            // Bubble rafts floating on a water plane (Raft.cs), one per A3 quadrant: rafts --start N [--n 8] [--marks glass-pure]
            int start = int.Parse(opt("start", "7201")), nRuns = int.Parse(opt("n", "8"));
            string kind = MarkSettings.Resolve(opt("marks", "glass-pure"));
            string rdir = Path.Combine(root, "rafts_" + kind + "_" + start + "-" + (start + nRuns - 1));
            Directory.CreateDirectory(rdir);
            var tiles = new List<Plot>(); var specs = new List<string>();
            for (int i = 0; i < nRuns; i++)
            {
                int seed = start + i;
                var prng = new Random(seed * 101 + 13);
                var re = settings(seed);
                re.Count = (int)Math.Round(Math.Exp(Math.Log(14) + prng.NextDouble() * (Math.Log(48) - Math.Log(14))));
                re.RMin = 0.2 + 0.15 * prng.NextDouble();
                re.RMax = re.RMin * (2.5 + 4 * prng.NextDouble());
                re.SizeSkew = 1.2 + 2 * prng.NextDouble();
                re.TriangleBudget = 120000;
                var rs = Edition.GenerateRaft(re, log);
                File.WriteAllText(Path.Combine(rdir, "raft_" + seed + ".obj"), FoamAnalysis.ToObj(rs.Mesh));
                re.SheetW = A3W / 2; re.SheetH = A3H / 2;
                var hs = dense(new HatchStyle { Short = style == "short", Mixed = style == "mixed", Hyper = light == "dramatic", Field = field, Swirl = swirl, Hidden = hidden });
                var ms = new MarkSettings { Kind = kind, Tag = "RAFT  " + re.Count + " CELLS" };
                tiles.Add(Edition.RaftSheet(rs, Edition.RaftView(seed), hs, re, ms));
                specs.Add(string.Format(CI, "{0}: raft, {1} bubbles, r {2:F2}..{3:F2}, skew {4:F1}", seed, re.Count, re.RMin, re.RMax, re.SizeSkew));
                log("raft " + (i + 1) + "/" + nRuns + ": " + specs[specs.Count - 1]);
            }
            var pe = settings(start); pe.SheetW = A3W; pe.SheetH = A3H;
            for (int pg = 0; pg * 4 < tiles.Count; pg++)
            {
                var page = Edition.Tile(tiles.Skip(pg * 4).Take(4).ToList(), A3W, A3H, 2, 2);
                string pname = string.Format(CI, "a3_rafts_{0}_p{1:00}_seeds{2}-{3}", kind, pg + 1, start + pg * 4, Math.Min(start + pg * 4 + 3, start + nRuns - 1));
                var pi = Edition.Write(page, rdir, pname, "Bubble rafts (" + MarkSettings.NameOf(kind) + "), page " + (pg + 1), pe);
                log("page " + (pg + 1) + ": " + pi["strokes"] + " strokes, ~" + pi["estMinutes"] + " min");
            }
            File.WriteAllLines(Path.Combine(rdir, "rafts.txt"), specs);
            log("done: " + rdir);
            return 0;
        }

        if (cmd == "gallery")
        {
            // Many different clusters in one mark style, one per A3 quadrant (Alex, 2026-10-20: "a range of different
            // clusters with a variety of cells and sizes"). Per seed: bubble count 8..40 (log-uniform), radius range and
            // size skew drawn from the seed, so the set runs from a few big cells to dense mixed foams.
            //   gallery --start N [--n 16] [--marks glass-pure]
            int start = int.Parse(opt("start", "7001")), nRuns = int.Parse(opt("n", "16"));
            string kind = MarkSettings.Resolve(opt("marks", "glass-pure"));
            // --shapes chain,ring,raft,branch,buds,ball: cycled through the runs (Shapes.cs); default all round balls.
            var shapes = opt("shapes", "ball").Split(',').Select(x => x.Trim().ToLowerInvariant()).ToArray();
            string gdir = Path.Combine(root, "gallery_" + kind + (shapes.Length > 1 || shapes[0] != "ball" ? "_shapes" : "") + "_" + start + "-" + (start + nRuns - 1));
            Directory.CreateDirectory(gdir);
            var tiles = new List<Plot>();
            var specs = new List<string>();
            for (int i = 0; i < nRuns; i++)
            {
                int seed = start + i;
                var prng = new Random(seed * 101 + 13);
                var ge = settings(seed);
                ge.Count = (int)Math.Round(Math.Exp(Math.Log(8) + prng.NextDouble() * (Math.Log(40) - Math.Log(8))));
                ge.RMin = 0.18 + 0.22 * prng.NextDouble();
                ge.RMax = ge.RMin * (2.5 + 5 * prng.NextDouble());
                ge.SizeSkew = 1.0 + 2.4 * prng.NextDouble();
                ge.Shape = shapes[i % shapes.Length];
                // Satellites: --satellites 0..1 = chance per cluster (default 0.6), 1-5 of them.
                if (prng.NextDouble() < double.Parse(opt("satellites", "0.8"), CI)) ge.Satellites = 2 + prng.Next(6);
                if (ge.Shape == "chain" || ge.Shape == "branch") ge.Count = Math.Max(ge.Count, 16);
                // Rings (round 3): more, more even bubbles, so the loop stays open instead of collapsing to a clump.
                if (ge.Shape == "ring") { ge.Count = Math.Max(ge.Count, 24); ge.RMax = Math.Min(ge.RMax, 2.0 * ge.RMin); ge.SizeSkew = 1.0; }
                if (ge.Shape == "raft" || ge.Shape == "buds") ge.Count = Math.Max(ge.Count, 12);
                ge.TriangleBudget = ge.Count > 25 ? 120000 : 90000;
                var gs = Edition.Generate(ge, gdir, log);
                File.WriteAllText(Path.Combine(gdir, "cluster_" + seed + ".obj"), FoamAnalysis.ToObj(gs.Mesh));
                ge.SheetW = A3W / 2; ge.SheetH = A3H / 2;
                var hs = dense(new HatchStyle { Short = style == "short", Mixed = style == "mixed", Hyper = light == "dramatic", Field = field, Swirl = swirl, Hidden = hidden });
                var ms = new MarkSettings { Kind = kind, Tag = (ge.Shape != "ball" ? ge.Shape.ToUpperInvariant() + "  " : "") + ge.Count + " CELLS" + (ge.Satellites > 0 ? " +" + ge.Satellites + " SAT" : "") + (ge.Variant > 0 ? "  V" + ge.Variant : "") };
                // Default composition (2026-10-29): the reframed one (Reframe.cs), from the saved cluster.
                if (args.Contains("--wide")) tiles.Add(Edition.MarkOverview(gs, Edition.ShapeView(gs, ge.Shape, seed), hs, ge, ms));
                else tiles.Add(Edition.ReframedTile(Path.Combine(gdir, "cluster_" + seed + ".obj"), ge.Shape, seed, hs, ge, ms, ge.Shape == "chain" || ge.Shape == "branch" || ge.Shape == "ring"));
                specs.Add(string.Format(CI, "{0}: " + ge.Shape + ", {1} bubbles, r {2:F2}..{3:F2}, skew {4:F1}, variant {5}", seed, ge.Count, ge.RMin, ge.RMax, ge.SizeSkew, ge.Variant));
                log("gallery " + (i + 1) + "/" + nRuns + ": " + specs[specs.Count - 1]);
            }
            var pe = settings(start); pe.SheetW = A3W; pe.SheetH = A3H;
            for (int pg = 0; pg * 4 < tiles.Count; pg++)
            {
                var page = Edition.Tile(tiles.Skip(pg * 4).Take(4).ToList(), A3W, A3H, 2, 2);
                string pname = string.Format(CI, "a3_gallery_{0}_p{1:00}_seeds{2}-{3}", kind, pg + 1, start + pg * 4, Math.Min(start + pg * 4 + 3, start + nRuns - 1));
                var pi = Edition.Write(page, gdir, pname, "Bubble clusters (" + MarkSettings.NameOf(kind) + "), page " + (pg + 1), pe);
                log("page " + (pg + 1) + ": " + pi["strokes"] + " strokes, ~" + pi["estMinutes"] + " min");
            }
            File.WriteAllLines(Path.Combine(gdir, "clusters.txt"), specs);
            log("done: " + gdir);
            return 0;
        }

        var e = settings(int.Parse(opt("seed", "1")));
        if (cmd == "bed")
        {
            // Foam bed on the A3 sheet, seen from below through the plane (Bed.cs).
            //   bed --seed N [--count 120] [--layers 3] [--bed mist,lattice] [--reuse: replot the saved foam]
            e.SheetW = A3W; e.SheetH = A3H;
            var b = new BedSettings { Count = int.Parse(opt("count", "120")), Layers = double.Parse(opt("layers", "3"), CI) };
            string bdir = Path.Combine(root, "bed_" + e.Seed.ToString(CI));
            Directory.CreateDirectory(bdir);
            string bobj = Path.Combine(bdir, "bed.obj"), bwin = Path.Combine(bdir, "window.txt");
            FoamSolver bs;
            if (args.Contains("--reuse") && File.Exists(bobj) && File.Exists(bwin))
            {
                var wh = File.ReadAllText(bwin).Split(' ');
                b.WinW = double.Parse(wh[0], CI); b.WinH = double.Parse(wh[1], CI);
                bs = new FoamSolver(FoamAnalysis.FromObj(File.ReadAllText(bobj)), new SolverSettings()) { Host = new PlaneHost(Vec3.Zero, new Vec3(0, 0, 1)) };
                log("reusing " + bobj);
            }
            else
            {
                bs = Edition.GenerateBed(e, b, log);
                File.WriteAllText(bobj, FoamAnalysis.ToObj(bs.Mesh));
                File.WriteAllText(bwin, b.WinW.ToString("R", CI) + " " + b.WinH.ToString("R", CI));
            }
            foreach (var kind in opt("bed", "mist,lattice").Split(',').Select(k => k.Trim().ToLowerInvariant()))
            {
                var hs = dense(new HatchStyle { Short = style == "short", Mixed = style == "mixed", Hyper = light == "dramatic", Field = field, Swirl = swirl, Hidden = hidden });
                var bp = Edition.BedSheet(bs, b, kind, hs, e, e.Seed);
                var bi = Edition.Write(bp, bdir, "a3_bed_" + kind, "Foam bed " + e.Seed + " (" + kind + ")", e);
                log("bed " + kind + ": " + bi["strokes"] + " strokes, ~" + bi["estMinutes"] + " min");
            }
            log("done: " + bdir);
            return 0;
        }
        string dir = Path.Combine(root, e.Seed.ToString(CI));
        FoamSolver s;
        int featured = -1;
        if (cmd == "obj")
        {
            // bubbleplot obj <foam.obj> [--seed N for the view] : plot a saved foam (harness / GH run mesh) instead of generating one.
            // Previews the overview + the interior bubble with the most faces (a whole cell, e.g. a Kelvin cell).
            string objPath = Path.GetFullPath(args[1]);
            dir = Path.Combine(root, "obj_" + Path.GetFileNameWithoutExtension(objPath));
            Directory.CreateDirectory(dir);
            s = new FoamSolver(FoamAnalysis.FromObj(File.ReadAllText(objPath)), new SolverSettings());
            featured = MostFacedInterior(s.Mesh);
            log("loaded " + objPath + ": " + s.Mesh.BubbleCount + " bubbles, " + s.Mesh.T.Count + " triangles; featured bubble " + featured);
            cmd = "preview";
        }
        else if (opt("obj", null) != null)
        {
            // --obj <foam.obj>: use a saved, settled cluster (e.g. a gallery cluster) instead of generating one.
            string op = Path.GetFullPath(opt("obj", null));
            s = new FoamSolver(FoamAnalysis.FromObj(File.ReadAllText(op)), new SolverSettings());
            log("loaded " + op + ": " + s.Mesh.BubbleCount + " bubbles");
        }
        else
        {
            Directory.CreateDirectory(dir);
            s = Edition.Generate(e, dir, log);
        }
        var view = Edition.MakeView(e.Seed, double.Parse(opt("az", "NaN"), CI), double.Parse(opt("el", "NaN"), CI));
        if (cmd == "tone") { Edition.ToneImage(s, view, e, Path.Combine(dir, "tone_" + e.Light + ".png")); log("tone image written"); return 0; }
        if (cmd == "marks")
        {
            // Mark-making tests: one A3 overview per kind (Marks.cs). --marks stipple,cross,natural,glass
            // [--wobble mm] [--breaks per-mm] [--dotmin mm]
            string mdir = Path.Combine(root, "marks_" + e.Seed.ToString(CI));
            if (args.Contains("--quad"))
            {
                // Four kinds on one A3 page (A5 quadrants, left to right, top to bottom) to plot side by side.
                var kinds = opt("marks", "glass-pure,cross,inkdrift,wild-bg").Split(',').Select(MarkSettings.Resolve).Take(4).ToList();
                e.SheetW = A3W / 2; e.SheetH = A3H / 2;
                var tiles = new List<Plot>();
                foreach (var kind in kinds)
                {
                    var hs = dense(new HatchStyle { Short = style == "short", Mixed = style == "mixed", Hyper = light == "dramatic", Field = field, Swirl = swirl, Hidden = hidden });
                    var ms = new MarkSettings { Kind = kind, Wobble = double.Parse(opt("wobble", "-1"), CI), Tag = opt("round", "") };
                    var tp = Edition.MarkOverview(s, view, hs, e, ms);
                    log("quadrant " + kind + ": " + tp.Strokes() + " strokes");
                    tiles.Add(tp);
                }
                e.SheetW = A3W; e.SheetH = A3H;
                var page = Edition.Tile(tiles, A3W, A3H, 2, 2);
                string qname = "a3_quad_" + string.Join("_", kinds) + opt("tag", "");
                var qi = Edition.Write(page, mdir, qname, "Bubble cluster " + e.Seed + " (" + string.Join(" | ", kinds) + ")", e);
                log("A3 quad page: " + qi["strokes"] + " strokes, ~" + qi["estMinutes"] + " min -> " + qname);
                File.WriteAllText(Path.Combine(mdir, "cluster.obj"), FoamAnalysis.ToObj(s.Mesh));
                return 0;
            }
            e.SheetW = A3W; e.SheetH = A3H;
            foreach (var kind in opt("marks", "stipple,cross,natural,glass").Split(','))
            {
                var hs = dense(new HatchStyle { Short = style == "short", Mixed = style == "mixed", Hyper = light == "dramatic", Field = field, Swirl = swirl, Hidden = hidden });
                var ms = new MarkSettings
                {
                    Kind = MarkSettings.Resolve(kind), Tag = opt("round", ""), Wobble = double.Parse(opt("wobble", "-1"), CI),
                    Breaks = double.Parse(opt("breaks", "0.04"), CI), DotMin = double.Parse(opt("dotmin", "0.42"), CI),
                };
                var mp = Edition.MarkOverview(s, view, hs, e, ms);
                var mi = Edition.Write(mp, mdir, "a3_mark_" + ms.Kind + (opt("wobble", null) != null ? "_wobble" + opt("wobble", "") : ""),
                                       "Bubble cluster " + e.Seed + " (marks: " + ms.Kind + ")", e);
                log("mark " + ms.Kind + ": " + mi["strokes"] + " strokes, ~" + mi["estMinutes"] + " min");
            }
            File.WriteAllText(Path.Combine(mdir, "cluster.obj"), FoamAnalysis.ToObj(s.Mesh));
            log("done: " + mdir);
            return 0;
        }
        var vol = s.Mesh.Volumes();
        var bubbles = Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => !s.IsDead(k) && vol[k] > 0).ToList();
        var manifest = new Dictionary<string, object>
        {
            { "seed", e.Seed }, { "bubbles", bubbles.Count },
            { "radii", bubbles.Select(k => Math.Round(s.DesiredRadius(k), 3)).ToList() },
            { "view", new Dictionary<string, object> { { "azimuthDeg", Math.Round(view.Azimuth * 180 / Math.PI, 1) }, { "elevationDeg", Math.Round(view.Elevation * 180 / Math.PI, 1) } } },
            { "sheet", a3 ? "A3 portrait 297x420 mm: overview full sheet, bubble sheets " + perPage + "-up; pen 0.3 mm" : "A4 portrait 210x297 mm, pen 0.3 mm" },
            { "density", density }, { "variant", e.Variant },
        };
        var sheets = new List<object>();
        string prefix = a3 ? "a3_" : "";
        foreach (var va in variants)
        {
            var hs = dense(new HatchStyle { Short = va.Style == "short", Mixed = va.Style == "mixed", Hyper = light == "dramatic", Field = va.Field, Swirl = swirl, Hidden = hidden });
            if (a3) { e.SheetW = A3W; e.SheetH = A3H; } else { e.SheetW = 210; e.SheetH = 297; }
            var p = Edition.Overview(s, view, hs, e);
            var info = Edition.Write(p, dir, prefix + "overview_" + va.Tag, "Bubble cluster " + e.Seed + " (" + va.Tag + ")", e);
            info["kind"] = "overview"; info["style"] = va.Tag; sheets.Add(info);
            log("overview (" + va.Tag + "): " + info["strokes"] + " strokes, ~" + info["estMinutes"] + " min");

            var which = cmd == "preview"
                ? new List<int> { int.Parse(opt("bubble", (featured > 0 ? featured : bubbles.OrderByDescending(k => s.DesiredRadius(k)).Skip(1).First()).ToString(CI))) }
                : bubbles;
            if (a3) { e.SheetW = A3W / perSide; e.SheetH = A3H / perSide; }
            var spec = new List<Plot>(); var ctx = new List<Plot>(); var tags = new List<string>();
            int num = 0;
            foreach (int k in which)
            {
                num++;
                string tag = (bubbles.IndexOf(k) + 1).ToString("D2", CI);
                var sp = Edition.Specimen(s, view, k, hs, e);
                var cp = Edition.Context(s, view, k, hs, e);
                log(string.Format(CI, "bubble {0} ({1}/{2}, {3}): specimen {4} strokes, context {5} strokes", tag, num, which.Count, va.Tag, sp.Strokes(), cp.Strokes()));
                if (a3) { spec.Add(sp); ctx.Add(cp); tags.Add(tag); continue; }
                var si = Edition.Write(sp, Path.Combine(dir, "specimen_" + va.Tag), "bubble_" + tag,
                                       "Bubble " + tag + " of " + bubbles.Count + ", cluster " + e.Seed + " (specimen, " + va.Tag + ")", e);
                si["kind"] = "specimen"; si["style"] = va.Tag; si["bubble"] = k; sheets.Add(si);
                var ci = Edition.Write(cp, Path.Combine(dir, "context_" + va.Tag), "bubble_" + tag,
                                       "Bubble " + tag + " of " + bubbles.Count + ", cluster " + e.Seed + " (in context, " + va.Tag + ")", e);
                ci["kind"] = "context"; ci["style"] = va.Tag; ci["bubble"] = k; sheets.Add(ci);
            }
            if (a3)
            {
                // 4- or 9-up pages, bubbles in order: page 1 = the first bubbles (left to right, top to bottom), ...
                e.SheetW = A3W; e.SheetH = A3H;
                foreach (var series in new[] { new { Name = "specimen", Tiles = spec }, new { Name = "context", Tiles = ctx } })
                    for (int pg = 0; pg * perPage < series.Tiles.Count; pg++)
                    {
                        var group = series.Tiles.Skip(pg * perPage).Take(perPage).ToList();
                        var groupTags = tags.Skip(pg * perPage).Take(perPage).ToList();
                        var page = Edition.Tile(group, A3W, A3H, perSide, perSide);
                        var pi = Edition.Write(page, Path.Combine(dir, "a3_" + series.Name + "_" + va.Tag), "b" + e.Seed + "_" + series.Name + "_p" + (pg + 1).ToString("D2", CI) + "_" + va.Tag,
                                               "Cluster " + e.Seed + ", bubbles " + string.Join(" ", groupTags) + " (" + series.Name + ", " + va.Tag + ")", e);
                        pi["kind"] = series.Name + " page"; pi["style"] = va.Tag; pi["bubbles"] = groupTags; sheets.Add(pi);
                        log(string.Format(CI, "A3 {0} page {1} (bubbles {2}): {3} strokes, ~{4} min", series.Name, pg + 1, string.Join(" ", groupTags), pi["strokes"], pi["estMinutes"]));
                    }
            }
        }
        manifest["sheets"] = sheets;
        File.WriteAllText(Path.Combine(dir, "manifest.json"), Edition.Json(manifest));
        File.WriteAllText(Path.Combine(dir, "cluster.obj"), FoamAnalysis.ToObj(s.Mesh));
        log("done: " + dir);
        return 0;
    }

    // The bubble with the most faces among those with no film to the outside (-1 if none).
    static int MostFacedInterior(FoamMesh m)
    {
        var nb = new Dictionary<int, HashSet<int>>(); var outer = new HashSet<int>();
        foreach (var t in m.T)
        {
            if (t.Neg <= 0 || t.Pos <= 0) { outer.Add(t.Neg); outer.Add(t.Pos); continue; }
            HashSet<int> h;
            if (!nb.TryGetValue(t.Neg, out h)) nb[t.Neg] = h = new HashSet<int>(); h.Add(t.Pos);
            if (!nb.TryGetValue(t.Pos, out h)) nb[t.Pos] = h = new HashSet<int>(); h.Add(t.Neg);
        }
        int best = -1, bf = -1;
        foreach (var kv in nb) if (!outer.Contains(kv.Key) && kv.Value.Count > bf) { bf = kv.Value.Count; best = kv.Key; }
        return best;
    }
}

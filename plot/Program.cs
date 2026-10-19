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
        string style = opt("style", cmd == "preview" ? "both" : "short");
        string field = opt("field", "light");
        bool a3 = opt("paper", "a4") == "a3";
        var styles = style == "both" ? new[] { "short", "long" } : style == "all" ? new[] { "short", "long", "mixed" } : new[] { style };
        string light = opt("light", "classic");
        bool outline = opt("outline", "on") != "off";
        var fields = field == "all" ? new[] { "light", "facet", "border" } : new[] { field };
        double swirl = double.Parse(opt("swirl", "0"), CI);
        string hidden = opt("hidden", null);
        string extra = (swirl > 0 ? "-swirl" + (int)Math.Round(swirl * 100) : "") + (hidden != null ? "-hidden" + (hidden == "dash" ? "dash" : "") : "")
                     + (light == "dramatic" ? "-dramatic" : "") + (outline ? "" : "-nooutline");
        var variants = (from st in styles from f in fields select new { Style = st, Field = f, Tag = (f == "light" ? st : st + "-" + f) + extra }).ToList();

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "editions"));
        if (opt("out", null) != null) root = Path.GetFullPath(opt("out", null));
        var sw = Stopwatch.StartNew();
        Action<string> log = m => Console.WriteLine(string.Format(CI, "[{0,5:F1} s] {1}", sw.Elapsed.TotalSeconds, m));
        Func<int, EditionSettings> settings = seed => new EditionSettings
        {
            Seed = seed, Count = int.Parse(opt("count", "0")), DetailBatches = int.Parse(opt("detail", "0")),
            Light = light, Outline = outline,
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
                var hs = new HatchStyle { Short = va.Style == "short", Mixed = va.Style == "mixed", Hyper = light == "dramatic", Field = va.Field, Swirl = swirl, Hidden = hidden };
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

        var e = settings(int.Parse(opt("seed", "1")));
        string dir = Path.Combine(root, e.Seed.ToString(CI));
        Directory.CreateDirectory(dir);
        var s = Edition.Generate(e, dir, log);
        var view = Edition.MakeView(e.Seed);
        if (cmd == "tone") { Edition.ToneImage(s, view, e, Path.Combine(dir, "tone_" + e.Light + ".png")); log("tone image written"); return 0; }
        var vol = s.Mesh.Volumes();
        var bubbles = Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => !s.IsDead(k) && vol[k] > 0).ToList();
        var manifest = new Dictionary<string, object>
        {
            { "seed", e.Seed }, { "bubbles", bubbles.Count },
            { "radii", bubbles.Select(k => Math.Round(s.DesiredRadius(k), 3)).ToList() },
            { "view", new Dictionary<string, object> { { "azimuthDeg", Math.Round(view.Azimuth * 180 / Math.PI, 1) }, { "elevationDeg", Math.Round(view.Elevation * 180 / Math.PI, 1) } } },
            { "sheet", a3 ? "A3 portrait 297x420 mm: overview full sheet, bubble sheets 4-up (A5 tiles); pen 0.3 mm" : "A4 portrait 210x297 mm, pen 0.3 mm" },
        };
        var sheets = new List<object>();
        string prefix = a3 ? "a3_" : "";
        foreach (var va in variants)
        {
            var hs = new HatchStyle { Short = va.Style == "short", Mixed = va.Style == "mixed", Hyper = light == "dramatic", Field = va.Field, Swirl = swirl, Hidden = hidden };
            if (a3) { e.SheetW = A3W; e.SheetH = A3H; } else { e.SheetW = 210; e.SheetH = 297; }
            var p = Edition.Overview(s, view, hs, e);
            var info = Edition.Write(p, dir, prefix + "overview_" + va.Tag, "Bubble cluster " + e.Seed + " (" + va.Tag + ")", e);
            info["kind"] = "overview"; info["style"] = va.Tag; sheets.Add(info);
            log("overview (" + va.Tag + "): " + info["strokes"] + " strokes, ~" + info["estMinutes"] + " min");

            var which = cmd == "preview"
                ? new List<int> { int.Parse(opt("bubble", bubbles.OrderByDescending(k => s.DesiredRadius(k)).Skip(1).First().ToString(CI))) }
                : bubbles;
            if (a3) { e.SheetW = A3W / 2; e.SheetH = A3H / 2; }
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
                // 4-up pages, bubbles in order: page 1 = bubbles 01-04 (left to right, top to bottom), ...
                e.SheetW = A3W; e.SheetH = A3H;
                foreach (var series in new[] { new { Name = "specimen", Tiles = spec }, new { Name = "context", Tiles = ctx } })
                    for (int pg = 0; pg * 4 < series.Tiles.Count; pg++)
                    {
                        var group = series.Tiles.Skip(pg * 4).Take(4).ToList();
                        var groupTags = tags.Skip(pg * 4).Take(4).ToList();
                        var page = Edition.Tile(group, A3W, A3H, 2, 2);
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
}

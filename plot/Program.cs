using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

// bubbleplot: headless pen-plot editions of bubble clusters.
//
//   bubbleplot preview --seed 4721 [--style short|long|both] [--bubble K]
//       the overview + one specimen + one context sheet, both styles by default (quick look)
//   bubbleplot edition --seed 4721 --style short|long [--count N] [--detail B]
//       everything: overview, a specimen and a context sheet for every bubble, manifest.json
//
// Output: editions/<seed>/ next to this folder (git-ignored). Each sheet is an A4 SVG with pen layers
// (hatch, outline, context) plus a PNG preview on cream paper.
public static class Program
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static int Main(string[] args)
    {
        if (args.Length == 0) { Console.WriteLine("usage: bubbleplot preview|edition --seed N [--style short|long|both] [--bubble K] [--count N] [--detail B]"); return 1; }
        string cmd = args[0];
        Func<string, string, string> opt = (name, def) => { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; };
        var e = new EditionSettings
        {
            Seed = int.Parse(opt("seed", "1")), Count = int.Parse(opt("count", "0")), DetailBatches = int.Parse(opt("detail", "0")),
        };
        string style = opt("style", cmd == "preview" ? "both" : "short");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "editions"));
        if (opt("out", null) != null) root = Path.GetFullPath(opt("out", null));
        string dir = Path.Combine(root, e.Seed.ToString(CI));
        Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        Action<string> log = m => Console.WriteLine(string.Format(CI, "[{0,5:F1} s] {1}", sw.Elapsed.TotalSeconds, m));

        var s = Edition.Generate(e, dir, log);
        var view = Edition.MakeView(e.Seed);
        var vol = s.Mesh.Volumes();
        var bubbles = Enumerable.Range(1, s.Mesh.BubbleCount).Where(k => !s.IsDead(k) && vol[k] > 0).ToList();
        var styles = style == "both" ? new[] { "short", "long" } : new[] { style };
        var manifest = new Dictionary<string, object>
        {
            { "seed", e.Seed }, { "bubbles", bubbles.Count },
            { "radii", bubbles.Select(k => Math.Round(s.DesiredRadius(k), 3)).ToList() },
            { "view", new Dictionary<string, object> { { "azimuthDeg", Math.Round(view.Azimuth * 180 / Math.PI, 1) }, { "elevationDeg", Math.Round(view.Elevation * 180 / Math.PI, 1) } } },
            { "sheet", "A4 portrait 210x297 mm, pen 0.3 mm" },
        };
        var sheets = new List<object>();
        foreach (var sty in styles)
        {
            var hs = new HatchStyle { Short = sty == "short" };
            Scene ov;
            var p = Edition.Overview(s, view, hs, e, out ov);
            var info = Edition.Write(p, dir, "overview_" + sty, "Bubble cluster " + e.Seed + " (" + sty + ")", e);
            info["kind"] = "overview"; info["style"] = sty; sheets.Add(info);
            log("overview (" + sty + "): " + info["strokes"] + " strokes, ~" + info["estMinutes"] + " min");
            var which = cmd == "preview" ? new List<int> { int.Parse(opt("bubble", bubbles.OrderByDescending(k => s.DesiredRadius(k)).Skip(1).First().ToString(CI))) } : bubbles;
            int num = 0;
            foreach (int k in which)
            {
                num++;
                string tag = (bubbles.IndexOf(k) + 1).ToString("D2", CI);
                var sp = Edition.Specimen(s, view, k, hs, e);
                var si = Edition.Write(sp, Path.Combine(dir, "specimen_" + sty), "bubble_" + tag,
                                       "Bubble " + tag + " of " + bubbles.Count + ", cluster " + e.Seed + " (specimen, " + sty + ")", e);
                si["kind"] = "specimen"; si["style"] = sty; si["bubble"] = k; sheets.Add(si);
                var cp = Edition.Context(s, view, k, hs, e, ov);
                var ci = Edition.Write(cp, Path.Combine(dir, "context_" + sty), "bubble_" + tag,
                                       "Bubble " + tag + " of " + bubbles.Count + ", cluster " + e.Seed + " (in context, " + sty + ")", e);
                ci["kind"] = "context"; ci["style"] = sty; ci["bubble"] = k; sheets.Add(ci);
                log(string.Format(CI, "bubble {0} ({1}/{2}, {3}): specimen {4} strokes, context {5} strokes", tag, num, which.Count, sty, si["strokes"], ci["strokes"]));
            }
        }
        manifest["sheets"] = sheets;
        File.WriteAllText(Path.Combine(dir, "manifest.json"), Edition.Json(manifest));
        File.WriteAllText(Path.Combine(dir, "cluster.obj"), FoamAnalysis.ToObj(s.Mesh));
        log("done: " + dir);
        return 0;
    }
}

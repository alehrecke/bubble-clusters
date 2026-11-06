using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

// Flipbook cards for the Orbit Flipbook (Alex's personal project, 2026-11-05:
// D:\AUGBOT\Personal\Practice\Projects\OrbitFlipbook). A settled cluster is drawn from a moving camera, one frame
// per poker card (88 x 63 mm, landscape, tip edge up). The machine pans and tilts the card reel by the same
// angles the camera moved, so a viewer who walks round to keep the card face-on sees the cluster stay put in space.
//
//   flipbook --seed N [--path orbit|sway|nod|figure8|orbit-nod] [--frames 48] [--fps 12] [--az0 35] [--el0 35]
//            [--sweep 60] [--nod 20] [--marks mist-dew] [--id 1] [--obj cluster.obj] [--out <editions root>]
//   flipbook-jigtest            one sheet: a 1 mm inset rectangle in every jig pocket (plot on scrap cards)
//
// Output (editions/animations/flipbook_<seed>_<path>/):
//   jig_sheet_01..04.svg/.png  12 cards per sheet, laid out for two printed jig tiles (cad/flipbook.scad card_jig)
//   cards/card_NN.png          each card on its own (preview), preview.gif (ffmpeg, if installed)
//   profile_<id>.json          pan/tilt per frame for the firmware (upload through the flipbook's web page)
//   manifest.json
// The card and jig numbers below must match cad/params.scad (see the project's docs/DATA-CONTRACT.md).
public static partial class Edition
{
    // ---- card (poker card, landscape: x along the 88 mm width, y down from the tip edge) ----
    const double CardW = 88, CardH = 63;
    // Drawn image: 4 mm side margins, 7 mm under the tip (the stop rod covers ~3 mm), 10 mm root zone (7 mm in the slot).
    const double ImgX = 4, ImgY = 7, ImgW = 80, ImgH = 46;
    // ---- jig tile (2 columns x 3 rows of pockets); two tiles side by side = one sheet of 12 cards ----
    const double PocketW = 88.4, PocketH = 63.4, JigWall = 4, JigBorder = 8, MarkInset = 4;
    const double TileW = 2 * PocketW + JigWall + 2 * JigBorder;           // 196.8
    const double TileH = 3 * PocketH + 2 * JigWall + 2 * JigBorder;       // 214.2

    // Card k (0..11) of a sheet: top-left of its pocket, in sheet coordinates whose origin is the cross-hair
    // engraved on the left tile (the AxiDraw's home position goes over it).
    static V2 PocketOrigin(int k)
    {
        int tile = k / 6, c = (k % 6) % 2, r = (k % 6) / 2;
        return new V2(tile * TileW + JigBorder + c * (PocketW + JigWall) - MarkInset, JigBorder + r * (PocketH + JigWall) - MarkInset);
    }

    public class FlipPath { public double[] Az, El; public int PanTurns; }
    public static double FlipZoom = 1.0;

    // Camera azimuth / elevation (degrees) per frame. Frame i is shown at loop phase (i + 0.5) / n.
    public static FlipPath MakeFlipPath(string kind, int n, double az0, double el0, double sweep, double nod)
    {
        var p = new FlipPath { Az = new double[n], El = new double[n] };
        for (int i = 0; i < n; i++)
        {
            double t = 2 * Math.PI * i / n;
            switch (kind)
            {
                case "orbit": p.Az[i] = az0 + 360.0 * i / n; p.El[i] = el0; p.PanTurns = 1; break;
                case "orbit-nod": p.Az[i] = az0 + 360.0 * i / n; p.El[i] = el0 + nod * Math.Sin(2 * t); p.PanTurns = 1; break;
                case "sway": p.Az[i] = az0 + sweep * Math.Sin(t); p.El[i] = el0; break;
                case "nod": p.Az[i] = az0; p.El[i] = el0 + nod * Math.Sin(t); break;
                case "figure8": p.Az[i] = az0 + sweep * Math.Sin(t); p.El[i] = el0 + nod * Math.Sin(2 * t); break;
                default: throw new ArgumentException("unknown --path " + kind + " (orbit | orbit-nod | sway | nod | figure8)");
            }
        }
        return p;
    }

    public static void Flipbook(FoamSolver s, int seed, string pathKind, int n, int fps, double az0, double el0, double sweep, double nod,
                                string marks, int id, HatchStyle hs, EditionSettings e, string dir, Action<string> log)
    {
        var ci = CultureInfo.InvariantCulture;
        var path = MakeFlipPath(pathKind, n, az0, el0, sweep, nod);
        const double TiltLimit = 35;     // the head's soft limit (firmware TILT_MIN/MAX)
        for (int i = 0; i < n; i++)
            if (Math.Abs(path.El[i] - el0) > TiltLimit) throw new ArgumentException("tilt " + (path.El[i] - el0) + " deg is outside +-" + TiltLimit);
        Directory.CreateDirectory(dir);
        string cdir = Path.Combine(dir, "cards"); Directory.CreateDirectory(cdir);

        // Constant framing: fit a bounding sphere, whose outline is the same from every direction, so the cluster
        // neither breathes nor drifts as the camera moves.
        var V = s.Mesh.V;
        var c0 = new Vec3(V.Average(q => q.X), V.Average(q => q.Y), V.Average(q => q.Z));
        // Its radius: the smallest that keeps the cluster inside the image window in every view of the path
        // (|up| <= rho, |right| <= rho * w / h about the centroid). --zoom > 1 crops in from there (overflow is clipped).
        double rho = 0;
        for (int i = 0; i < n; i++)
        {
            var fv = MakeView(seed, path.Az[i], path.El[i]).Forward.Normalized();
            var fr = Vec3.Cross(fv, new Vec3(0, 0, 1)).Normalized(); var fu = Vec3.Cross(fr, fv).Normalized();
            foreach (var q in V) { var d = q - c0; rho = Math.Max(rho, Math.Max(Math.Abs(Vec3.Dot(d, fu)), Math.Abs(Vec3.Dot(d, fr)) * ImgH / ImgW)); }
        }
        double rad = rho * 1.03 / FlipZoom;
        var sphere = Fibonacci(800).Select(d => c0 + d * rad).ToList();

        // World-fixed lights: the camera-relative lights of the reference view (az0, el0), frozen.
        var refView = MakeView(seed, az0, el0);
        var F = refView.Forward.Normalized();
        var R = Vec3.Cross(F, new Vec3(0, 0, 1)).Normalized();
        var U = Vec3.Cross(R, F).Normalized();
        e.SheetW = CardW; e.SheetH = CardH;
        var ms = new MarkSettings { Kind = MarkSettings.Resolve(marks) };
        var cards = new List<Plot>();
        var weights = new Dictionary<string, double> { { "context", 0.45 }, { "hidden", 0.5 }, { "inner", 0.55 }, { "innerborders", 0.6 }, { "background", 0.8 }, { "fine", 0.5 } };
        var sw = Stopwatch.StartNew();
        try
        {
            FrameOverride = new[] { ImgX / CardW, ImgY / CardH, ImgW / CardW, ImgH / CardH };
            FitOverride = sphere;
            WorldFill = (R * -0.45 + U * 0.7 + F * -0.55).Normalized();
            WorldKey = (R * -0.72 + U * 0.5 + F * -0.48).Normalized();
            for (int i = 0; i < n; i++)
            {
                var v = MakeView(seed, path.Az[i], path.El[i]);
                var p = MarkOverviewRaw(s, v, Clone(hs), e, new MarkSettings { Kind = ms.Kind });
                foreach (var ln in p.LayerOrder.ToList())
                {
                    var kept = ClipRect(p.Layers[ln], ImgX, ImgY, ImgX + ImgW, ImgY + ImgH);
                    p.Layers[ln].Clear(); p.Layers[ln].AddRange(kept);
                }
                // Frame number in the root zone (it ends up inside the reel's slot): which slot the card goes in.
                p.Layer("number").AddRange(Label.Text((i + 1).ToString("D2", ci), 2.5, CardH - 5.2, 2.2));
                SheetIO.Optimize(p);
                SheetIO.WritePng(p, Path.Combine(cdir, string.Format(ci, "card_{0:00}.png", i + 1)), 8, weights);
                cards.Add(p);
                log(string.Format(ci, "card {0}/{1}: az {2:F1} el {3:F1}, {4} strokes ({5:F0} s)", i + 1, n, path.Az[i], path.El[i], p.Strokes(), sw.Elapsed.TotalSeconds));
            }
        }
        finally { FrameOverride = null; FitOverride = null; WorldFill = null; WorldKey = null; }

        // Jig sheets: 12 cards each, plotted landscape on the AxiDraw A3 (origin = the jig cross-hair).
        var sheets = new List<object>();
        var se = Clone(e); se.SheetW = 420; se.SheetH = 297; se.PlotterCopy = false;
        int nSheets = (n + 11) / 12;
        for (int sh = 0; sh < nSheets; sh++)
        {
            var page = new Plot { Width = 420, Height = 297 };
            for (int k = 0; k < 12 && sh * 12 + k < n; k++)
            {
                var card = cards[sh * 12 + k]; var off = PocketOrigin(k);
                foreach (var name in card.LayerOrder)
                    page.Layer(name).AddRange(card.Layers[name].Select(l => l.Select(q => q + off).ToList()));
                // Preview only: the pocket outline, so the PNG shows where each card sits.
                page.Layer("_pocket").Add(new List<V2> { off, off + new V2(CardW, 0), off + new V2(CardW, CardH), off + new V2(0, CardH), off });
            }
            var info = Write(page, dir, string.Format(ci, "jig_sheet_{0:00}_frames{1:00}-{2:00}", sh + 1, sh * 12 + 1, Math.Min(n, sh * 12 + 12)),
                             string.Format(ci, "Orbit Flipbook {0} ({1}), frames {2}-{3}: load 12 blank poker cards in the jig, pen home over the cross-hair",
                                           seed, pathKind, sh * 12 + 1, Math.Min(n, sh * 12 + 12)), se);
            sheets.Add(info);
            log("jig sheet " + (sh + 1) + ": " + info["strokes"] + " strokes, ~" + info["estMinutes"] + " min");
        }

        // Firmware profile: pan relative to the first frame (unwrapped; an orbit adds +360 over the loop),
        // tilt = camera elevation minus the reference elevation the head shows at tilt 0.
        var prof = new Dictionary<string, object>
        {
            { "format", "orbit-flipbook.profile/1" }, { "id", id }, { "name", "seed " + seed + " " + pathKind + " (" + MarkSettings.NameOf(ms.Kind) + ")" },
            { "frames", n }, { "fps", fps }, { "panTurns", path.PanTurns }, { "refElevationDeg", el0 },
            { "panDeg", path.Az.Select(a => Math.Round(a - path.Az[0], 3)).ToList() },
            { "tiltDeg", path.El.Select(x => Math.Round(x - el0, 3)).ToList() },
        };
        File.WriteAllText(Path.Combine(dir, "profile_" + id + ".json"), Json(prof));
        var man = new Dictionary<string, object>
        {
            { "seed", seed }, { "path", pathKind }, { "frames", n }, { "fps", fps }, { "marks", ms.Kind }, { "profileId", id },
            { "az0", az0 }, { "el0", el0 }, { "sweep", sweep }, { "nod", nod },
            { "card", "poker 88 x 63 mm landscape, tip edge up; image 80 x 46 at (4, 7)" }, { "sheets", sheets },
            { "totalMinutes", Math.Round(sheets.Cast<Dictionary<string, object>>().Sum(d => Convert.ToDouble(d["estMinutes"], ci)), 1) },
        };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), Json(man));
        File.WriteAllText(Path.Combine(dir, "cluster.obj"), FoamAnalysis.ToObj(s.Mesh));

        try
        {
            var psi = new ProcessStartInfo("ffmpeg", string.Format(ci, "-y -framerate {0} -i \"{1}\" -vf \"scale=528:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse\" -loop 0 \"{2}\"",
                                                                   fps, Path.Combine(cdir, "card_%02d.png"), Path.Combine(dir, "preview.gif")))
            { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            var pr = Process.Start(psi); pr.StandardError.ReadToEnd(); pr.WaitForExit();
            log("preview.gif: ffmpeg exit " + pr.ExitCode);
        }
        catch (Exception ex) { log("no preview.gif (ffmpeg not found: " + ex.Message + ")"); }
        log("done: " + dir);
    }

    // Registration test: a rectangle 1 mm inside every card, plus a cross at each card's image corner.
    public static void FlipbookJigTest(string dir, EditionSettings e)
    {
        var page = new Plot { Width = 420, Height = 297 };
        for (int k = 0; k < 12; k++)
        {
            var o = PocketOrigin(k);
            page.Layer("outline").Add(new List<V2> { o + new V2(1, 1), o + new V2(CardW - 1, 1), o + new V2(CardW - 1, CardH - 1), o + new V2(1, CardH - 1), o + new V2(1, 1) });
            foreach (var c in new[] { new V2(ImgX, ImgY), new V2(ImgX + ImgW, ImgY), new V2(ImgX, ImgY + ImgH), new V2(ImgX + ImgW, ImgY + ImgH) })
            {
                page.Layer("outline").Add(new List<V2> { o + c + new V2(-2, 0), o + c + new V2(2, 0) });
                page.Layer("outline").Add(new List<V2> { o + c + new V2(0, -2), o + c + new V2(0, 2) });
            }
            page.Layer("outline").AddRange(Label.Text((k + 1).ToString("D2", CultureInfo.InvariantCulture), o.X + 2.5, o.Y + CardH - 5.2, 2.2));
        }
        var se = Clone(e); se.SheetW = 420; se.SheetH = 297; se.PlotterCopy = false;
        Write(page, dir, "jig_registration_test", "Orbit Flipbook jig registration test: scrap cards in all 12 pockets, pen home over the cross-hair", se);
    }
}

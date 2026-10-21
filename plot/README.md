# bubbleplot: pen-plot editions of bubble clusters

Headless (no Rhino): seeded random cluster → relaxed with the same solver core → orthographic view →
surface-following hatching + Plateau-border arcs + silhouettes, hidden lines removed → A4 SVGs for the
AxiDraw (0.3 mm fine liner) + PNG previews on cream paper.

## Defaults (Alex's settings, 2026-10-19)

`bubbleplot edition --seed N` alone gives: A3 (`--paper a3`), bubble sheets 9-up (`--tiles 9`), `--style mixed --field border
--light dramatic --density 1.5`, context sheets with the whole border network (`--context all`; `dash` = back borders dashed,
`visible` = the old front-only look). Any flag overrides its default.

**One SVG per sheet:** only the plotter-ready file (`*_plotter-landscape.svg`, turned 90 degrees for the AxiDraw A3: load the
paper landscape), plus an upright PNG preview.

**Settled clusters only:** after relaxing, `Edition.Settle` measures pinched films (`MeasureStress`: a border with 2+ edges where
two films meet under 30 deg; these plot as thorns) and forces rearrangements until there are none (up to 6 rounds). If a seed
still won't settle, variants seed*1000+1.. are tried; the variant used is in the manifest. Seed 8150: 6 pinched borders (two
films 2 deg apart) → 1 forced rearrangement → clean (smallest film angle 71 deg), thorns gone.

## Run

```
dotnet build plot -c Release
plot/bin/Release/net9.0/bubbleplot.exe preview --seed 4721            # overview + one bubble, both styles (~10 s)
plot/bin/Release/net9.0/bubbleplot.exe edition --seed 4721 --style short   # every bubble (~20-40 s)
```

A3 (4-up) and stroke-direction variants:

```
bubbleplot edition --seed 1093 --paper a3 --field border    # A3 overview + bubble sheets 4 per A3 page
bubbleplot overviews --seeds 4721,1093,2207,3318            # four clusters on one A3 page
bubbleplot preview --seed 4721 --style short --field all    # compare the three direction fields
```

`--context visible|all|dash`: what the context sheets show of the rest of the cluster. `visible` (default) = front borders +
silhouette (hidden lines removed); `all` = the whole Plateau-border network, back borders too (an X-ray, solid); `dash` = the
same with the back borders dashed (0.8 mm dash, 0.6 mm gap). Lines are still cut where they'd cross the featured bubble. 9-up
seed 8150: visible ~11 min/page, all ~21, dash ~39 (every dash is a pen lift).

`--tiles 9` (with `--paper a3`): bubble sheets 9-up (3x3 tiles of 99x140 mm) instead of 4-up. `--density f`: hatching
lines f times closer (all spacings / f; the darkest spacing is floored at the 0.3 mm pen width). First 9-up set: seed 8150,
`--paper a3 --tiles 9 --style mixed --field border --light dramatic --density 1.5`.

`--paper a3`: each tile is rendered natively at A5 (148.5x210 mm) with the same physical line spacing
(scaling an A4 sheet down would pack the lines tighter than the 0.3 mm pen can draw). Pages go in bubble
order (page 1 = bubbles 01-04, left to right, top to bottom) in `a3_specimen_<style>/` and `a3_context_<style>/`.
Cut marks between tiles are on a separate `marks` layer.

`--field` (lighting stays global in all three):
- `light` (default): rings around each bubble's light axis, along lines of equal brightness.
- `border`: inset rings parallel to each film's own Plateau borders (level sets of the distance to the border).
- `facet`: concentric circles around each film's centroid.

Details (combine freely with any field/style):
- `--hidden solid|dash`: Plateau borders behind the surface on their own `hidden` layer (preview grey). On the
  overview this is the cluster's whole internal border network (the X-ray of the foam); on a bubble sheet,
  its back-side borders. A small halo gap is left where a hidden line passes behind a visible one.
- `--swirl 0..1`: soap-film eddies. A few soft vortices per film plus a gentle noise rotate the stroke
  direction within the surface (strokes still lie on the film and stop at its borders). Vortices are seeded
  from the film's identity in 3D, so a film swirls the same way on every sheet. 0.5 = subtle, 1 = visible eddies.

Dramatic lighting and mixed stroke lengths (after the look of Piter Pasma's ray-hatched plots; our own
implementation of the ideas, none of his code):
- `--light dramatic`: a low key light from the front-left with real cast shadows (shadow map from the light,
  soft edges) plus ambient occlusion (depth maps from 64 directions), so bubbles shadow each other and the
  creases between them go dark. Spacing follows a steep curve, spacing = 0.33 mm / darkness, so the darks close
  up to near-solid and the lights open fast (nothing past 6 mm). `bubbleplot tone --seed N --light dramatic`
  writes the tone image the hatcher sees (useful when tuning `LightSettings` in Lighting.cs).
- `--style mixed`: no length rule. A stroke runs until it comes within the local spacing of another stroke,
  turns sharply, leaves its film, or would come back onto itself. Long lines grow in even tones, short ones in
  gradients and at the light/shadow threshold.
- `--outline off`: no border arcs or silhouettes; edges show only through tone (lit edges dissolve into the paper).

Plotting on the AxiDraw V3/A3 (draw area 430 x 297, landscape): every portrait sheet also gets a
`*_plotter-landscape.svg`, the same drawing turned 90 degrees. Load the A3 paper landscape; the top of the
drawing is on the left. The Pi's `~/axidraw_conf.py` must say `model = 2`, otherwise axicli assumes the A4
machine and clips at 300 x 218 mm (fixed on 2026-10-18). Send with
`Personal/Practice/Experiments/raspi-plotter/scripts/send-plot.ps1 <file>_plotter-landscape.svg`.
The Pi's web UI lists plots newest first, so upload multi-page sets **last page first** (p03, p02, p01) to have them listed in
order, and give files that share a name a unique one on the Pi (e.g. prefix the seeds: the overviews pages are all called
`a3_overviews_<style>_plotter-landscape.svg`). Page counts: 21 bubbles 9-up = 9 + 9 + 3 (the last page is one row).

Cluster generator (since 2026-10-18 evening): 16-26 bubbles, radii 0.3-1.9 skewed toward small (`SizeSkew` 2.2),
90k-triangle budget; `--rmin/--rmax` override. Old seeds no longer reproduce the earlier clusters.
First plot run: seed 2614, `--paper a3 --style mixed --field border --light dramatic` (+ a `--swirl 1` set).

Best so far: `--style mixed --field border --light dramatic` (optionally `--outline off`).

True curvature directions don't work here: soap films have constant mean curvature and are close to
spherical caps, where every direction is a principal direction, so the "principal curvature" field is
noise. `border` is what following the facet's shape means for these surfaces.

Plot any saved foam instead of generating one: `bubbleplot obj <foam.obj> [--az deg --el deg]`. That covers a GH run's
`mesh_*.obj` or a harness result (`harness lattice BCC` writes `lattice_BCC.obj`). It previews the overview plus the interior
bubble with the most faces (e.g. a Kelvin cell). `--az` / `--el` set the view direction (camera azimuth from +X around Z, and
elevation); otherwise the seed picks them.

Options: `--style short|long|both`, `--count N` (bubbles; default random 9-15), `--detail B` (Border Detail
batches of small bubbles after relaxing), `--bubble K` (which bubble a preview features), `--out DIR`.

## Mark-making tests (`marks`, 2026-10-19)

`bubbleplot marks --seed N [--marks stipple,cross,natural,glass] [--wobble mm] [--breaks per-mm] [--dotmin mm]` writes one A3
overview per kind to `editions/marks_<seed>/` (Marks.cs):
- `stipple`: variable-radius Poisson-disc dots (spacing DotMin/tone^0.75, wavering with low-frequency noise), each a 0.22 mm tick.
  Seed 6427: 35k dots ≈ 2.5 h of pen lifts. `--dotmin 0.6` roughly halves that.
- `cross`: the surface-following pass opened up 1.6x, then a pass rotated 72 deg over tone > 0.38, and a -38 deg pass over tone > 0.68.
  Each pass has its own spacing grid; light wobble.
- `natural`: house style + hand noise: sideways wander 0.12 mm (two noise octaves, 3.5 mm wavelength), small breaks (0.04/mm).
- `glass`: interior walls (`Scene.Surface(m, -1)`, two-sided shading, facet rings) on layer `inner`, the internal border network
  on `innerborders`, the skin at 0.6 tone on top. Plot `inner` + `innerborders` with a finer or lighter pen.
Round 2 (quad sheet, `marks --seed N --quad [--marks a,b,c,d]`): four kinds as A5 quadrants on one A3 page, for side-by-side
plots. Default quad: glass-stipple | cross | inkdrift | wild. Stipple spacing is now DotMin 0.30 / tone^1.6 (steep: solid darks,
sparse lights). `cross` adds per-film angle jitter (6/16/22 deg per pass), +-30% spacing noise and seed-drawn pass angles. `inkdrift`
pushes strokes along a swirling noise flow (up to 2.2 mm at the cluster edge, growing along each stroke), with pooled double
passes in the darks. `wild` is natural at 0.5 mm wobble. Also RotateJitter, SepJitter, JitterSeed.
Round 3 default quad: glass-dots (skin and walls both stippled, walls sparser, borders kept) | cross (back to 1.1-1.25x spacing,
smaller jitter: delicate) | inkdrift (1.1 mm drift, no breaks, outline wobbles and drifts with the hatching) | wild-bg (no outlines;
hatched ground with nested 4/2/1/0.5 mm line families by tone: cast shadow away from the light plus a dark contact halo).
Round 4 default quad: glass-pure (dots only; borders and silhouette as runs of dots, 0.32 mm visible / 0.55 mm behind) | cross |
inkdrift (outline kept smooth and in place) | wild-bg (sky ground: near-horizontal lines, darker toward the top, stretched cloud
patches, no cast shadow). glass-dots (stippled with line borders) still available.
**Names** (printed on every PNG preview, layer `_label`, never plotted; accepted by `--marks` in place of the kind):
Sand = stipple, Engrave = cross, Hand = natural, Tremor = wild, Tremor Sky = wild-bg, Drift = inkdrift, Lens = glass,
Ghost = glass-stipple, Frost = glass-dots, Mist = glass-pure. `--round r4` adds a round tag to the label.

## Gallery (`gallery`, 2026-10-20)

`bubbleplot gallery --start N [--n 16] [--marks glass-pure]`: N.. N+n-1 new clusters, one per A3 quadrant, 4 per page, in one mark
style. Per seed: count 8..40 (log-uniform), RMin 0.18..0.40, RMax 2.5..7.5 x RMin, skew 1.0..3.4. Clusters settled, OBJs + clusters.txt
saved. Seeds 7001-7016 in Mist: ~70-80k strokes (~5.5 h) per page.

## Foam bed (`bed`, 2026-10-19)

`bubbleplot bed --seed N [--count 120] [--layers 3] [--bed mist,lattice] [--reuse]` -> `editions/bed_<seed>/`. The A3 sheet is the plane
the foam sits on (PlaneHost z = 0); bubbles (r 0.12..1.2) are dropped one by one, each landing in the lowest of 8 tried spots, over an
area a little bigger than the window, so the pile is ~3 layers deep and runs off the sheet edges. Viewed straight up through the plane.
Full sheet with a 10 mm paper margin. The solved foam is saved (`bed.obj` + `window.txt`); `--reuse` replots it in seconds.
- `mist`: transparent stipple. Interior walls + skin in dots (DotMin 0.36, gamma 1.8, tone 0.66 / 0.8), every border as a run of dots,
  footprint borders on the plane tightest. Seed 3301: ~104k dots, ~7.5 h.
- `lattice`: borders only, thickened by height: 4 parallel passes 0.17 mm apart on the plane down to 1 at the top of the pile. ~30 min.
- `meniscus` (Meniscus.cs): lattice + wet borders, two pens. Layer `fine` (plot first, fine grey/sepia pen): pile above the plane,
  lines going wobbly -> broken -> dotted with height, stipple on deep walls and dust around upper nodes in the focus only. Layer
  `bold` (plot second, 0.3-0.5 black): plane borders as ribbons swelling toward the nodes, node blobs, and meniscus contours
  (marching squares on a blurred distance-to-wall field, so they round off in the corners; reach grows near nodes). Focus = densest
  patch of plane nodes, Gaussian falloff. Seed 3301: ~13k strokes, ~72 min.
- One-pen variants of meniscus (layers deep + front, same pen): `wet-lattice` (every border a wet ribbon, width shrinking with
  height; ~42 min), `deep-stipple` (crisp borders + wall stipple weighted (1-height)^1.4: densest near the plane; ~2 h),
  `depth-hatch` (walls hatched, strokes kept with probability (1-height)^1.3; ~95 min, busy).
- `windows` (one pen): the footprints are transparent, so each opening looks up into its own bubble: one scene of ALL films
  (`Scene.Surface(m, -2)`, two-sided), nearest-from-below = each room's ceiling, stippled (edge-on walls at the rims dark, the
  ceiling overhead light), the rooms' visible facet edges as fine lines, the meniscus plane on top. Seed 3301: ~92k strokes, ~7 h.
- `deep-windows` (one pen): transparent windows. FilmStack.cs finds every film along each vertical sight line (depth peeling);
  4 layers of walls stippled, tone x 0.62 and dots sparser per layer; room edges by films in front: crisp / dashed / dotted /
  sparse dots / hidden. Seed 3301: ~115k strokes, ~8.5 h.
Seed 3301 took ~50 min to solve (234k triangles); settle ended with 7 pinched borders (not fully clean, not visible in the drawings).

More styles (More.cs, 2026-10-20; most need few pen lifts): Contour = contour (skin sliced by horizontal planes, nested
families by tone), Thread = thread (stipple points walked nearest-neighbour into long lines), Froth = froth (circles packed by tone),
Wave = wave (parallel lines rippling with tone, nested families). `marks --obj <cluster.obj>` reuses a saved cluster.
Beads (Beads.cs, 2026-10-20): satellite bubbles placed in 3D along the cleaned Plateau borders as a post-process (not simulated):
clumped at the nodes, shrinking along each border, packed without overlap; drawn as outline circles, nearer beads hiding farther ones,
whole-bead visibility against the surface, a highlight gap toward the light, and the stipple cleared under each bead. Beaded Mist =
mist-beads / mist-beads-dense, Beaded Wire = wire-beads / wire-beads-dense (front border network only).
Dew (Dew.cs): border beads as 3D spheres sunk ~1/3 into the skin, drawn per point against the skin depth, body stippled under the
cluster light (+ film rim), tiny crisp glints, stippled contact shadows (only from beads that show), Mist cleared only under visible
bead surface. mist-dew / mist-dew-dense; read best at full A3 (`marks --marks mist-dew`), ~87k strokes.
HatchStyle knobs added for these: `RotateDeg`, `ToneFrom`, `ToneScale`.

## Output: `editions/<seed>/` (git-ignored)

| File | What |
|---|---|
| `overview_<style>.svg/.png` | the whole cluster as an opaque solid |
| `specimen_<style>/bubble_NN` | bubble NN alone, centred, same view direction |
| `context_<style>/bubble_NN` | bubble NN hatched where it sits, the rest of the cluster as faint outlines |
| `manifest.json` | seed, radii, view, per-sheet stroke count, pen-down length, plot-time estimate |
| `cluster.obj` | the relaxed foam (open in Rhino to check) |

The seed fixes everything (cluster, view, light, stroke placement), so any sheet can be regenerated exactly.

SVG layers (Inkscape): `1 hatch`, `2 outline`, `3 context`. Plot them as separate passes (e.g. the context
layer with a lighter pen, or skip it). Strokes are travel-optimised within each layer.

## How the lines are made

- **Tone**: Lambert shading from a fixed upper-left light, plus darkening where the film turns edge-on.
- **Direction**: rings around each bubble's light axis (t = n × A), i.e. along lines of equal brightness, the
  way an engraver hatches a sphere. Each bubble's axis is nudged slightly so neighbours' rings don't line up.
- **Spacing**: evenly spaced streamlines (Jobard & Lefer), from 3 mm in light areas to 0.45 mm in the
  darkest; nothing above the highlight threshold. Strokes never cross a Plateau border.
- **short**: dashes of 0.9-2.2 mm with gaps (the Pasma ray-hatching look). **long**: continuous lines up to
  80 mm.
- **Outlines**: `BorderClean` arcs (the same exact arcs as the GH Border Curves component) and mesh silhouettes,
  tested against a 10 px/mm depth buffer, then smoothed on the sheet.

Tuning knobs live in `HatchStyle` (Hatcher.cs) and `EditionSettings` (Edition.cs).

Sending to the plotter isn't automated here. See `Personal/Practice/Experiments/raspi-plotter/scripts/send-plot.ps1`.

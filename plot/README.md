# bubbleplot: pen-plot editions of bubble clusters

Headless (no Rhino): seeded random cluster → relaxed with the same solver core → orthographic view →
surface-following hatching + Plateau-border arcs + silhouettes, hidden lines removed → A4 SVGs for the
AxiDraw (0.3 mm fine liner) + PNG previews on cream paper.

## Run

```
dotnet build plot -c Release
plot/bin/Release/net9.0/bubbleplot.exe preview --seed 4721            # overview + one bubble, both styles (~10 s)
plot/bin/Release/net9.0/bubbleplot.exe edition --seed 4721 --style short   # every bubble (~20-40 s)
```

Options: `--style short|long|both`, `--count N` (bubbles; default random 9-15), `--detail B` (Border Detail
batches of small bubbles after relaxing), `--bubble K` (which bubble a preview features), `--out DIR`.

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

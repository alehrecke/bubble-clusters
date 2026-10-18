# Bubble Clusters

A Rhino 8 / Grasshopper tool for generating physically correct soap bubble cluster geometry, implemented as C# script components. (Formerly `bubble-tool`, absorbed into CADcoding 2026-07-13.)

**Location / repo:** `D:\AUGBOT\Pneuhaus\Tools\bubble-clusters\` = git repo `github.com/alehrecke/bubble-clusters` (private).
Graduated out of `Pneuhaus/Lab/tools/` on 2026-10-16 (moved whole: the Lab prototype *was* the tool). `runs/` and build output are
git-ignored; `harness/fixtures/` are tracked. Commit after each working change; push to `origin main`.

## Goal

Generate foam-like geometry that strictly follows Plateau's laws across a range of contexts — bubbles landing on curved surfaces (arm, wrist), collecting into clusters, producing Plateau border networks suitable for jewelry and other fabrication outputs.

## Knowledge base

The physics and math reference wiki is at:
`D:\LLM-wikis\llm-bubble\wiki\`

Read these pages at the start of any session before writing code:

| Page | What it covers |
|---|---|
| `bubble-cluster-geometry.md` | 1/rC = 1/rB − 1/rA formula; CSG construction; Grasshopper precedent |
| `plateaus-laws.md` | 120° triple junctions; 109.47° tetrahedral nodes; Steiner proof |
| `kangaroo-grasshopper.md` | Kangaroo LiveSoap; Isopod; CMC vs minimal surface distinction |
| `minimal-surfaces.md` | H = 0; edge minimisation ≠ minimal surface caveat |
| `constant-mean-curvature-surfaces.md` | Young-Laplace; H = ΔP/2γ; CMC for pressurised bubbles |
| `voronoi-interface.md` | Power diagram for unequal-radius adjacency |
| `non-manifold-junctions.md` | Junction handling; T1/T2 topology changes |
| `thin-film-interference.md` | Shader formula if rendering is needed |
| `configurational-transitions.md` | Hysteresis; multiple stable configurations for same volumes |

## Stack

- **Rhino 8** — host geometry kernel (NURBS + mesh)
- **Grasshopper** — parametric graph; user input and pipeline wiring
- **C# Script components** — all core computation
- **Kangaroo 2** — optional physics relaxation (LiveSoap for boundary-frame minimal surfaces)
- **Isopod** — optional implicit surface operations (Rhino 8 package manager)

## Core geometry pipeline

1. **Seed** — place bubble centers on or near a host surface (arm mesh); assign radii
2. **Adjacency** — power diagram (weighted Voronoi by r²) of centers → neighbor graph
3. **Interface spheres** — for each adjacent pair (A, B): rC = rA·rB / (rA − rB); center on line AB
4. **Clipping** — each bubble's visible patch = its own sphere clipped by all neighbor interface spheres
5. **Boundary** — unclipped portions of boundary bubbles = free spherical caps (naturally rounded, no flat Voronoi faces)
6. **Plateau borders** — intersection curves of three mutually-adjacent spherical patches (triple-line curves)
7. **Tetrahedral nodes** — intersection of four Plateau borders (109.47° junctions)
8. **Host surface** — arm mesh as a clipping/grounding volume; contact patches where bubbles touch skin
9. **Output** — Plateau border curves swept to pipes + node fillets → closed solid for fabrication

## Implementation notes

- All surfaces (internal and boundary) are spherical — never use Voronoi cell faces as output geometry
- Distance-field / marching-cubes approach scales better than analytic sphere-patch CSG for large clusters
- Power diagram is preferable to standard Voronoi for heterogeneous bubble sizes
- The CMC condition (pressurised bubbles) requires pressure forces beyond pure area minimisation
- 120° angles at all junctions (including free-surface / interface junctions) fall out automatically from the 1/rC construction
- Wide size variation is a feature: large size contrast → strongly curved interface walls → visual richness

## Design intent

- Visual language: organic, delicate, variable — the 120° angles and broad bubble size range are primary aesthetic drivers
- Target outputs: Plateau border pipe networks → 3D printable / castable jewelry
- Surfaces (films) may be included as thin shells or omitted, leaving only the border skeleton
- Context: bubbles blown onto / collecting on a curved body surface (arm, wrist)

## Current status

**Read `ROADMAP.md` first.** The goal is an interactive/generative equilibrium solver
(blow, drag, collide, seed on a form). It minimises area with each bubble's volume fixed, and Plateau's laws emerge from that.

### Layout (since 2026-10-14)
- `core/`: pure C# (no RhinoCommon), C# 7.3 so it compiles in Rhino's script editor.
  - `Vec3`, `FoamMesh`: non-manifold mesh, each triangle labelled (Neg, Pos) region; 0 = outside.
  - `MobiusMesher`: sphere seeds → watertight film mesh (Saye polygon chopping on the Möbius diagram).
  - `Remesher`: split/collapse/flip; borders are features.
  - `FoamSolver`: area gradient + volume Lagrange multipliers (= pressures), heavy-ball momentum,
    tangential smoothing, remesh when edges drift.
  - `FoamAnalysis`: border chains, 120°/109.47° checks, sphere fit, OBJ I/O with film groups.
  - `ClusterPacker`: soft-sphere pre-pass to the double-bubble spacing.
- `harness/`: `dotnet run -c Release -- bench | replay <runDir> [steps] | obj <file.obj> | tune | debug`.
- `gh/BubbleEngine.cs`: the GH-side logic (live loop, ApplyLive, preview, RunRecorder, RhinoMeshHost), shared by both front ends:
  - `plugin/` (since 2026-10-16): **the compiled plugin**. `dotnet build plugin -c Release` → `dist/BubbleClusters.gha` (net48,
    references Rhino 8's DLLs). Component "Bubble Solver" on the Pneuhaus tab. Install/update: `plugin/README.md`.
  - `gh/BubbleSolver.template.cs`: a ~40-line paste-in shim → `python gh/build_gh.py` → `gh/BubbleSolver.cs` (fallback, same behaviour).
  `gh/check/` compiles the merged script against Rhino 8's DLLs (`cd gh/check && dotnet build`; `GH_CHECK` stubs). `gh/SETUP.md` = wiring.
- `runs/`: GH recordings. When Alex asks about a run, read the newest folder's `log.jsonl`
  and `view_*.png`, run `harness obj` on its `mesh_*.obj`, or `harness replay` the folder.

Workflow: edit core → `harness bench` (must still pass) → `build_gh.py` + `gh/check` build → `dotnet build plugin -c Release`
→ commit + push (`origin main`) → Alex restarts Rhino (plugin) or re-pastes (script).
**Bump `Settings.Build` (gh/BubbleEngine.cs) with every change Alex will run.** It's the first line of Info and the "build" field of each
run's log header. Check it before analysing a run: the 2026-10-18_123315 run was still the previous plugin (Rhino not restarted).
Triangle limit: `Settings.MaxTriangles` = 100k total (raised from 60k on 2026-10-18; `harness budget <runDir>` shows a cluster's cost).

### Phase 1 status (2026-10-14): built and benchmarked headless, not yet run in GH
Benchmarks (`harness bench`, CellSize ≈ r/7–r/12):
- double bubble: wall radius vs Glassner 0.5%; pressures vs 2/r ≤ 1%; borders 120.00° mean
- triple: borders 120.00°, nodes 109.26°. Equal 4-bubble: 120.00° / 109.20°, equal pressures
- random 12 (packed): 119.9° / 109.1°, all bubbles alive, ~6 s to settle
- per-vertex angle scatter is a few degrees (mesh-level); volumes held to ~1e-9
Lessons: mesher phase sets must be face-consistent (else holes); default `long` hash collides
on edge keys (use `LongKey.Comparer`, 15× speedup); momentum 0.9 settles ~10× faster than
plain gradient flow (dt 0.2 is the stability limit); packing must only push apart (pulling
non-neighbours together crushes small bubbles). Rhino compiles scripts with overflow checking on: anything that
relies on integer wrap (hashes) must be `unchecked`; harness + gh/check build with CheckForOverflowUnderflow to match.

First GH test (2026-10-14) froze Rhino. The recording showed Radii typed as 1 / **7**: one
sphere at CellSize 0.1 gave 162k tris, a 24 s build and 8 s per tick, re-scheduled with no gap. Added guards:
triangle-estimate cap (60k), a 40 ms time budget per solution + 15 ms reschedule delay, settle over
200-step windows (2e-5) + a 6000-step cap, and warnings for swallowed bubbles.

Second GH run (2026-10-14): physics right (pressures 2.006/2.878 vs 2/r), but it never settled. Cause:
an "ear", a film strip of triangles whose vertices all lie on the border (from the initial mesh). Its
area pull kinks the border and smoothing undoes it, giving a period-3 cycle. Fix: split "chords" (film edges
joining two border vertices), flip ears, refuse collapses that would create chords, and run a light
split-chords + flip pass every RemeshEvery steps. Saved as `harness/fixtures/double_gh` (`harness trace
fixtures/double_gh 400 0` should show area flat by about step 100). Tried and REJECTED: momentum on the physics
move only. It made the random-12 cluster worse (nodes 103.8°), so momentum stays on the total move.
Perf: GH runs ~8x slower than the Release harness (~10 ms/step on the double bubble, same as a Debug
build). Rhino seems to compile scripts unoptimized. Options: Debug-friendly hot loops (inline Vec3
math) or a Release-built core DLL referenced from the script.

Speed + preview + rearrangement (2026-10-14, after the 10-bubble GH run `fixtures/pack10_gh`):
- Speed: `FoamSolver` step rewritten on flat arrays (Debug build ~48 → ~12 ms/step on 17.8k tris; core
  step ~7 ms). Light maintenance (chords + flips) every 50 steps. VolumeIters 1. GH side: `FoamView`
  caches Rhino meshes/chains per `TopologyVersion` and only copies positions each tick. Full
  `Info` report only every 10 ticks (it costs ~25 ms in Debug). Use `harness perf <fixture>`
  (build with `-c Debug` to see Rhino-like numbers).
- Preview: custom DrawViewportMeshes/Wires (see-through caps, amber walls, thick borders, red nodes).
  Geometry outputs are filled only when wired, to avoid the default preview drawing on top.
- Rearrangement (`core/Rearranger.cs`, Phase 3 v1): detect pinch (mean smallest film angle along a
  border < 25°) or a short border (< 0.5 L between nodes) every 25 steps. Fit f_k = A|x|²+B·x+D to the
  films by least squares (`BubbleFunctions`) and rebuild with `MobiusMesher.Build(fn, ...)`. Cooldown
  75 steps; gives up after 3 refused rebuilds. On pack10: 4 rebuilds, area 65.55 (stuck) → 65.17, valid
  foam (120.00° / 109.2°). Costs ~2 s Release / ~3 s Debug per rebuild (a pause in GH).
- Fold edges (a border edge where one film appears twice) came from the old sliver collapse in
  MobiusMesher. It now uses `Remesher.CollapseShort` (link condition), so folds = 0.
- Settled threshold loosened to 1e-4 per 200 steps (mesh-level area jitter ~5e-5).

15-bubble packed GH run (`fixtures/pack15_gh`, two r 0.35 bubbles at cell 0.1): squeezing + every rebuild refused.
Three fixes:
- **Target volumes = 4/3 pi r^3 from the input radii** (`FoamSolver.SetTargetRadii`, used by GH and all fixture
  commands). Before, targets were the starting diagram's cells, which neighbours carve. Small packed bubbles got
  a fraction of their volume (pressure 14 instead of ~3.5). This was the main cause of "squeezing".
- Rebuild fit: A_k fixed to pressure/4 (unit-gradient sphere). A free fit gave interior bubbles negative A
  (inside-out), which swallowed every other bubble.
- Adaptive edge size: `EdgesPerRadius` 6 (scale per region, floor 1/4 of base). Used by the remesher
  (`Remesher.RegionScale`, a static set around each call), NeedsRemesh, the per-vertex step cap, and rebuilds.
Results: pack15 → one rearrangement, 120.00° (2.5° scatter) / 109.3°, pressures sane. pack10 → residual 0.0016,
worst border 5°. Note: the GH capture showed per-bubble coloured meshes from Alex's own downstream preview
of the wired Bubbles output, not this component's preview.

2026-10-15 GH re-run of a 15-bubble pack: settled at step 407 with 1 rearrangement (7 s pause in GH at 24k tris),
120.0° (1.5° scatter) / 109.4°. Preview confirmed working. Bug: Bubbles output was null because the "is wired" check used
output positions, and R8 script components can have an extra output first. Outputs are now looked up by name.

Rearrangement without freezes (2026-10-15):
- Rebuild = `Rearranger.Prepare` (main thread: fit + snapshot, ~40 ms) + `RebuildJob.Run` (pure geometry). With
  `SolverSettings.AsyncRearrange` (GH: true) Run goes on a Task while the solver keeps stepping. The result is applied in
  `PollRebuild` at the next step, and GH won't settle while `RebuildPending`. Harness default is synchronous, for reproducibility
  (`ASYNC=1` env var for trace).
- `Remesher.RegionScale` static → parameter everywhere (needed for thread safety).
- Rebuild grid at 1.5x edge (`RebuildGridFactor`) + 2 remesh rounds: 5.3 → 3.4 s Debug, same quality (2.0x changed topology).
- Solver's periodic remesh capped at 2 passes per operation: 0.6–1.25 s → 0.3–0.5 s Debug hitches (they happen mostly
  during the first ~300 steps, while bubbles inflate to their target volumes).
- `RearrangeWarmup` 100 steps (fits during inflation always failed).
- Rebuild validation compares with the snapshot volumes (not the targets), and refuses below 50%.
Measured (Debug, pack15, async): longest step 0.5 s (was ~7 s in GH). The background rebuild takes ~5 s.
Possible further speedups: CollapseShort builds a HashSet ring per vertex each pass; better start volumes (scale the
starting spheres so cells ≈ target volumes) would avoid most of the inflation-phase remeshing.

Phase 2 live control (2026-10-15), all in core + GH:
- `DesiredVolume` + `RampVolumes` (≤ `VolumeRate` 2%/step); `SetDesiredRadii` for live resizes (updates the per-vertex scale only).
- `ApplyPull`: per pulled bubble, force VA·Pull·(target − centroid)/R² on its film vertices, subtracted from the area gradient
  before the pressure solve, so volumes stay exact. GH drag semantics: a point pulls only after it moves; target =
  centroid at grab + point offset; `Pull` 0 releases.
- `RequestAdd(radius, seed)` → `StartAdd` picks a random interior node (4 bubble slots) → `Rearranger.PrepareAdd`
  extends the fitted functions with a small sphere (r0 = 1.5 local edge) offset to win at the node → rebuild →
  `Finish` grows the per-region arrays; the new bubble inflates from its seeded volume. Adds queue behind pending rebuilds.
- GH: rebuild signature = bubble count | CellSize | Pack (values are applied live in `ApplyLive`). New inputs
  `Pull`, `Add`, `AddRadius`. Info/recording show events, pull targets and desired radii.
- Harness `live [resize|pull|add]` (`ASYNC=1`, `ROUNDS=n`). Sync: resize → exact volume, wall 0.4% vs Glassner;
  pull 1.94/2.0 in 1000 steps, valid foam; add → exact volume, 120.00°/109.3°. Async (GH mode) is slower to converge
  (each rebuild lands ~170 Release steps later): pull 1.95/2.0 after 2000 steps, clean.
Not yet: pop (T2), separation/new contacts, resizing/dragging added bubbles, mouse picking (Phase 4).

Add-button fix (2026-10-15, GH runs 2026-10-15_0052–0100, fixture `live15_gh`, CellSize 0.15, radii 0.27–1.73):
every add (and some rearrangements) was refused: "bubble 9/14 would shrink to 0–46%". Cause: the rebuild fit, not the grid
(rebuild grid 1.5/1.0/0.75 all gave the same per-bubble ratios). A squeezed tiny interior bubble claimed 15–18x its volume and
neighbours lost ~70%. Fixes:
- `Rearranger.CalibrateVolumes` (inside RebuildJob.Run, background): sample the phase winner on a grid, shift each D so
  claimed volume = current volume (8 damped iterations, step ≤ r/2). Worst error 1510% → 16%; all bubbles 77–100%.
  For adds: calibrate the existing bubbles first, then `PlaceSeed` (otherwise the neighbours squeeze the seed out).
- Interior bubbles (outer film < 10% of their area) take A = 1/(2 r_eff) in the fit (`InteriorFitBySize`); the others keep p/4.
- Accept rebuilds unless a bubble would vanish (< 5%). `Finish` sets TargetVolume = rebuilt volume, and the ramp
  re-inflates ("re-inflating bubble k at x%" in the log).
- Churn guards in `CheckRearrange`: debounce (event seen in 2 consecutive checks), ignore events that come back right after the
  rebuild they triggered (1000 steps), no rebuild while any bubble is < 95% of desired.
live15 scenario (`harness live addfix`): add succeeds, all 16 bubbles at 100% of desired volume, 5 rebuilds over 2400 steps
(the tiny r 0.27 bubble loses ~30% per rebuild and regains it). Benchmarks + pack10/pack15 unchanged.

GH run 2026-10-15_011442: adds r 0.8/1.4/0.4 worked; later r 0.4/0.3/0.1 failed ("new bubble got no volume", once "bubble 18
would vanish"). The seed (r0 ~0.05–0.1) was smaller than the rebuild grid (CellSize 0.15 x 1.5 = 0.225). Fixes in `StartAdd`: r0 ≥ 1.25 grid
cell; radius < 1 grid cell is raised (noted in the log); adds wait until no bubble is re-inflating. `harness live addmany` (Alex's 8
presses): 15 → 23 bubbles, all at 100% volume, 120.00°/109.26°; adds go in one at a time, ~200 steps apart.

**2026-10-15 drag out / drag back (fixture `drag_gh` = run 2026-10-15_213610):** a bubble dragged out separated correctly (pinch
rebuild), but dragged back it passed through everything: pieces with no shared wall don't interact. New `core/Contacts.cs`: group
bubbles into pieces (union-find over walls); for each pair of pieces, contact = outer-film vertices within 0.5 L (spatial hash) or a
vertex inside the other piece (ray parity against its outer films). The event carries `Grow` (the smaller piece's bubbles):
`RebuildJob.Run` lowers their D by ~one rebuild-grid cell before calibration so the pieces overlap, and the diagram makes the wall.
Contacts bypass debounce/ignore. Replay: "contact between bubbles 11 and 1: near (1-bubble piece touches 13-bubble piece)" → merged,
then borders with 6/12 form; volumes 100%. This is also the basis for drift-in adds. Note: when saving a fixture, pick the run folder
by name. Alex may have started another run since (the newest folder was the wrong one once).

**2026-10-15 tiny-bubble run (fixture `tiny_gh`): r 0.087 at CellSize 0.15 never existed.** It kept the volume error at 100%, and
every rebuild refused ("would vanish (NaN)"); the foam "settled" with an unresolved pinch. Fixes: GH raises radii (build + live)
to `MinRadius` = 1.5 x CellSize with a WARNING; core `MarkDead`/`DeadBubbles` (no volume → target 0, skipped in the volume error,
the rebuild validation and live resize; the fit gives dead bubbles D = 1e9 so they never claim space). Harness `CLAMP=1` mimics
the GH floor. Replay raw: rearrangement proceeds, 120.0°/109.3°. Clamped: all bubbles exist, the add succeeds, 120.00°/109.2°, all 100%.

**2026-10-15 CellSize 0.1 run (fixture `cell01_gh`): every rebuild refused, "not watertight (740 open edges)".** A small, still-inflating
bubble had p 0.71, so its A = p/4 described a radius-2.8 sphere that leaked out of the rebuild box. Fixes:
- Fit A from geometry: R = sphere fitted to the bubble's own outer film (else equal-volume radius), clamped to [0.5, 3] x r_eff.
  Pressure is no longer used for A.
- CalibrateVolumes works in radius: boundary moved by (current equivalent radius - target radius), 16 iterations. 10,812% → 8% here,
  1,514% → 3% on live15.
- Seed: offset by an extra -r0 so it wins out to ~r0 (before: ~0.4 r0, sub-grid, near-empty, one 4-film edge), jittered off the node.
- Refusal messages now say how many open / 4+ edges; `RebuildJob.Candidate` keeps the refused mesh; harness `boxleak <dir> <steps>`.
Regression: cell01 replay → 6/6 adds, 120.0°/109.0°; adds28 → settles, 12/13 adds (one refused: it would erase an r 0.2 bubble below the
rebuild grid at CellSize 0.15); addmany 23/23; benchmarks + pack10/pack15 unchanged.

**2026-10-15 rebuild loop (GH run 2026-10-15_012102, fixture `adds28_gh` with log.jsonl):** 13 adds → 28 bubbles (many r 0.2–0.3 at
CellSize 0.15). Afterwards a rebuild every ~140 steps forever, each costing small bubbles 30–55% volume, and the foam never settled.
Cause: the "short border" trigger used the global 0.5 x CellSize, but tiny bubbles are meshed finer and have short, *stable* borders.
Fix in `Rearranger.Detect`: threshold x the local RegionScale, and the border must have shrunk ≥15% since the previous check
(`FoamSolver.borderLengths`). New harness `replaylive <runDir> [steps]` re-applies the recorded adds / radii / pull targets at their steps
(async). adds28: after the last add, 7 rearrangements, then none from step ~4250 to 6000; 120.00°/109.2°, all volumes 100%.
Benchmarks, pack10 and pack15 unchanged.

**2026-10-15: Phase 2 confirmed working in GH by Alex** (resize, drag, add incl. queued small adds).
Open next options: pop (T2) / separation + drift-in contact, local T1 surgery (no lossy rebuilds), Phase 4 mouse picking,
Phase 5 seeding on a host form, Phase 6 fabrication outputs (border pipes + node swelling; panels → Membrane).

**2026-10-15: Phase 3 finished in core, not yet tested in GH.**
- **Pop (T2)**, `core/Pop.cs`: done on the mesh by relabelling, with no rebuild. A bubble with an outer film becomes outside air
  (its walls turn into the neighbours' outer films). A fully interior bubble merges into its biggest neighbour (volumes add; `mergedVolume`
  keeps that through later slider changes). `FoamSolver.RequestPop(k | point)` is queued, applied when no rebuild is pending. Popped
  bubbles go into `DeadBubbles`. GH inputs `Pop` (button) + `PopAt` (point; `Pop.BubbleAt` = containing bubble, else nearest). Harness `live pop`
  (`FIXTURE=`, `ASYNC=1`); `replaylive` replays recorded pops.
- **Rebuilds act locally**, `core/ShapeTransfer.cs`: events carry `At`/`Radius`. After the mesher, every vertex outside the zone
  (event radius + 2 grid cells, 3-cell blend) is moved back onto the old film/border/node with the same labels. Shifts > 0.75 grid cell fade out
  (a real change, e.g. around a dragged bubble), and the weights are eased over the mesh. `SolverSettings.TransferShape` (harness `NOTRANSFER=1` for A/B).
  `Finish` keeps a bubble's target when its radius gap is < 0.5 edge (the Newton step closes it), and ramps only larger gaps. The log line now reports
  "smallest volume kept vs snapshot" (live resizes during a background rebuild made the old metric misleading).
  A/B (smallest kept per rebuild): cell01 ~85% → 96–99%; drag_gh similar, end angles cleaner; adds28 ~50% → ~70–78% (tiny r 0.2 bubbles at the
  0.225 grid are inside the event zones, where the rebuild still decides).
- Adds: avoid nodes next to bubbles < 2 rebuild-grid cells; a refused add retries at another node twice (adds28 28/28, addmany 23/23).
- Benchmarks unchanged (120.00° / 109.2–109.3°).
GH runs 2026-10-15_224201 (14 pops, 8 adds) + _225306 (drag out → contact re-merge, pop, add): pops clean (one ~0.2 s tick each,
neighbours fill the gap), transfer kept 93–99% on most rebuilds. Bug fixed: `MaxSteps` was run-wide, so after step 6000 every press got one
tick and stopped. Three queued adds never ran. Now it counts from the last live change (`BubbleRun.LimitFrom`), and the run won't settle with
adds/pops queued. Every pop in 224201 was "at 0 0 0" (PopAt probably unwired → origin), so the interior-merge path is still untested in GH.
GH run 2026-10-15_230244: PopAt with a gumball point works. Both points tested inside the bubble that popped (bubbles 9 and 1). Bubble 1 had only
3% outer film, so it escaped rather than merged: the rule is "any outer film → escape". Possible tweak (not made): treat < ~10% outer film as
interior, matching the fit's interior rule. The interior merge is still untested in GH.
**2026-10-15: AddAt + Gather (for "bubbles on a plane, added one at a time").**
- `RequestAdd(radius, seed, hasAt, at)`: the add queue now holds requests. A point inside the foam → the nearest border vertex;
  in the open → a free seed right there (`PrepareAdd` grows the rebuild box to include it). A refused add retries the same request.
  `Pop.InsideBubble` = containment without the nearest fallback.
- `SolverSettings.Gather` / `GatherTo`: a capped pull (offset ≤ the bubble's radius) on every live bubble, folded into `ApplyPull`.
  Border events wait while bubbles re-inflate, but contacts are checked anyway (inflating/gathered bubbles passed through before).
- GH: inputs `AddAt`, `Gather`, `GatherTo`. `InputWired()` checks the wires, because unconnected points arrive as (0,0,0). GatherTo not
  connected → the cluster's volume-weighted centre. The settle test also requires centroids to stop drifting (< 0.1 edge per window).
  replaylive replays AddAt and gather strength.
- Harness `live raft`: 3 bubbles on z=0 + 6 AddAt points in the open + Gather 0.5 → all join one piece, volumes exact, inside AddAt
  snaps to a border 0.18 away. With Gather on, borders deviate ~10° (it's a body force on the films); with Gather off they recover to
  120.00°/109.16°. A point-gathered raft doesn't stay flat (|z| up to ~1); a flat raft needs Phase 5 surface contact.
- Regressions OK: pull 1.94/2.0, addmany 23/23, pop (incl. interior merge), benchmarks. drag_gh replay: one run had a one-off
  "4+ film edge" refusal; two reruns had none (async timing varies).
**2026-10-15: Phase 5 v1 - bubbles resting on a host surface (core + GH; tested headless, not yet in GH).**
- `core/HostSurface.cs`: abstract Height/Project/Normal; `PlaneHost`, `SphereHost` (test). GH adds `RhinoMeshHost` (RhinoCommon
  closest point, normal-signed height, calls locked for the background rebuild). It compiles but hasn't been run.
- The solid is region `FoamMesh.Solid = -1`, so every `region > 0` test treats it as outside, which is right for volumes. A bubble on the
  host is closed by a footprint (Solid|k triangles). Dry solid (Solid|0) isn't meshed. `FoamMesh.IsBorder(tris)` = 3+ tris, or 2 tris
  where a film meets a footprint (the contact line). Mismatched 2-tri edges without a footprint are NOT borders: they appear
  transiently in the mesher soup, and treating them as borders broke cell01 rebuilds (inside-out bubble).
- Mesher: host = extra phase n+1 (`HostSurface.Phase` = -1000 x height), relabelled Solid; footprint vertices snapped (`SnapToHost`).
- Solver: footprint triangles have no tension (excluded from the area gradient and the area). On footprint vertices, GA and GV are made
  tangent to the host inside `Pass`, so the pressure solve and Newton step respect the constraint. `SnapWet` after each step.
  The 90 degree contact angle emerges.
- Rebuilds: fit skips footprints; calibration skips samples inside the solid; functions carry `Host`. Contacts: a piece with no footprint
  whose outer film comes within 0.5 L of the host "lands" (rebuild → dome); a piece with a footprint dipping below -0.25 L is "pushed into" it.
  The inside test includes footprints. Pop: a footprint becomes dry and is dropped.
- Analysis: `ContactAngles` (target 90); border/node stats skip host-touching edges/nodes.
- Harness `host [dome|double|raft|ball]`: one r 1 bubble → hemisphere R 1.2610 vs 2^(1/3) = 1.2599, centre on the plane, p within 0.2%,
  contact 88.6 (mesh-level). Two domes: borders 120.00, contact 88.9. Ball host (R 3, 4 bubbles): footprints exactly on the ball, volumes exact.
  Raft: 3 on the plane + 5 dropped, Gather 0.5 → all 8 landed, one flat layer (top z 1.117 for r 0.7), volumes exact, 120.00 / contact 89 once Gather off.
- GH: `HostPlane` (Plane) / `HostMesh` (Mesh) inputs; the host is part of the rebuild signature; footprints aren't drawn (their outline
  shows as a border); with a host, Gather's default target is the cluster centre projected onto it. `inputs.txt` records `host plane ...`, and
  LoadFixture reads it.
- Not yet: lifting a dome off the host (it stays attached), moving the host live, mesh-host recording/replay. Calibration calls Height per
  sample, which may be slow on big Rhino mesh hosts.
**2026-10-16 GH runs reviewed (233324 no host; 000150 / 001029 plane; 002423 / 002904 Alex's hand mesh).**
- The hand-mesh host works in GH (RhinoMeshHost confirmed): the cluster sits on the back of the hand, and bubbles land and press on.
  Rebuilds take ~8-10 s with the mesh host (vs ~5 s on a plane), from the closest-point queries in the mesher and calibration.
- Fixed a repeated landing (002904: "bubble 8 lands" x3, one refused). A bubble hovering within 0.5 L wasn't cut by the solid, so it got
  no footprint. Landing events now carry `Shift`, and `RebuildJob.Run` translates that bubble's function so its lowest point sits one
  rebuild cell inside the solid; calibration refills the volume. Harness `host hover`: 1 landing, hemisphere R 1.2609.
- AddRadius 10 into r ~1 clusters (233324, 000150) took the mesh to 72k tris, 3.7 s ticks, 25 s rebuilds. GH now refuses an add whose estimated
  triangles exceed `Settings.MaxTriangles` and says the largest radius that fits. Live radius changes still have no such guard.
**2026-10-16: speed, round 1.** Measured first: the compiled plugin alone is only ~1.7x per step (the hot loops were already
array-based), so the real costs were whole-mesh operations. Profiled with `harness remeshperf <fixture>` and the per-stage timing now in every
rebuild log line ("calibrate / mesh (chop, clean, slivers, remesh) / transfer / checks").
- `Remesher` rewritten around an incremental vertex→triangle table (`Topo`): edge lookups intersect two small lists; splits, collapses and
  flips update the table in place; passes after the first only scan edges around vertices changed in the previous pass. Same public API,
  same rules. One remesh round on a 45k-tri mesh: 544 → 124 ms.
- Results (single process, Release): rebuild cell01 ~5.3 s → ~2.3 s (remesh 3.0 → 1.1 s); solver periodic remesh 158 → 91 ms; Maintain
  109 → 52 ms; initial build 2.0 → 1.2 s. Regression set unchanged (bench, adds, pop, pull, raft, host dome/hover/ball, cell01/drag/adds28
  replays: 0 refused). The host raft once ended with 7/8 landed and 1 resting on top (valid two-layer state, depends on drop details).
- Mesher: per-node phase cache (chop barely changed: its cost is the polygon clipping, not function evaluation).
Next speed steps: coarse mesh while interacting + refine when idle; mesh-host distance cache (hand-mesh rebuilds); chop/transfer allocations.
**2026-10-18 (build d): rebuilds after huge bubbles.** Run 2026-10-18_125753 (fixture `vanish_gh`; first run on the new plugin):
an r 10 bubble went in, then every rebuild was refused ("bubble 8 would vanish 0%"). Cause: a big bubble's fitted function is deep
(min ~ -R/2) and its sphere runs through the cluster it's pressed against, so it beat small bubbles that weren't even neighbours
(-3.06 vs -0.38 at an r 0.55 bubble's centre); calibration's small steps couldn't win that back.
- `CalibrateVolumes` rescue: a bubble left with < 20% of its volume is first made to win at its snapshot centroid (by A r^2 / 2), at
  most twice, then the regular steps fix volumes (`RebuildJob.Centroid`; `Rearranger.RescueSwallowed` switch). Deterministic repro:
  rescue off → bubble 7 vanishes (worst 100%); on → OK, worst 2%. A "locality mask" (claims only near current territory) was tried
  and REJECTED: 3 cells of dilation are wider than small bubbles, and the hard mask edge made meshes not watertight.
- Calibration samples only spheres binned near each sample: up to 3 s → ~0.6 s mean with big bubbles.
- A rebuilt mesh with an open edge or a 4-film edge (four phases tying on a grid line) is rebuilt on a shifted grid, up to 2 retries
  (`MobiusMesher.Build(..., jitter)`).
- Harness: `replaylive` is now deterministic (rebuild at once, applied `SimulatedDelay` = 60 steps later; `DELAY=0` for real async).
  `failrepro <fixture> <steps> [kind]` replays to the first refused rebuild and re-runs that exact job (`RebuildJob.Retry`, `Fn0`;
  `NORESCUE=1`). `rebuilddebug`, `budget`.
**2026-10-18 (build e): generators (Option C, chosen by Alex).** Architecture: the solver outputs `Foam` = `FoamHandle` (live, in
gh/BubbleEngine.cs); generators sit downstream (no GH cycle), re-solve every solver tick, and push requests; `Wake()` restarts a settled
solver. API: Step/Settled/Busy/CellSize/MinRadius, BubbleIds/Centroid/Radius/Pressure/BubbleAt, Borders/BorderBubbles/Nodes,
Add/AddBatch/Pop/Wake. Scripts use it via `dynamic` (example `gh/examples/NodeSprinkle.cs`, compile-checked).
- Core: `FoamSolver.RequestAddBatch` (many seeds, ONE rebuild; `Rearranger.PrepareAdds`, `PlaceSeed(fn, index, ...)`, RebuildJob
  AddRadii/AddAts/AddR0s; a batch seed that took no space is marked dead, not refused).
- `core/Generators.cs` `BorderDetail.Choose`: borders by length (Where 0/1/2), point along, radius = U(SizeMin, SizeMax) x smallest
  bubble on the border, spacing from picks and nodes; skips host contact lines. Plugin component `BorderDetailComponent` (Seed -1 =
  random, shown in Info; Pace; Reset). Harness `gen <fixture> [seed] [batches] [where]`: pack15 + 4 batches → 17/17 seeds became
  bubbles, volumes exact, identical on rerun with the same seed.
- Recorder logs batches; `replaylive` replays them (and generator Add/Pop).
- Next generator ideas from Alex: stream bed (bubbles float by and collect; needs direct insertion of free bubbles without a rebuild,
  flow fields, removal downstream). Alex wants generative pen plots: random seeds fine, reproducibility nice-to-have.
**2026-10-18 (build f): Stream Bed generator.**
- `FreeBubble.Append` + `FoamSolver.RequestInsertFree`: an icosphere appended as its own piece, no rebuild; queued and applied only with
  no rebuild pending; if it would overlap the foam (gap < 2 edges) it falls back to a normal add. `GrowRegion` extends per-region arrays.
- `FoamSolver.ExtForce` (per-region force density, folded into ApplyPull, volume-neutral). `Contacts.Pieces` (piece per bubble +
  on-host flag).
- `StreamBed.Tick` (core/Generators.cs): stuck = piece on the host (no host: the largest piece); floating bubbles get Flow/r (+ Sink
  toward the host); removed past Exit downstream; spawns on a disc across the flow every `Every` steps, up to MaxFree floating.
  Plugin `StreamBedComponent` (keeps the solver awake; clears forces on Run off; notes "stream generator seed ... settings" in the run log).
  Handle: InsertFree, Remove, FreeBubbleIds, SetForce, ClearForces, Note.
- Harness `stream [steps] [seed] [flow] [sink]`: 3 domes on a plane; sink 0.25 → 36 spawned, all stuck, 38 rebuilds, 0 refused, volumes
  exact; sink 0 → 22 spawned, 7 floated past and were removed, 11 stuck on the domes. `replaylive` re-runs a recorded stream generator.
Still open: true mesh-level T1 surgery (not needed so far); tiny bubbles near the grid limit still lose volume when they're in the event.
UI reference for Phase 4: `llm-bubble/wiki/saharan-bubbles.md` "Interaction design" (tools as scene objects, capped drag, random pops).
Speed plan (proposed to Alex 2026-10-15, not started): compiled core DLL/.gha (~6–8x), coarse mesh while interacting + refine when idle, then optionally
a sphere-proxy real-time layer that displays the Möbius diagram and seeds this solver.

Next (older note): Alex re-runs SETUP.md's double-bubble test in GH, then Phase 2 (live volumes/drag without rebuild).

**⚠ RECOVERY NEEDED (2026-07-13):** the saved Grasshopper document `bubblesScripts.gh` was accidentally deleted during a folder migration. A size/date-matched candidate recovered from Grasshopper's AutoSave cache is sitting in `RECOVERY/bubblesScripts_RECOVERED_CANDIDATE.gh` — **unverified, open it in Grasshopper to confirm before relying on it.** Once resolved, delete the `RECOVERY/` folder and remove this note.

`src/` (the older analytic/power-diagram scripts) and `WIRING_GUIDE.txt` survived intact. They're superseded as outputs but still useful as previews and seeders (see ROADMAP).

Related/possibly-redundant files: `tools/mathematical-components/` has `BubbleFoam.cs`, `BubbleIntersection2.cs`, `BubblePlateau3.cs` — earlier or alternate versions of this same bubble-geometry work, not yet reconciled with `src/` here.

This tool has not yet been ported into the Membrane plugin repo — it's a standalone art piece (jewelry), not currently part of the inflatable-patterning pipeline.

**2026-10-18: big bubbles + Films output.**
- Big bubbles are meshed coarser: `MobiusMesher.ScaleFor` = fine for small bubbles (6 edges per radius, down to 0.25 x CellSize),
  CellSize for ordinary ones, and r / (15 x CellSize) beyond r > 15 x CellSize, capped at 2x. `Remesher.SizeField` grades the
  size per vertex (Grading 0.3 per L; the outside air / host don't constrain), used by the remesher and `NeedsRemesh` (cached per
  topology version). GH triangle guards estimate each bubble at its own size (`BubbleEngine.EstTris`).
  Tested (`harness big double`, r 3 + r 0.6 at CellSize 0.1): 4x cap → 2,555 tris but border scatter 6.7 deg (cause not found:
  not big triangles at the border, not smoothing, not convergence); 2x cap → 3.6 deg, pressures within 0.2%. Coarsening from 6 x h
  degraded the benchmarks, hence the 15 x h threshold (benchmarks unchanged). `big add`: Alex's cluster (fixture `big_gh`) + an
  r 7.07 bubble → joins, 32k tris, 120.00 deg.
- Films output was empty when wired after the run had settled (outputs were filled only if wired at solve time). The plugin now
  always fills outputs (`BubbleEngine.AlwaysOutput`; its preview override replaces GH's default preview). Films = walls between
  bubbles only (one mesh per pair); new output **Caps** (last) = outer films; Bubbles = closed volumes.
- AddAt (run 2026-10-18_121617, fixture `addat_gh`): Alex's AddAt point looked inside the cluster in perspective but was 23 units in
  front of it (y 26.9 vs cluster y -3.6..3.2), so the first add made a free bubble there. The second press sent the identical point
  (it hadn't updated in GH; possibly an internalised point), which was now inside that free bubble, and the old rule (global nearest
  border) put the new bubble on the cluster 23 away. `PlaceAddPoint` now uses the containing bubble's own nearest border, or its
  surface if it has none, and logs the distance for points outside the foam. The component previews AddAt (X marker; outside the
  foam: dotted line to the nearest bubble + "AddAt: d outside" label; included in the clipping box).

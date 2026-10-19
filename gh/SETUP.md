# Bubble solver — Grasshopper setup (Phases 1–5)

## 0. Plugin (recommended since 2026-10-16)
The same component is available compiled: `dotnet build plugin -c Release` builds it and copies it into
`%APPDATA%\Grasshopper\Libraries\`; restart Rhino (see `plugin/README.md`). It appears as **Pneuhaus → Bubbles → Bubble Solver**
with the inputs below. Info's first line shows which build is loaded. The paste-in script (section 1) still works and behaves the same.

## 1. Make the component (paste-in script)
1. Add **Maths › Script › C# Script** (Rhino 8).
2. Open it, select all, paste **`gh/BubbleSolver.cs`** (the merged file, ~1800 lines).
   Don't paste the `.template.cs`; it doesn't contain the solver core.
3. Set the inputs and outputs to match `RunScript`. Use these names, types and access:

| Input | Type hint | Access |
|---|---|---|
| `Centers` | Point3d | List |
| `Radii` | double | List |
| `CellSize` | double | Item |
| `Pack` | bool | Item |
| `Reset` | bool | Item |
| `Run` | bool | Item |
| `Pull` | double | Item |
| `Add` | bool | Item |
| `AddRadius` | double | Item |
| `Pop` | bool | Item |
| `PopAt` | Point3d | Item |
| `AddAt` | Point3d | Item (optional) |
| `Gather` | double | Item |
| `GatherTo` | Point3d | Item (optional) |
| `HostPlane` | Plane | Item (optional) |
| `HostMesh` | Mesh | Item (optional) |
| `Gravity` | double | Item (optional) |

Outputs: `Films`, `Bubbles`, `Borders`, `Nodes`, `Pressures`, `Info`, `Caps`.

## 2. A first test (double bubble, should match Glassner)
- `Centers`: two points, (0,0,0) and (0.8888,0,0)
- `Radii`: 1.0 and 0.7
- `CellSize`: 0.1 (or leave at 0 → smallest radius / 6)
- `Pack`: false; `Run`: a Boolean Toggle; `Reset`: a Button
- Wire `Info` to a Panel.

Keep `Run` **off** while pasting and wiring. Then turn it on. The component re-runs itself, with no Timer
needed. Each run gets about 40 ms of solver time, then a short pause so Rhino stays responsive. It
stops by itself ("SETTLED (area stable)"), or 6000 steps after your last live change (resize, drag, add, pop).

Safety guards: a build or add estimated above 100,000 triangles in total is refused (big bubbles are meshed coarser, so r 7 costs ~11k at CellSize 0.2), and Info suggests a
CellSize. Info also warns when a bubble is swallowed by another, e.g. a radius typo like 7 instead
of 0.7, which is what froze the first test. In Info you should see:
- border angles: mean ≈ 120°, typical deviation a few degrees (mesh-level noise)
- pressures ≈ 2.0 and ≈ 2.9 (= 2/r for r = 1 and 0.7)

## 3. More bubbles
- Feed a cloud of centres plus radii and set `Pack` = true. Packing pushes apart pairs that
  overlap more than a double bubble and pulls the group together, so the cluster starts
  near equilibrium.
- Without packing, badly overlapping spheres can swallow a small bubble (its pressure shows 0).
- Each bubble keeps the volume of a free sphere with its radius. A cluster is "these bubbles, blown separately,
  then pushed together", so it ends up larger than the overlapping starting spheres.
- **Minimum radius = 1.5 x CellSize** (0.225 at CellSize 0.15). Smaller radii, at build or from live sliders, are held at that
  size, with a WARNING in Info. For smaller bubbles, lower CellSize. A bubble that still gets no space (e.g. swallowed while
  packing) is listed in Info and left out; it doesn't block anything.
- Small bubbles get a finer mesh automatically (about 6 triangles across the radius, at most 4x finer than CellSize).
- Cost grows with 1/CellSize². For 12 bubbles of radius 0.5–1.1 at CellSize 0.08, it takes about
  1.5 s to build and about 6 ms per step. Go coarser (e.g. smallest radius / 4) while exploring.

## 3b. Live control (Phase 2)
Only changing **how many** bubbles there are, `CellSize`, `Pack` or `Reset` rebuilds. Everything else is live
while `Run` is on:
- **Resize:** change a value in `Radii` (e.g. from a slider list). That bubble inflates or deflates smoothly
  (at most 2% volume per step) while its neighbours push back.
- **Drag:** set `Pull` > 0 (about 1 is soft, 5 is stiff), then move one of the `Centers` points, e.g. with the gumball on
  referenced Rhino points. Its bubble is pulled by the same offset from where it was when you started moving the point.
  Points you haven't touched don't pull, so the rest of the cluster relaxes freely. `Pull` = 0 releases every bubble.
  Dragging a bubble through the cluster triggers rearrangements on the way.
- **Add:** wire a Button to `Add`. Each press adds a bubble of radius `AddRadius` (<= 0: mean of `Radii`) at a random
  interior node, a point where four bubbles meet. It appears small there and inflates. Added bubbles aren't
  in `Centers` / `Radii`, so they can't be resized or dragged yet. Adding is built in the background (a few seconds).
  Several presses queue up: each add waits until the previous bubbles have inflated (Info shows the queue). The
  smallest bubble an add can hold is about 1.5 x CellSize in radius; smaller requests are raised to that.
- **Pop:** wire a Button to `Pop` and a point to `PopAt` (e.g. a referenced Rhino point you place inside a bubble). Each
  press pops the bubble containing that point (if the point is outside every bubble, the nearest one). A bubble with an
  outer film loses its air and its neighbours bulge into the gap. A bubble fully inside the cluster merges with its
  biggest neighbour, which keeps both volumes. The pop is instant (no rebuild); if a background rebuild is running, it
  waits for that to finish. Popped bubbles ignore their `Radii` value and `Centers` point from then on.
- **Add at a point:** connect a point to `AddAt` (e.g. a referenced Rhino point you move between presses). Inside the foam,
  the new bubble is seeded at the nearest Plateau border. Out in the open, it appears right there as a free bubble and
  inflates. Without `AddAt` connected, Add uses a random interior node as before.
- **Gather:** `Gather` > 0 makes every bubble drift toward `GatherTo` (not connected: the cluster's centre), like floating
  bubbles collecting. Free bubbles join the cluster when they touch. About 0.2 is gentle and 1 is firm. While Gather is on,
  it pushes on the films like gravity, so the angles are a few degrees off. Set it back to 0 once everything has joined,
  and the cluster relaxes to the exact equilibrium. Note: gathering toward a point makes a 3D clump, even if every bubble
  starts on a plane. A flat raft needs a surface for the bubbles to sit on (Phase 5).
`Info` lists the bubble count, drags and the last few live changes. All live changes go into the run recording.

## 3b2. Gravity (settling aid)
`Gravity` gives the films a little weight, toward the host (or world -Z without one). Real soap films are too light for this to
matter, so 0 (the default) is the exact foam. But bubbles resting on a neighbour's flank are neutrally balanced and can sit
"hovering"; a gentle pull (~0.02) lets them roll into the valleys and onto the bed, and in tests the foam converged better. While
it's on, films sag slightly and angles drift a few degrees; set it back to 0 for the final exact equilibrium (positions stay).
Stronger values (0.05+) push harder than needed.

## 3c. Bubbles resting on a surface (Phase 5)
Connect a plane to `HostPlane`, or any mesh to `HostMesh` (an arm, a table, a sculpted form). The side the plane's Z axis
points to, or the side a mesh's normals point to, is air. The other side is a wet solid.
- Bubbles that touch the surface sit on it as domes, meeting it at 90°. A single bubble becomes an exact hemisphere
  holding the same air as a free sphere of its radius (radius x 1.26).
- Bubbles above the surface stay free until they touch it. Then they land (a background rebuild) and become domes.
- **Rafts:** put bubbles on or above the plane and turn on `Gather`. With a host and no `GatherTo`, they collect toward
  the cluster's centre projected onto the surface. They land, slide together and form a flat single layer. Set Gather
  back to 0 at the end for the exact equilibrium.
- Where a bubble sits on the surface, its outline shows as a border line (the "footprint"). The footprint itself isn't
  drawn. The `Bubbles` output includes it, so each bubble mesh is still closed.
- `Info` adds "contact angles on the host (90°)".
- Bubbles that start entirely inside the solid don't exist (a warning says how many).
- Changing the host rebuilds. Plane runs are recorded and can be replayed. Mesh hosts aren't recorded yet.
- Not handled yet: a dome being pulled off the surface (it stays attached), or moving the host live.

## 3d. Generators (Foam output)
The solver's last output, `Foam`, is a live handle to the running simulation. Generators sit downstream of it, read the
current foam and ask the solver for changes. Your own Add / AddAt / Pop inputs keep working alongside them.
- **Border Detail** (Pneuhaus → Bubbles): sprinkles small bubbles along Plateau borders, a batch at a time (one rebuild per
  batch), until `Total`. `SizeMin`/`SizeMax` = new radius as a fraction of the smallest bubble on that border; `Where` 0 any
  border, 1 outer only (the visible ones), 2 inner only; `Spacing` keeps seeds apart; `Pace` = solver steps to wait after a
  batch (0 = until settled). `Seed` -1 = random: Info shows the seed used, so type it into `Seed` to repeat a result you liked
  (the generator's choices repeat; the background rebuild timing in Rhino can still make two runs differ slightly).
- **Stream Bed** (Pneuhaus → Bubbles): bubbles appear around `Source` (a point upstream), drift along `Flow` (a vector: direction,
  and its length is the strength, ~0.3 gentle, ~1 brisk), and stick when they touch the bed (the solver's HostPlane/HostMesh)
  or the cluster already stuck to it. `Sink` drifts them toward the bed (0 = they only stick where they bump into something);
  `Spread` = spawn disc radius across the flow; `Every` = steps between spawns; `RMin`/`RMax` = sizes; `Exit` = floating bubbles
  this far downstream of Source are removed; `MaxFree` caps how many float at once. Floating bubbles are dropped in without a
  rebuild; they join through the normal contact rebuild. Without a host, the largest cluster is what catches them. Seed as above.
- **Your own generator:** a C# Script component with the `Foam` handle as input. Start from `gh/examples/NodeSprinkle.cs`:
  it uses the handle through `dynamic` (no plugin reference needed). Members are listed at the top of that file and on
  `FoamHandle` in `gh/BubbleEngine.cs`. Batches (`foam.AddBatch`) go in with one rebuild; keep seeds a couple of CellSizes apart.

## 3e. Clean borders for fabrication (Border Curves)
**Border Curves** (Pneuhaus → Bubbles) takes the solver's `Foam` output and gives fabrication-ready borders:
- `Curves`: each border between two nodes, smoothed and, where it's within `ArcTol` x CellSize of a circular arc, replaced by that
  exact arc (closed loops by exact circles). Almost all borders qualify: a border is where two near-spherical films meet. The rest
  are smooth degree-3 curves. Good input for MultiPipe.
- `Polylines`: the same borders, evenly resampled (`Spacing`, default CellSize; arcs get at least one point per 10 degrees). For
  mesh thickeners.
- Every border ends exactly on its node points (`Nodes`), so the network is connected.
- `IsArc`, `Outer` (touches the outside air: the visible borders, e.g. for pen plots). `Host` toggles the footprint outlines.
- Clean at the end: while the foam is still relaxing (Info says so), borders keep moving slightly.

## 4. Preview
The component draws its own preview:
- **Outer caps:** see-through pale blue.
- **Inner walls:** amber.
- **Plateau borders:** thick light lines.
- **4-way nodes:** red dots.

Colours, transparency and line weight are in `Settings` at the top of the script. Geometry outputs are only filled when something is wired to them. That keeps Grasshopper's default red/green preview from drawing on top. If you wire an output, you'll see its default preview as well; hide the downstream component's preview if that's distracting.

`Info` refreshes the full angle report every 10 ticks while running, because it's slow in Rhino. In between it shows the current step, area and volume error. Once paused or settled, it always shows the full report.

## 5. Outputs
- `Borders`: Plateau-border polylines, ready to pipe for jewelry.
- `Nodes`: the four-way junctions.
- `Bubbles`: one closed mesh per bubble (for inflatables).
- `Films`: the walls between bubbles, one open mesh per pair of touching bubbles.
- `Caps`: the outer films (each bubble's surface facing the air).
- The plugin always fills its outputs. The paste-in script fills an output only if it's wired when the component solves: wire it before the run settles, or press Reset.

Info starts with "Bubble Clusters build <date+letter>": tell Claude this when something looks off, so it knows which version ran.

## 6. Recordings (how Claude sees your run)
Each rebuild writes `runs/<timestamp>/` (newest 5 kept):
`inputs.txt` (exact replay input), `log.jsonl` (per tick: area, volume error, pressures),
`mesh_kNNNN.obj` and `view_kNNNN.png` snapshots (first tick, every 10 ticks, and when settled).
Tell Claude "look at my latest bubble run". It can replay the same inputs headless and
compare the numbers.

## Known limits (Phase 1)
- Changing any input rebuilds from scratch. Live volume and drag control come in Phase 2.
- Rearrangement (T1) and new contacts are done by rebuilding the mesh when a border pinches or shrinks away, or two
  separate pieces touch. The rebuild runs in the background (a few seconds), and the bubbles keep relaxing meanwhile.
  `Info` says "rearranging..." and then lists it. Away from the event the rebuilt mesh is put back onto the old film
  shape, so only the area around the rearrangement changes. Expect short hitches (<0.5 s) in the first few hundred
  steps while bubbles inflate to their volumes.
- Uniform mesh size: very small bubbles next to big ones need a small CellSize.
- If Grasshopper reports `Component` / `GrasshopperDocument` as unknown, tell Claude. The
  self-rerun loop would then switch to a Timer.

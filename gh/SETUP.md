# Bubble solver — Grasshopper setup (Phases 1–3)

## 0. Plugin (recommended since 2026-10-16)
The same component is available compiled: `dotnet build plugin -c Release` → `dist/BubbleClusters.gha`. Install once
(see `plugin/README.md`: `GrasshopperDeveloperSettings`, add the `dist` folder, tick memory-load, restart Rhino); it appears as
**Pneuhaus → Bubbles → Bubble Solver** with the inputs below. The paste-in script (section 1) still works and behaves the same.

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

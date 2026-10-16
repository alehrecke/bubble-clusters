# Bubble Clusters — solver roadmap (2026-10-14)

Goal: an interactive / generative tool for **physically accurate** bubble clusters (target look:
Da et al., *Double Bubbles Sans Toil and Trouble*, Fig. 1, left image). You can blow bubbles,
drag them, have them collide, or seed them over a form. It outputs (a) Plateau-border
wireframes for jewelry/objects and (b) film/bubble surfaces for inflatables.

## Key reframing: we need equilibrium, not vortex sheets

Da et al. simulate the *motion* of films: wobble and air inertia, using vortex sheets and Biot–Savart,
which is O(N²) and needs Los Topos. The shapes in Fig. 1 are close to **equilibria**: minimum total
film area with each bubble's air volume held fixed. That is the Surface Evolver problem
(`wiki/surface-evolver.md`, `variational-foam-model.md`). Plateau's laws (120° borders,
109.47° nodes, constant-mean-curvature films) are the conditions for this minimum. They **emerge**
from it, so we don't impose them.

So the core is a **quasi-static solver**: area gradient flow with per-bubble volume constraints.
The pressures are its Lagrange multipliers. Making it interactive means we re-solve
continuously while the user changes volumes or positions. A small amount of momentum or damping
can give a "live" feel later, without the cost of a real fluid model.

Why the current `src/` can't get there: analytic sphere constructions (`BubbleCluster_Phase1`,
Glassner's 1/rC formula) are exact for 2 and 3 bubbles only. With 4 or more bubbles, real films stop
being spheres, and the power diagram (`BubblePowerDiagram`) gives flat walls. Both are good as
**initial guesses**, but they aren't the answer.

## Architecture: four layers

```
 Interaction / seeding   blow (dV/dt), drag (centroid pull), add, pop, seed-on-form
          │  per-bubble targets: volume, pull point, pinned?
 Topology manager        detect T1 / T2 / new contacts → rebuild mesh from implicit
          │
 Film solver (core)      non-manifold tri mesh, labelled half-faces
                         area gradient + volume constraints (Lagrange) + remesh
          │
 Outputs                 Plateau border polylines/curves, nodes, films, bubble shells,
                         pipes for jewelry, panels → Membrane / blender-fabrication
```

### Film solver (the heart)
- Mesh: vertices + triangles, each triangle has (regionFront, regionBack). Region 0 = outside air.
  Triple-line edges have 3 triangles, and tetrahedral vertices are where 4 lines meet. The
  area gradient is defined for any topology (`wiki/surface-area-functional.md`), so junctions need no special
  handling in the physics.
- Step: g = −∇Area (times tension). Volumes V_i = sum over the region's faces of signed tetra volumes, with
  ∇V_i = per-vertex face normals. Solve the small N×N system for multipliers λ (pressures) so the
  step keeps volumes fixed. Then apply a Newton correction back to target volumes. This is what Surface Evolver does.
- Mesh quality: split, collapse and flip inside films, plus tangential smoothing. Triple lines are treated as
  feature curves: they are split or collapsed only along themselves.
- Pure C# with no RhinoCommon in the core, so it can move to a compiled .gha, Unity, or a
  standalone app later without a rewrite. The GH script is a thin wrapper.

### Initialisation and rebuild: the Möbius diagram
Per bubble i, take a sphere (c_i, r_i) and f_i(x) = (|x−c_i|² − r_i²) / (2 r_i). Each point
belongs to the bubble with the smallest f, and points with all f > 0 are outside. The wall between
two bubbles (f_i = f_j) is **exactly** Glassner's sphere, with 1/rC = 1/rB − 1/rA, when the bubbles meet at 60°.
This was checked numerically on 2026-10-14, with exact agreement. With 4 or more bubbles, it's a very
good starting point that the solver then corrects. Mesh it with Saye's Voronoi-interface
polygon chopping on a BCC grid (`wiki/voronoi-interface-meshing.md`), which gives shared edges at junctions by
construction.
For a rebuild in the middle of a run, refit each bubble's sphere from the solver: r_i = 2γ / pressure_i, c_i from the
region's centroid and outer-film fit. Then rebuild the mesh and re-relax. Usually only a few steps are needed.

### Topology manager
- **T1**: a Plateau-border edge chain gets shorter than a threshold → rebuild.
- **T2**: a bubble's volume target goes to 0, or it's popped → drop the region and rebuild.
- **New contact**: outer films of two non-neighbours come within ε → rebuild with the new adjacency.
Rebuilding from the implicit diagram is the robust first version. If the "pop" at each rebuild
is visible, add local mesh-level T1 operations later (Los Topos-style).

### Host surfaces (bubbles on an arm, a form, a table)
A film meets a wet solid at 90°. Vertices that touch the host get projected onto it and slide
freely, which gives hemispherical bubbles that sit on the form. This is the same mechanism as seeding on a form.

### Interaction in Rhino
1. First: GH-native. A Timer drives the solve; per-bubble volume sliders; draggable Rhino points
   act as pull targets.
2. Then: a `MouseCallback` + `DisplayConduit` (as Kangaroo does it). Click and hold on empty space
   blows a new bubble, holding on a bubble inflates it, and dragging pulls it. Collisions come for free, because
   bubbles are films that push each other through volume conservation.

### Outputs
- Plateau borders: triple-edge chains → polylines → smoothed curves; nodes = valence-4 vertices.
- Jewelry: pipes along the borders, optionally thicker near nodes (real Plateau borders swell there).
- Inflatables: per-bubble closed shells and per-interface walls as panels → Membrane panel/unroll
  (check `Tools/_docs/DATA-CONTRACTS.md` before defining the export format).

## Phases (each one ends with something to look at in GH)

| # | Deliverable | Validation |
|---|---|---|
| 1 ✅ confirmed in GH (2026-10-14) | Core solver + Möbius-diagram mesher; GH component: centers + volumes → relaxed films + borders | Double bubble: wall radius = Glassner, 120° at the border. Triple bubble: 120°. 4-bubble: 109.47° node |
| 2 ✅ confirmed in GH (2026-10-15) | Live loop: volume sliders, draggable pull points, add at interior node | 10–20 bubbles stay interactive (target ≥ 10 fps) |
| 3 ✅ core (2026-10-15), GH test pending | Topology manager: T1 + contact by rebuild with shape transfer (local in effect), T2 pop/merge by relabel | Drag one bubble through a cluster without the mesh breaking |
| 4 | Mouse interaction: blow / drag / pop | — |
| 5 🟡 v1 (2026-10-15): plane / ball / Rhino mesh hosts, 90° contact, landing, rafts; GH test pending; generative seeding on forms not yet | Generative seeding on host forms + 90° contact | Bubbles sitting on an arm mesh |
| 6 | Fabrication outputs: pipes / nodes for jewelry, panels for inflatables | Printable closed solid; Membrane import |

Phase 1 can be developed and unit-tested outside Rhino (`dotnet` 9 is installed) before the
first paste into GH.

## Alternative kept in reserve: grid threshold dynamics
MBO threshold dynamics + auction dynamics for volumes (`wiki/threshold-dynamics.md`,
`auction-dynamics.md`) handle any topology change automatically on a voxel grid, and suit
large generative foams (100s of bubbles). The films are less accurate, and the grid limits detail.
This could be a "rough generative" mode whose output is polished by the Phase 1 mesh solver.

## Existing code: what's reused
- Seeders (`FoamSeeder_Phase2`, `LayerSeeder`, `ScaffoldSeeder`) → Phase 5 seeding inputs.
- `BubblePowerDiagram`, `BubbleCluster_Phase1` → fast preview / fallback mode, superseded as output.
- `BubbleSkeleton_Pipe` → starting point for Phase 6 pipes.
- `BubbleFoamMesh` / `BubbleVoronoiMesh` → superseded by the Möbius mesher (they weld separate
  sphere meshes instead of building one non-manifold mesh).

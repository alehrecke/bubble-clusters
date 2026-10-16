# bubble-clusters

Physically accurate soap-bubble clusters for Rhino 8 / Grasshopper (Pneuhaus).

The solver minimises total film area with each bubble's air volume held fixed (Surface Evolver-style). Plateau's laws
aren't imposed; they come out of the minimum: films meet in threes at 120°, borders meet in fours at 109.47°, and
every film has constant mean curvature. On top of that:

- live control: resize, drag, add (at a node or a chosen point), pop, gather
- topology changes (T1 rearrangements, new contacts, pops), done locally in effect
- bubbles resting on a host surface (plane, ball, any Rhino mesh), meeting it at 90°

Target outputs: Plateau-border networks for jewelry, and bubble/film panels for inflatables.

## Layout

| Path | What |
|---|---|
| `core/` | the solver, plain C# 7.3, no RhinoCommon (compiles in Rhino's script editor, in .NET, later in Unity) |
| `gh/` | Grasshopper wrapper: `BubbleSolver.template.cs` + `build_gh.py` → `BubbleSolver.cs` (paste into a C# Script component); `SETUP.md` = wiring |
| `gh/check/` | compiles the merged GH script against Rhino 8's DLLs |
| `harness/` | headless benchmarks, scenarios and replays of recorded GH runs (`dotnet run -c Release -- bench`, `live …`, `host …`, `replaylive <run>`) |
| `harness/fixtures/` | recorded GH runs kept as regression cases |
| `src/` | the older analytic / power-diagram scripts (superseded, kept as seeders/previews) |
| `ROADMAP.md` | phases and design |
| `CLAUDE.md` | detailed working notes and status |

Run recordings go to `runs/` (git-ignored).

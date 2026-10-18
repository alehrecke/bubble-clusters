# Bubble Clusters plugin (BubbleClusters.gha)

The compiled version of the bubble solver: same inputs, outputs and behaviour as the paste-in script
(`gh/BubbleSolver.cs`), built in Release from the same files (`core/` + `gh/BubbleEngine.cs`).

## Build

```
dotnet build plugin -c Release
```
This writes `dist/BubbleClusters.gha` **and copies it to `%APPDATA%\Grasshopper\Libraries\`**, which is where
Grasshopper loads plugins from. Then restart Rhino. Needs Rhino 8 at `C:\Program Files\Rhino 8` (net48 build,
referencing RhinoCommon / Grasshopper there).

If Rhino is running, the copy into Libraries can fail because the file is in use (the build prints a warning and
keeps going). Either build with Rhino closed, or, in Rhino, run `GrasshopperDeveloperSettings` and tick "Memory load
*.GHA assemblies using COFF byte arrays" so the file isn't locked.

## Check which build is loaded

The first line of the component's `Info` output is `Bubble Clusters build <date+letter>` (`Settings.Build` in
`gh/BubbleEngine.cs`). If it's older than expected, there's a stale copy somewhere. Keep exactly one copy:
the one in `%APPDATA%\Grasshopper\Libraries\`. Don't also add `dist/` as a library folder, or Grasshopper finds two.

History: on 2026-10-18 Grasshopper kept loading a copy dragged onto the canvas on 2026-10-16 (Grasshopper had copied it
into Libraries), so rebuilds in `dist/` never arrived. That's why the build now copies straight into Libraries.

## Moving from the script component

Drop the plugin component (**Pneuhaus → Bubbles → Bubble Solver**) next to the script, move the wires across (same
names, same order; `Caps` is an extra output at the end), delete the script. Every input after `Radii` is optional.

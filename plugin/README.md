# Bubble Clusters plugin (BubbleClusters.gha)

The compiled version of the bubble solver: same inputs, outputs and behaviour as the paste-in script
(`gh/BubbleSolver.cs`), built in Release from the same files (`core/` + `gh/BubbleEngine.cs`).

## Build

```
dotnet build plugin -c Release
```
→ `dist/BubbleClusters.gha` (net48, so it loads in Rhino 8 in both runtime modes). Needs Rhino 8 installed at
`C:\Program Files\Rhino 8` (it references RhinoCommon / Grasshopper from there).

## Install (once)

1. In Rhino 8, run the command `GrasshopperDeveloperSettings`.
2. Add the folder `D:\AUGBOT\Pneuhaus\Tools\bubble-clusters\dist` to the library folders.
3. Tick **"Memory load *.GHA assemblies using COFF byte arrays"**. Then the .gha file isn't locked while Rhino runs,
   so a new build can replace it at any time.
4. Restart Rhino. The component is on the **Pneuhaus** tab, **Bubbles** group: "Bubble Solver".

## Updating

After a new build, restart Rhino (Grasshopper loads plugins at startup). Existing definitions keep their wiring.

## Moving from the script component

Drop the plugin component next to the script, move the wires across (same names, same order), delete the
script. Every input after `Radii` is optional: unconnected points / planes / meshes count as "not set".

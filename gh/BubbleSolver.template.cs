// Bubble cluster solver — Phase 3: live control + topology changes (rearrange, contact, pop) (see ../ROADMAP.md, setup in ../gh/SETUP.md)
//
// Physically accurate soap bubble clusters: minimum total film area with each bubble's
// volume held fixed (Surface Evolver-style). Plateau's laws come out of the solve:
// 120 degree borders, 109.47 degree nodes, constant-curvature films. Info reports how
// close the mesh is to each one.
//
// INPUTS
//   Centers   : Point3d list  starting bubble centres. Moving one later (e.g. gumball) drags
//                             its bubble by the same offset, when Pull > 0
//   Radii     : double list   bubble radii: each bubble keeps the volume of a free sphere of that
//                             radius. Changing a value later inflates/deflates that bubble live
//   CellSize  : double        mesh resolution (edge length); <= 0 → smallest radius / 6
//   Pack      : bool          slide centres apart so no two overlap more than a double bubble
//   Reset     : bool          rebuild from the inputs. Changing how many bubbles, CellSize or Pack
//                             also rebuilds; changing values of Centers / Radii does not
//   Run       : bool          keep relaxing; stops by itself once settled
//   Pull      : double        drag strength (0 = off and releases all; ~1 soft, ~5 stiff)
//   Add       : bool          button: add a bubble at a random interior node (where 4 bubbles meet)
//   AddRadius : double        radius of added bubbles (<= 0 → mean of Radii)
//   Pop       : bool          button: pop the bubble at PopAt. With an outer film its air escapes;
//                             fully inside the cluster it merges with its biggest neighbour
//   PopAt     : Point3d       a point inside the bubble to pop (outside all: the nearest bubble)
//   AddAt     : Point3d       optional: where Add puts the bubble. Inside the foam → the nearest
//                             Plateau border; out in the open → right there, as a free bubble
//   Gather    : double        every bubble drifts toward GatherTo (0 = off; ~0.2 gentle, ~1 firm),
//                             so free bubbles collect and stick. Angles are slightly off while it's on;
//                             set it back to 0 once they've joined to get the exact equilibrium
//   HostPlane : Plane         optional: a wet surface the bubbles can rest on (the side the Z axis
//                             points to is air). Bubbles touching it become domes meeting it at 90 degrees
//   HostMesh  : Mesh          optional: same, any mesh (an arm, a form); normals point to the air.
//                             HostPlane wins if both are connected. Changing the host rebuilds
//   GatherTo  : Point3d       optional; not connected → the centre of the cluster
//
// OUTPUTS
//   Films     : Mesh list      one mesh per film (inner walls + outer caps)
//   Bubbles   : Mesh list      one closed mesh per bubble, normals outward
//   Borders   : Curve list     Plateau borders (polylines)
//   Nodes     : Point3d list   points where four borders meet
//   Pressures : double list    per bubble, relative to outside (units: tension / length)
//   Info      : string         diagnostics
//
// GENERATED FILE: edit ../core/*.cs or gh/BubbleSolver.template.cs, then run
// `python gh/build_gh.py`. Don't edit the merged gh/BubbleSolver.cs by hand.

#region Usings
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Globalization;
using System.Reflection;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
// @CORE_USINGS
#endregion

// Paste-in shim: Grasshopper calls RunScript; everything happens in BubbleEngine (merged below).
// The compiled plugin (plugin/, BubbleClusters.gha) runs the same engine about 6-8x faster.
public class Script_Instance : GH_ScriptInstance
{
#if GH_CHECK
    // Only for the offline compile check (gh/check); Grasshopper generates this itself.
    public override void InvokeRunScript(IGH_Component owner, object rhinoDocument, int iteration, List<object> inputs, IGH_DataAccess DA) { }
    IGH_Component Component = null;
    GH_Document GrasshopperDocument = null;
#endif
    readonly BubbleEngine engine = new BubbleEngine();

    private void RunScript(List<Point3d> Centers, List<double> Radii, double CellSize, bool Pack, bool Reset, bool Run,
                           double Pull, bool Add, double AddRadius, bool Pop, Point3d PopAt,
                           Point3d AddAt, double Gather, Point3d GatherTo, Plane HostPlane, Mesh HostMesh,
                           ref object Films, ref object Bubbles, ref object Borders, ref object Nodes,
                           ref object Pressures, ref object Info)
    {
        engine.Component = Component;
        engine.GrasshopperDocument = GrasshopperDocument;
        engine.Solve(Centers, Radii, CellSize, Pack, Reset, Run, Pull, Add, AddRadius, Pop, PopAt, AddAt, Gather, GatherTo,
                     HostPlane, HostMesh, ref Films, ref Bubbles, ref Borders, ref Nodes, ref Pressures, ref Info);
    }

    public override BoundingBox ClippingBox { get { return engine.ClippingBox; } }
    public override void DrawViewportMeshes(IGH_PreviewArgs args) { engine.DrawViewportMeshes(args); }
    public override void DrawViewportWires(IGH_PreviewArgs args) { engine.DrawViewportWires(args); }
}

// ===================== ENGINE (merged from gh/BubbleEngine.cs by build_gh.py) =====================
// @ENGINE

// ===================== CORE (merged from ../core/*.cs by build_gh.py) =====================
// @CORE

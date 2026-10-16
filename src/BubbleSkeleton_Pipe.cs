// BubbleSkeleton Pipe — Phase 1 output
//
// INPUTS  (auto-configured from RunScript signature on compile):
//   Borders : Circle list — Plateau border circles from BubbleCluster.Borders
//   PipeR   : double      — pipe cross-section radius (e.g. 0.3 for jewelry scale)
//
// OUTPUTS (auto-configured):
//   Pipes : Brep list — solid torus-section wires → Bake to export

#region Usings
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

public class Script_Instance : GH_ScriptInstance
{
    #region Notes
    /*
      Members:
        RhinoDoc RhinoDocument
        GH_Document GrasshopperDocument
        IGH_Component Component
        int Iteration
    */
    #endregion

    private void RunScript(List<Circle> Borders, double PipeR, ref object Pipes)
    {
        if (Borders == null || Borders.Count == 0) return;

        double r     = PipeR > 0 ? PipeR : 0.3;
        var    pipes = new List<Brep>();

        foreach (Circle c in Borders)
        {
            if (c.Radius < 1e-8) continue;

            Brep[] breps = Brep.CreatePipe(c.ToNurbsCurve(), r, r, true, PipeCapMode.None, 0.01, 0.01);
            if (breps != null)
                foreach (Brep b in breps)
                    if (b != null && b.IsValid) pipes.Add(b);
        }

        Pipes = pipes;
    }
}

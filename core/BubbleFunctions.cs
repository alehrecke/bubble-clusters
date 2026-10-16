using System;
using System.Collections.Generic;

// Per-bubble implicit functions f_k(x) = A_k |x|^2 + B_k . x + D_k, negative inside bubble k.
// A sphere (c, r) is A = 1/(2r), B = -c/r, D = (|c|^2 - r^2)/(2r): the Möbius diagram.
// Any (A, B, D) keeps walls (f_i = f_j) and outer films (f_k = 0) spherical, so fitted
// functions can describe a relaxed cluster for remeshing.
public class BubbleFunctions
{
    public int Count;
    public double[] A, D;
    public Vec3[] B;
    public HostSurface Host;   // optional solid (region FoamMesh.Solid); fixed, never fitted

    // Does the host solid win at x (against every bubble and the outside)?
    public bool SolidWins(Vec3 x)
    {
        if (Host == null) return false;
        double gs = Host.Phase(x);
        if (gs <= 0) return false;
        for (int k = 1; k <= Count; k++) if (-F(k, x) >= gs) return false;
        return true;
    }

    public BubbleFunctions(int n)
    {
        Count = n; A = new double[n]; D = new double[n]; B = new Vec3[n];
    }

    public static BubbleFunctions FromSpheres(IList<Vec3> c, IList<double> r, HostSurface host = null)
    {
        var f = new BubbleFunctions(c.Count);
        for (int i = 0; i < c.Count; i++)
        {
            f.A[i] = 1 / (2 * r[i]);
            f.B[i] = c[i] * (-1 / r[i]);
            f.D[i] = (c[i].LengthSq - r[i] * r[i]) / (2 * r[i]);
        }
        f.Host = host;
        return f;
    }

    // f_k(x) for bubble k (1-based).
    public double F(int k, Vec3 x)
    {
        int i = k - 1;
        return A[i] * x.LengthSq + B[i].X * x.X + B[i].Y * x.Y + B[i].Z * x.Z + D[i];
    }

    // Mesher phase: outside 0, bubble k = -f_k (largest wins).
    public double Phase(int k, Vec3 x) { return k == 0 ? 0 : -F(k, x); }
}

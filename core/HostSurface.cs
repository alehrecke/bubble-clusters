using System;
using System.Collections.Generic;

// A wet solid the bubbles can rest on (Phase 5): a table, a plane, a ball, later an arm mesh.
//
// The solid is region FoamMesh.Solid (-1). Where a bubble sits on it, the bubble is closed by a
// "footprint": film triangles labelled (Solid | k) lying on the surface. Footprints carry no tension
// (a wet solid under a soap film has the same energy wet or dry), and their vertices slide on the
// surface. The film meets the surface at 90 degrees, which isn't imposed: it's what tension balance
// gives when the contact line can only move along the surface. Dry solid (Solid | outside) isn't
// meshed at all. For volumes the solid is just "outside", so footprints close each bubble.
public abstract class HostSurface
{
    // Signed distance: > 0 on the air side, < 0 inside the solid.
    public abstract double Height(Vec3 x);
    // Closest point on the surface.
    public abstract Vec3 Project(Vec3 x);
    // Unit normal at (or near) x, pointing into the air.
    public abstract Vec3 Normal(Vec3 x);

    // Mesher phase for the solid: steep, so it wins right at the surface and nowhere above it.
    public const double Steepness = 1000;
    public double Phase(Vec3 x) { return -Steepness * Height(x); }
}

public class PlaneHost : HostSurface
{
    public readonly Vec3 Origin, N;
    public PlaneHost(Vec3 origin, Vec3 normal) { Origin = origin; N = normal.Normalized(); }
    public override double Height(Vec3 x) { return Vec3.Dot(x - Origin, N); }
    public override Vec3 Project(Vec3 x) { return x - N * Height(x); }
    public override Vec3 Normal(Vec3 x) { return N; }
}

// Bubbles sitting on the outside of a ball (a simple curved host for testing).
public class SphereHost : HostSurface
{
    public readonly Vec3 C; public readonly double R;
    public SphereHost(Vec3 c, double r) { C = c; R = r; }
    public override double Height(Vec3 x) { return (x - C).Length - R; }
    public override Vec3 Project(Vec3 x) { var d = x - C; double l = d.Length; return l > 1e-12 ? C + d * (R / l) : C + new Vec3(R, 0, 0); }
    public override Vec3 Normal(Vec3 x) { var d = x - C; double l = d.Length; return l > 1e-12 ? d / l : new Vec3(1, 0, 0); }
}

using System;
using System.Collections.Generic;
using System.Linq;

public class SolverSettings
{
    public double Tension = 1.0;     // film tension (2 surfaces x surface tension); pressures scale with it
    public double Dt = 0.2;          // time step, in units of (mean edge length)^2
    public double MaxMove = 0.25;    // per-step displacement cap, in mean edge lengths
    public double Smooth = 0.3;      // tangential smoothing per step (0 = off); doesn't change the shape
    public int VolumeIters = 1;      // Newton corrections back to target volumes per step
    public double EdgeLength = 0;    // remesh target edge length (0 = keep the mesher's cell size)
    public int RemeshEvery = 10;     // steps between remesh checks (0 = never); remeshes only if edges are out of range
    public int MaintainEvery = 50;   // steps between light passes (split border chords + Delaunay flips); 0 = never
    public double Momentum = 0.9;    // heavy-ball momentum (0 = plain gradient flow); damps toward rest, no wobble physics
    public double MobilityFloor = 0.3; // min vertex area in the mobility, x EdgeLength^2 (stability)

    // Rearrangement (topology changes by rebuild, see Rearranger).
    public bool Rearrange = true;
    public int RearrangeEvery = 25;  // steps between checks
    public int RearrangeCooldown = 75; // steps after a rebuild before checking again
    public int RearrangeWarmup = 100; // no checks before this step: bubbles are still inflating to their volumes, fits fail
    public double PinchAngle = 25;   // degrees: mean smallest film angle along a border that counts as pinched
    public double ShortBorder = 0.5; // border between two nodes shorter than this x edge length → rebuild
    public double FitPrior = 0.01;   // weight of the sphere prior in the rebuild fit
    public bool InteriorFitBySize = true; // rebuild fit: interior bubbles take A from their size, not their pressure
    public bool AsyncRearrange = false; // build the new mesh on a background thread (GH: true, so Rhino doesn't freeze)
    public int SimulatedDelay = 0;      // harness: with AsyncRearrange, build at once but apply exactly this many steps later (deterministic replays)
    public double RebuildGridFactor = 1.5; // rebuild grid = edge x this (1.5: ~35% faster than 1, same quality; 2 changed topology)
    public int RebuildRemeshIters = 2;
    public bool TransferShape = true; // after a rebuild, put the old film shape back outside the event zone (ShapeTransfer)

    // Adaptive size: about this many edges across each bubble's radius (0 = uniform), never
    // finer than 1/4 of the base edge. Small bubbles otherwise get too few triangles to hold
    // their shape and are crushed (15-bubble GH run, r 0.35 at cell 0.1).
    public double EdgesPerRadius = 6;

    // Live control (Phase 2).
    public double VolumeRate = 0.02; // max change of a bubble's target volume per step, as a fraction (smooth inflate/deflate)
    public double Pull = 0;          // strength of the pull toward each bubble's PullTarget (0 = off; ~1 moderate, ~5 stiff)
    public double Gather = 0;        // every bubble drifts toward GatherTo (0 = off; ~0.2 gentle, ~1 firm), like floating bubbles collecting
    public Vec3 GatherTo;
}

// Surface Evolver-style gradient flow: minimise total film area with each bubble's volume
// fixed. Pressures are the Lagrange multipliers. Plateau's laws are not imposed; they are
// what the minimum looks like, so the 120 / 109.47 degree angles are a check, not an input.
//
// Performance note: Rhino compiles script components without optimisation (measured ~6x
// slower than Release), so the per-step code works on flat double/int arrays with the vector
// math written out by hand instead of Vec3 operators, and topology lookups are precomputed
// in RebuildTopology. Keep new hot-loop code in this style.
public class FoamSolver
{
    public FoamMesh Mesh;
    public SolverSettings S;
    public double[] TargetVolume;    // per region, index 0 unused: the volume enforced this step
    public double[] DesiredVolume;   // per region: where TargetVolume is heading (ramped by VolumeRate)
    public Vec3[] PullTarget;        // per region: point the bubble is pulled toward (if PullOn)
    public bool[] PullOn;
    public double[] Pressure;        // per region relative to outside, index 0 = 0
    public int Steps;
    public double Area, Residual, MaxVolumeError;
    public double EdgeLength;        // length scale for dt, cap, mobility floor and remesh target
    public Remesher.Result LastRemesh;
    public HostSurface Host;         // optional solid the bubbles rest on (see HostSurface); set before stepping
    public int Rearrangements;
    public List<string> RearrangeLog = new List<string>();  // newest last; read and clear by the caller
    int lastRearrangeStep = -1000000, failedRebuilds;
    public double TargetEdge { get { return S.EdgeLength > 0 ? S.EdgeLength : lockedEdge; } }
    public List<long> BorderEdges = new List<long>();
    public List<int> Nodes = new List<int>();

    // ---- topology (rebuilt when connectivity changes) ----
    int nv, nt, nslot;
    int[] tv;                        // 3 per triangle: vertex ids
    int[] cNeg, cPos;                // 3 per triangle: slot of the Neg / Pos region at that corner (-1 = outside)
    int[] tNeg, tPos;                // per triangle regions
    int[] slotStart;                 // per vertex: first slot (nv + 1 entries)
    int[] slotRegion;                // per slot: bubble id (1..n)
    int[] cls;                       // 0 film, 1 border line, 2 node
    int[] nbStart, nbList;           // smoothing neighbours (CSR): film one-ring / 2 border neighbours

    // ---- per-step work arrays ----
    double[] X, X0, prev;            // positions, positions at step start, last displacement (3 per vertex)
    double[] GA;                     // area gradient (3 per vertex)
    double[] VA;                     // vertex area (1/3 of incident triangles)
    double[] GV;                     // volume gradient per slot (3 per slot)
    double[] vol;                    // per region
    double[] nrm, nxt;               // smoothing scratch (3 per vertex)
    double lockedEdge;               // remesh target fixed at the first rebuild so it doesn't drift
    public double[] RegionScale;     // per region edge-size scale (see SolverSettings.EdgesPerRadius)
    double[] vScale;                 // per vertex: smallest scale of its regions

    public FoamSolver(FoamMesh mesh, SolverSettings s)
    {
        Mesh = mesh;
        S = s ?? new SolverSettings();
        TargetVolume = mesh.Volumes();
        DesiredVolume = (double[])TargetVolume.Clone();
        PullTarget = new Vec3[mesh.BubbleCount + 1];
        PullOn = new bool[mesh.BubbleCount + 1];
        Pressure = new double[mesh.BubbleCount + 1];
        lockedEdge = EdgeLength = mesh.MeanEdgeLength();
        var radii = new double[mesh.BubbleCount];
        for (int k = 1; k <= mesh.BubbleCount; k++)
            radii[k - 1] = Math.Pow(3 * Math.Max(TargetVolume[k], 0) / (4 * Math.PI), 1.0 / 3);
        RegionScale = MobiusMesher.SizeScale(radii, lockedEdge, S.EdgesPerRadius);
        RebuildTopology();
        // The mesher's lockedEdge guess is the mean edge, which adaptive sizing pulls down:
        // use the base (largest) target instead when some bubbles are refined.
        if (RegionScale.Skip(1).Any(x => x < 1))
        {
            double maxScaleMean = 0; int cnt = 0;
            foreach (var t in mesh.T)
                if (t.Neg >= 0 && t.Pos >= 0 && Math.Abs(RegionScale[t.Neg] - 1) < 1e-9 && Math.Abs(RegionScale[t.Pos] - 1) < 1e-9)
                {
                    maxScaleMean += (mesh.V[t.B] - mesh.V[t.A]).Length; cnt++;
                }
            if (cnt > 0) { lockedEdge = EdgeLength = maxScaleMean / cnt; }
        }
    }

    public int TopologyVersion;      // bumps whenever connectivity changes (viewers cache on it)

    bool[] wetT;                     // per triangle: a footprint on the host (no tension)
    int[] wetList;                   // vertices on a footprint (slide on the host)

    public void RebuildTopology()
    {
        TopologyVersion++;
        var m = Mesh;
        if (Host != null) MobiusMesher.SnapToHost(m, Host);
        nv = m.V.Count; nt = m.T.Count;
        var regions = m.VertexRegions();
        slotStart = new int[nv + 1];
        var sr = new List<int>();
        for (int v = 0; v < nv; v++)
        {
            slotStart[v] = sr.Count;
            foreach (int r in regions[v]) if (r > 0) sr.Add(r);
        }
        slotStart[nv] = sr.Count;
        slotRegion = sr.ToArray();
        vScale = new double[nv];
        for (int v = 0; v < nv; v++)
        {
            double sc = 1;
            if (RegionScale != null)
                foreach (int r in regions[v]) if (r >= 0 && r < RegionScale.Length && RegionScale[r] < sc) sc = RegionScale[r];
            vScale[v] = sc;
        }
        nslot = slotRegion.Length;

        tv = new int[3 * nt]; cNeg = new int[3 * nt]; cPos = new int[3 * nt];
        tNeg = new int[nt]; tPos = new int[nt];
        wetT = new bool[nt];
        var wetV = new bool[m.V.Count];
        for (int t = 0; t < nt; t++)
        {
            var f = m.T[t];
            tv[3 * t] = f.A; tv[3 * t + 1] = f.B; tv[3 * t + 2] = f.C;
            tNeg[t] = f.Neg; tPos[t] = f.Pos;
            if (FoamMesh.IsSolid(f)) { wetT[t] = true; wetV[f.A] = wetV[f.B] = wetV[f.C] = true; }
            for (int k = 0; k < 3; k++)
            {
                cNeg[3 * t + k] = SlotOf(tv[3 * t + k], f.Neg);
                cPos[3 * t + k] = SlotOf(tv[3 * t + k], f.Pos);
            }
        }

        var edges = m.EdgeTris();
        var ring = new HashSet<int>[nv];
        var border = new List<int>[nv];
        for (int v = 0; v < nv; v++) { ring[v] = new HashSet<int>(); border[v] = new List<int>(2); }
        BorderEdges.Clear();
        foreach (var kv in edges)
        {
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
            ring[a].Add(b); ring[b].Add(a);
            if (m.IsBorder(kv.Value))
            {
                BorderEdges.Add(kv.Key);
                border[a].Add(b); border[b].Add(a);
            }
        }
        cls = new int[nv];
        nbStart = new int[nv + 1];
        var nl = new List<int>();
        Nodes.Clear();
        for (int v = 0; v < nv; v++)
        {
            nbStart[v] = nl.Count;
            if (border[v].Count == 0) { cls[v] = 0; nl.AddRange(ring[v]); }
            else if (border[v].Count == 2) { cls[v] = 1; nl.AddRange(border[v]); }
            else { cls[v] = 2; Nodes.Add(v); }
        }
        nbStart[nv] = nl.Count;
        nbList = nl.ToArray();
        var wl = new List<int>();
        for (int v = 0; v < nv; v++) if (wetV[v]) wl.Add(v);
        wetList = wl.ToArray();

        X = new double[3 * nv]; X0 = new double[3 * nv]; GA = new double[3 * nv]; VA = new double[nv];
        nrm = new double[3 * nv]; nxt = new double[3 * nv];
        GV = new double[3 * nslot];
        vol = new double[m.BubbleCount + 1];
        if (prev == null || prev.Length != 3 * nv) prev = new double[3 * nv];
    }

    int SlotOf(int v, int region)
    {
        if (region == 0) return -1;
        for (int s = slotStart[v]; s < slotStart[v + 1]; s++) if (slotRegion[s] == region) return s;
        return -1;
    }

    public int VertexClass(int v) { return cls[v]; }

    // Target volumes from the requested radii (4/3 pi r^3): each bubble keeps the air it was
    // blown with. Without this the targets are the starting diagram's cells, which neighbours
    // have already carved. Small packed bubbles were then squeezed to a fraction of their
    // intended size (15-bubble GH run). Also resets the adaptive size scale.
    public void SetTargetRadii(IList<double> radii)
    {
        for (int k = 1; k <= Mesh.BubbleCount && k - 1 < radii.Count; k++)
            DesiredVolume[k] = TargetVolume[k] = 4.0 / 3.0 * Math.PI * radii[k - 1] * radii[k - 1] * radii[k - 1];
        RegionScale = MobiusMesher.SizeScale(radii, lockedEdge, S.EdgesPerRadius);
        MarkDead();
        RebuildTopology();
    }

    // Bubbles with no volume in the mesh (smaller than the grid at build, or swallowed while
    // packing) are excluded: target 0, never counted in volume error, never block a rebuild.
    // In Alex's run an r 0.09 bubble at CellSize 0.15 never existed, but it kept the volume error at
    // 100% and made every rebuild refuse ("would vanish (NaN)").
    public List<int> DeadBubbles = new List<int>();

    public void MarkDead()
    {
        var vol = Mesh.Volumes();
        for (int k = 1; k <= Mesh.BubbleCount; k++)
            if (!(vol[k] > 1e-12) && !DeadBubbles.Contains(k))
            {
                DeadBubbles.Add(k);
                TargetVolume[k] = DesiredVolume[k] = 0;
            }
    }

    public bool IsDead(int k) { return DeadBubbles.Contains(k); }

    // Live resize: bubbles inflate/deflate toward these radii at S.VolumeRate per step.
    // Radii beyond the list (bubbles added later) keep their current desired size.
    public void SetDesiredRadii(IList<double> radii)
    {
        bool scaleChanged = false;
        for (int k = 1; k <= Mesh.BubbleCount && k - 1 < radii.Count; k++)
        {
            if (IsDead(k)) continue; // a bubble that doesn't exist can't be inflated (Reset to rebuild it)
            DesiredVolume[k] = 4.0 / 3.0 * Math.PI * radii[k - 1] * radii[k - 1] * radii[k - 1] + (k < mergedVolume.Length ? mergedVolume[k] : 0);
            double sc = MobiusMesher.ScaleFor(radii[k - 1], lockedEdge, S.EdgesPerRadius);
            if (Math.Abs(sc - RegionScale[k]) > 0.05) { RegionScale[k] = sc; scaleChanged = true; sizeFieldVersion = -1; }
        }
        if (scaleChanged) UpdateVertexScale();
    }

    public double DesiredRadius(int k) { return Math.Pow(3 * DesiredVolume[k] / (4 * Math.PI), 1.0 / 3); }

    // ---- popping (see Pop) ----
    // Volume a bubble took over from merged neighbours; SetDesiredRadii adds it back on top of the
    // radius input, so a later slider change doesn't shrink the merged bubble back to one input.
    double[] mergedVolume = new double[0];
    readonly List<KeyValuePair<int, Vec3>> pendingPops = new List<KeyValuePair<int, Vec3>>();
    public int Popped;
    public int PendingPops { get { return pendingPops.Count; } }

    // Pop bubble k, or (k = 0) the bubble at point `at`, which is resolved when the pop happens.
    // Applied at the next step with no background rebuild running (that rebuild would bring the
    // bubble back).
    public void RequestPop(int k, Vec3 at = default(Vec3)) { pendingPops.Add(new KeyValuePair<int, Vec3>(k, at)); }

    void ApplyPops()
    {
        foreach (var req in pendingPops.ToList())
        {
            pendingPops.Remove(req);
            int k = req.Key > 0 ? req.Key : Pop.BubbleAt(Mesh, req.Value);
            var res = Pop.Apply(Mesh, k);
            RearrangeLog.Add("step " + Steps + ": " + res);
            if (res.Fail != null) continue;
            if (res.Into > 0)
            {
                int j = res.Into;
                TargetVolume[j] += res.Volume;
                DesiredVolume[j] += DesiredVolume[k];
                if (mergedVolume.Length <= j) Array.Resize(ref mergedVolume, Mesh.BubbleCount + 1);
                mergedVolume[j] += DesiredVolume[k]; // already includes anything k had merged
                if (PullOn[k] && !PullOn[j]) { PullOn[j] = true; PullTarget[j] = PullTarget[k]; }
            }
            PullOn[k] = false;
            TargetVolume[k] = DesiredVolume[k] = 0;
            Pressure[k] = 0;
            if (!DeadBubbles.Contains(k)) DeadBubbles.Add(k);
            Popped++;
            prev = null;
            RebuildTopology();
        }
    }

    void UpdateVertexScale()
    {
        for (int v = 0; v < nv; v++)
        {
            double sc = 1;
            for (int sl = slotStart[v]; sl < slotStart[v + 1]; sl++)
                if (RegionScale[slotRegion[sl]] < sc) sc = RegionScale[slotRegion[sl]];
            vScale[v] = sc;
        }
    }

    // Volume-weighted centroid of each bubble (index 0 unused).
    public Vec3[] Centroids()
    {
        var m = Mesh;
        var c = new Vec3[m.BubbleCount + 1];
        var v = new double[m.BubbleCount + 1];
        foreach (var t in m.T)
        {
            Vec3 a = m.V[t.A], b = m.V[t.B], cc = m.V[t.C];
            double tv = Vec3.Dot(a, Vec3.Cross(b, cc)) / 6.0;
            Vec3 tc = (a + b + cc) / 4.0;
            if (t.Neg > 0) { v[t.Neg] += tv; c[t.Neg] = c[t.Neg] + tc * tv; }
            if (t.Pos > 0) { v[t.Pos] -= tv; c[t.Pos] = c[t.Pos] - tc * tv; }
        }
        for (int k = 1; k <= m.BubbleCount; k++) if (v[k] > 0) c[k] = c[k] / v[k];
        return c;
    }

    void Load()
    {
        var V = Mesh.V;
        for (int v = 0; v < nv; v++) { var p = V[v]; X[3 * v] = p.X; X[3 * v + 1] = p.Y; X[3 * v + 2] = p.Z; }
    }

    void Store()
    {
        var V = Mesh.V;
        for (int v = 0; v < nv; v++) V[v] = new Vec3(X[3 * v], X[3 * v + 1], X[3 * v + 2]);
    }

    // One pass over the triangles: volumes per region and their gradients per slot; with
    // withArea also the area gradient, vertex areas and the total area.
    double Pass(bool withArea)
    {
        Array.Clear(GV, 0, GV.Length);
        Array.Clear(vol, 0, vol.Length);
        if (withArea) { Array.Clear(GA, 0, GA.Length); Array.Clear(VA, 0, VA.Length); }
        double area = 0;
        double[] x = X;
        for (int t = 0; t < nt; t++)
        {
            int ia = 3 * tv[3 * t], ib = 3 * tv[3 * t + 1], ic = 3 * tv[3 * t + 2];
            double ax = x[ia], ay = x[ia + 1], az = x[ia + 2];
            double bx = x[ib], by = x[ib + 1], bz = x[ib + 2];
            double cx = x[ic], cy = x[ic + 1], cz = x[ic + 2];
            if (withArea && !wetT[t])   // footprints on a host carry no tension
            {
                double ux = bx - ax, uy = by - ay, uz = bz - az, wx = cx - ax, wy = cy - ay, wz = cz - az;
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 1e-300)
                {
                    double s = 0.5 / len; nx *= s; ny *= s; nz *= s; // half unit normal
                    // grad_a = 0.5 n x (c - b), grad_b = 0.5 n x (a - c), grad_c = 0.5 n x (b - a)
                    double ex = cx - bx, ey = cy - by, ez = cz - bz;
                    GA[ia] += ny * ez - nz * ey; GA[ia + 1] += nz * ex - nx * ez; GA[ia + 2] += nx * ey - ny * ex;
                    ex = ax - cx; ey = ay - cy; ez = az - cz;
                    GA[ib] += ny * ez - nz * ey; GA[ib + 1] += nz * ex - nx * ez; GA[ib + 2] += nx * ey - ny * ex;
                    ex = bx - ax; ey = by - ay; ez = bz - az;
                    GA[ic] += ny * ez - nz * ey; GA[ic + 1] += nz * ex - nx * ez; GA[ic + 2] += nx * ey - ny * ex;
                    double third = len / 6.0;
                    VA[ia / 3] += third; VA[ib / 3] += third; VA[ic / 3] += third;
                    area += 0.5 * len;
                }
            }
            // Volume: v = a.(b x c)/6; grad_a = (b x c)/6, grad_b = (c x a)/6, grad_c = (a x b)/6.
            double dax = (by * cz - bz * cy) / 6, day = (bz * cx - bx * cz) / 6, daz = (bx * cy - by * cx) / 6;
            double dbx = (cy * az - cz * ay) / 6, dby = (cz * ax - cx * az) / 6, dbz = (cx * ay - cy * ax) / 6;
            double dcx = (ay * bz - az * by) / 6, dcy = (az * bx - ax * bz) / 6, dcz = (ax * by - ay * bx) / 6;
            double tvol = ax * dax + ay * day + az * daz;
            int k0 = 3 * t;
            int neg = tNeg[t], pos = tPos[t];
            if (neg > 0)
            {
                vol[neg] += tvol;
                int s0 = 3 * cNeg[k0], s1 = 3 * cNeg[k0 + 1], s2 = 3 * cNeg[k0 + 2];
                GV[s0] += dax; GV[s0 + 1] += day; GV[s0 + 2] += daz;
                GV[s1] += dbx; GV[s1 + 1] += dby; GV[s1 + 2] += dbz;
                GV[s2] += dcx; GV[s2 + 1] += dcy; GV[s2 + 2] += dcz;
            }
            if (pos > 0)
            {
                vol[pos] -= tvol;
                int s0 = 3 * cPos[k0], s1 = 3 * cPos[k0 + 1], s2 = 3 * cPos[k0 + 2];
                GV[s0] -= dax; GV[s0 + 1] -= day; GV[s0 + 2] -= daz;
                GV[s1] -= dbx; GV[s1 + 1] -= dby; GV[s1 + 2] -= dbz;
                GV[s2] -= dcx; GV[s2 + 1] -= dcy; GV[s2 + 2] -= dcz;
            }
        }
        // Footprint vertices can only slide on the host: keep the tangential part of every force.
        // The pressure solve then sees the same constraint, and volumes stay exact.
        if (Host != null)
            foreach (int v in wetList)
            {
                int i = 3 * v;
                Vec3 n = Host.Normal(new Vec3(x[i], x[i + 1], x[i + 2]));
                if (withArea)
                {
                    double d = GA[i] * n.X + GA[i + 1] * n.Y + GA[i + 2] * n.Z;
                    GA[i] -= d * n.X; GA[i + 1] -= d * n.Y; GA[i + 2] -= d * n.Z;
                }
                for (int sl = slotStart[v]; sl < slotStart[v + 1]; sl++)
                {
                    int j = 3 * sl;
                    double d = GV[j] * n.X + GV[j + 1] * n.Y + GV[j + 2] * n.Z;
                    GV[j] -= d * n.X; GV[j + 1] -= d * n.Y; GV[j + 2] -= d * n.Z;
                }
            }
        return area;
    }

    // Put footprint vertices back on the host (moves are tangential to first order; a curved host
    // needs the correction).
    void SnapWet()
    {
        if (Host == null) return;
        foreach (int v in wetList)
        {
            int i = 3 * v;
            var q = Host.Project(new Vec3(X[i], X[i + 1], X[i + 2]));
            X[i] = q.X; X[i + 1] = q.Y; X[i + 2] = q.Z;
        }
    }

    double Mob(int v)
    {
        double floor = S.MobilityFloor * EdgeLength * EdgeLength;
        double a = VA[v];
        return 1.0 / (a > floor ? a : floor);
    }

    // True when more than 1% of edges have drifted outside [0.5 L, 1.6 L] x the local size.
    // Uses the remesher's graded size field (cached per topology version: vertices move slowly).
    double[] sizeField; int sizeFieldVersion = -1; double[] sizeFieldScale;
    public bool NeedsRemesh()
    {
        double L = S.EdgeLength > 0 ? S.EdgeLength : lockedEdge;
        double lo = 0.25 * L * L, hi = 2.56 * L * L;
        if (sizeFieldVersion != TopologyVersion || sizeFieldScale != RegionScale)
        {
            sizeField = Remesher.SizeField(Mesh, RegionScale, L);
            sizeFieldVersion = TopologyVersion; sizeFieldScale = RegionScale;
        }
        int bad = 0;
        var V = Mesh.V;
        foreach (var t in Mesh.T)
        {
            Vec3 a = V[t.A], b = V[t.B];
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z, l2 = dx * dx + dy * dy + dz * dz;
            if (sizeField != null && t.A < sizeField.Length && t.B < sizeField.Length)
            {
                double fs = Math.Min(sizeField[t.A], sizeField[t.B]); fs *= fs;
                if (l2 < lo * fs || l2 > hi * fs) bad++;
                continue;
            }
            // Same rule as Remesher.EdgeScale: the smaller bubble decides; outside / solid don't constrain.
            double sc = 1;
            if (RegionScale != null)
            {
                double sn = t.Neg > 0 ? RegionScale[t.Neg] : double.MaxValue, sp = t.Pos > 0 ? RegionScale[t.Pos] : double.MaxValue;
                sc = Math.Min(sn, sp);
                if (sc == double.MaxValue) sc = 1;
            }
            sc *= sc;
            if (l2 < lo * sc || l2 > hi * sc) bad++;
        }
        return bad > 0.01 * Mesh.T.Count;
    }

    public void Remesh()
    {
        double L = S.EdgeLength > 0 ? S.EdgeLength : lockedEdge;
        LastRemesh = Remesher.Run(Mesh, L, 1, RegionScale, 2);
        prev = null;
        RebuildTopology();
        EdgeLength = L;
    }

    // Light pass: split border chords + Delaunay flips, without the full remesh.
    public void Maintain()
    {
        int changed = Remesher.SplitChords(Mesh) + Remesher.FlipDelaunay(Mesh, 2);
        if (changed == 0) return;
        if (Mesh.V.Count != nv) prev = null;
        RebuildTopology();
    }

    // Swap in a rebuilt mesh (same bubbles, same target volumes).
    public void ReplaceMesh(FoamMesh mesh)
    {
        Mesh = mesh;
        prev = null;
        RebuildTopology();
        EdgeLength = TargetEdge;
    }

    Rearranger.RebuildJob pendingJob;
    System.Threading.Tasks.Task pendingTask;
    public bool RebuildPending { get { return pendingJob != null; } }
    int pendingReadyStep;

    // ---- adding bubbles ----
    class AddRequest { public double Radius; public bool HasAt; public Vec3 At; }
    readonly List<AddRequest> addQueue = new List<AddRequest>();
    AddRequest currentAdd;
    int addRetries; Random addRng;

    // Queue a new bubble of the given radius. Without a point it's seeded at a random interior
    // four-way node (a point where four bubbles meet). With a point (AddAt):
    //   inside the foam  -> the nearest Plateau border point (a new bubble wedged into that border)
    //   outside the foam -> right there, as a free bubble (it joins the cluster when it touches,
    //                       e.g. pulled in by Gather)
    // It's inserted by a rebuild (background if AsyncRearrange) and then inflates at VolumeRate.
    // Several requests queue up.
    public void RequestAdd(double radius, int seed) { RequestAdd(radius, seed, false, Vec3.Zero); }

    public void RequestAdd(double radius, int seed, bool hasAt, Vec3 at)
    {
        if (addRng == null) addRng = new Random(seed);
        addQueue.Add(new AddRequest { Radius = radius, HasAt = hasAt, At = at });
    }

    public int PendingAdds { get { return addQueue.Count; } }

    // Where a requested AddAt point actually seeds (see RequestAdd).
    // A point inside bubble k seeds on k's own nearest border; if k has none (a free bubble), on k's
    // surface nearest the point, so the new bubble attaches to the one pointed into. Before, the nearest
    // border anywhere was used: in Alex's run 2026-10-18_121617 a point inside a free bubble 23 units from
    // the cluster put the new bubble on the cluster.
    Vec3 PlaceAddPoint(Vec3 p, out string where)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        int k = Pop.InsideBubble(Mesh, p);
        if (k == 0)
        {
            double dn;
            NearestFoamPoint(p, out dn);
            where = "at the given point, outside the foam (" + dn.ToString("G3", ci) + " from the nearest bubble)";
            return p;
        }
        int best = -1; double bd = double.MaxValue;
        foreach (long e in BorderEdges)
            foreach (int v in new[] { (int)(e >> 32), (int)(e & 0xffffffff) })
            {
                if (SlotOf(v, k) < 0) continue;
                double d = (Mesh.V[v] - p).LengthSq;
                if (d < bd) { bd = d; best = v; }
            }
        if (best >= 0)
        {
            where = "on bubble " + k + "'s nearest border (" + Math.Sqrt(bd).ToString("G2", ci) + " from the point)";
            return Mesh.V[best];
        }
        foreach (var t in Mesh.T)
        {
            if (!((t.Neg == k && t.Pos == 0) || (t.Pos == k && t.Neg == 0))) continue;
            foreach (int v in new[] { t.A, t.B, t.C })
            {
                double d = (Mesh.V[v] - p).LengthSq;
                if (d < bd) { bd = d; best = v; }
            }
        }
        if (best < 0) { where = "at the given point"; return p; }
        where = "on bubble " + k + "'s surface nearest the point (it has no borders yet)";
        return Mesh.V[best];
    }

    // Nearest mesh vertex to p and its distance (shows where an AddAt point is relative to the foam).
    public Vec3 NearestFoamPoint(Vec3 p, out double dist)
    {
        double bd = double.MaxValue; Vec3 best = p;
        foreach (var q in Mesh.V) { double d = (q - p).LengthSq; if (d < bd) { bd = d; best = q; } }
        dist = Math.Sqrt(bd);
        return best;
    }

    // Interior nodes (4 bubbles, no outside air) first, then any node, then any border
    // vertex, then the centroid of bubble 1. Nodes next to a bubble under 2 rebuild-grid cells in
    // radius are avoided when possible: the seed can erase it ("bubble 24 would vanish" in the
    // adds28 replay, where many bubbles were r 0.2 at a 0.225 grid).
    Vec3 PickAddPoint(out string where)
    {
        var interior = new List<int>(); var any = new List<int>();
        double small = 2 * TargetEdge * S.RebuildGridFactor;
        foreach (int v in Nodes)
        {
            any.Add(v);
            int cnt = slotStart[v + 1] - slotStart[v];
            // slots hold only bubbles; a node touching outside has 3 bubble slots
            if (cnt < 4) continue;
            interior.Add(v);
        }
        Func<List<int>, List<int>> roomy = l => l.Where(v =>
        {
            for (int sl = slotStart[v]; sl < slotStart[v + 1]; sl++) if (DesiredRadius(slotRegion[sl]) < small) return false;
            return true;
        }).ToList();
        var ri = roomy(interior);
        if (ri.Count > 0) interior = ri;
        var ra = roomy(any);
        if (ra.Count > 0) any = ra;
        var pool = interior.Count > 0 ? interior : any;
        if (pool.Count > 0)
        {
            where = interior.Count > 0 ? "interior node" : "surface node";
            return Mesh.V[pool[addRng.Next(pool.Count)]];
        }
        if (BorderEdges.Count > 0)
        {
            where = "border";
            long e = BorderEdges[addRng.Next(BorderEdges.Count)];
            return Mesh.V[(int)(e >> 32)];
        }
        where = "bubble 1 centre";
        return Centroids()[1];
    }

    void StartAdd()
    {
        currentAdd = addQueue[0];
        addQueue.RemoveAt(0);
        string where;
        Vec3 at = currentAdd.HasAt ? PlaceAddPoint(currentAdd.At, out where) : PickAddPoint(out where);
        // The seed must be at least about one rebuild-grid cell across, or the grid misses it
        // ("the new bubble got no volume" for r 0.1–0.4 at CellSize 0.15, where the grid is 0.225).
        // A bubble requested smaller than the grid can hold is raised to that size.
        double grid = TargetEdge * S.RebuildGridFactor;
        double radius = currentAdd.Radius, minRadius = 1.0 * grid;
        string note = "";
        if (radius < minRadius)
        {
            note = "; radius " + radius.ToString("G3", System.Globalization.CultureInfo.InvariantCulture) + " raised to " +
                   minRadius.ToString("G3", System.Globalization.CultureInfo.InvariantCulture) + " (smallest this CellSize can hold)";
            radius = minRadius;
        }
        double r0 = Math.Max(1.25 * grid, 1.5 * TargetEdge * Math.Max(0.25, Math.Min(1, S.EdgesPerRadius > 0 ? radius / (S.EdgesPerRadius * TargetEdge) : 1)));
        var job = Rearranger.PrepareAdd(this, at, r0, radius);
        job.What += " (" + where + ")" + note;
        if (S.AsyncRearrange)
        {
            pendingJob = job;
            if (S.SimulatedDelay > 0) { job.Run(); pendingTask = System.Threading.Tasks.Task.FromResult(0); pendingReadyStep = Steps + S.SimulatedDelay; }
            else pendingTask = System.Threading.Tasks.Task.Run(() => job.Run());
        }
        else
        {
            job.Run();
            Finish(job);
        }
    }

    // Churn guards (live 15-bubble run: one short border triggered a rebuild every ~225 steps
    // five times in a row, because each rebuild recreated it):
    //   - debounce: an event must be seen in two consecutive checks
    //   - events that triggered the last rebuild and are seen again right after it didn't help;
    //     ignore those borders for IgnoreSteps
    //   - no rebuilds while any bubble is still re-inflating (target < 95% of desired)
    HashSet<string> seenLastCheck = new HashSet<string>();
    HashSet<string> triggeredLast = new HashSet<string>();
    Dictionary<string, int> ignoreUntil = new Dictionary<string, int>();
    bool firstCheckAfterRebuild;
    Dictionary<string, double> borderLengths = new Dictionary<string, double>(); // per border, at the last check
    const int IgnoreSteps = 1000;

    static string Key(Rearranger.Event e) { return e.Kind + ":" + string.Join("-", e.Regions); }

    bool Reinflating()
    {
        for (int k = 1; k < TargetVolume.Length; k++)
            if (TargetVolume[k] < 0.95 * DesiredVolume[k]) return true;
        return false;
    }

    void CheckRearrange()
    {
        if (pendingJob != null || Steps < S.RearrangeWarmup) return;
        if (Steps - lastRearrangeStep < S.RearrangeCooldown || failedRebuilds >= 3) return;
        // While bubbles re-inflate, border events wait (fits fail on half-inflated bubbles), but new
        // contacts are still checked: an inflating or gathered bubble would otherwise pass straight
        // through its neighbours.
        var all = Reinflating() ? Contacts.Detect(this) : Rearranger.Detect(this, borderLengths);
        var keys = new HashSet<string>(all.Select(Key));
        if (firstCheckAfterRebuild)
        {
            foreach (var k in keys)
                if (triggeredLast.Contains(k) && !ignoreUntil.ContainsKey(k))
                {
                    ignoreUntil[k] = Steps + IgnoreSteps;
                    RearrangeLog.Add("step " + Steps + ": " + k + " came back after a rebuild; ignoring it for " + IgnoreSteps + " steps");
                }
            firstCheckAfterRebuild = false;
        }
        foreach (var k in ignoreUntil.Where(kv => kv.Value <= Steps).Select(kv => kv.Key).ToList()) ignoreUntil.Remove(k);
        // Contacts act at once (two pieces touching isn't a slow trend, and the bubble may be
        // passing straight through). Border events need two consecutive sightings.
        var events = all.Where(e => e.Kind == "contact" ||
                                    (seenLastCheck.Contains(Key(e)) && !ignoreUntil.ContainsKey(Key(e)))).ToList();
        seenLastCheck = keys;
        if (events.Count == 0) return;
        triggeredLast = new HashSet<string>(events.Where(e => e.Kind != "contact").Select(Key));
        firstCheckAfterRebuild = true;
        // Contacts first in the message.
        events = events.OrderBy(e => e.Kind == "contact" ? 0 : 1).ToList();
        string what = string.Join("; ", events.Take(4).Select(e => e.ToString())) + (events.Count > 4 ? " (+" + (events.Count - 4) + ")" : "");
        var job = Rearranger.Prepare(this, what, events);
        foreach (var e in events) if (e.Grow != null) job.Grow.AddRange(e.Grow);
        foreach (var e in events)
            if (e.Shift.LengthSq > 0 && e.Regions != null && e.Regions.Length > 0) job.Shift.Add(new KeyValuePair<int, Vec3>(e.Regions[0], e.Shift));
        if (S.AsyncRearrange)
        {
            pendingJob = job;
            if (S.SimulatedDelay > 0) { job.Run(); pendingTask = System.Threading.Tasks.Task.FromResult(0); pendingReadyStep = Steps + S.SimulatedDelay; }
            else pendingTask = System.Threading.Tasks.Task.Run(() => job.Run());
        }
        else
        {
            job.Run();
            Finish(job);
        }
    }

    // Apply a finished background rebuild (called at the start of each step).
    void PollRebuild()
    {
        if (pendingJob == null || !pendingTask.IsCompleted || Steps < pendingReadyStep) return;
        var job = pendingJob;
        pendingJob = null; pendingTask = null;
        Finish(job);
    }

    void Finish(Rearranger.RebuildJob job)
    {
        lastRearrangeStep = Steps;
        string when = S.AsyncRearrange ? " (background, steps " + job.Step + "-" + Steps + ")" : "";
        if (job.Fail != null)
        {
            LastFailedJob = job;
            string retry = "";
            if (job.AddRadius > 0)
            {
                // A refused add tries another node (twice) instead of being dropped.
                if (addRetries < 2 && currentAdd != null)
                {
                    addRetries++;
                    addQueue.Insert(0, currentAdd);   // same request; a point request is re-jittered by PrepareAdd
                    retry = currentAdd.HasAt ? "; trying again" : "; trying another node";
                }
                else { addRetries = 0; retry = "; add dropped after 3 tries"; }
            }
            else failedRebuilds++;
            RearrangeLog.Add("step " + job.Step + ": " + job.What + " -> rebuild refused: " + job.Fail + when + retry);
            return;
        }
        if (job.AddRadius > 0) addRetries = 0;
        int bordersBefore = BorderEdges.Count, nodesBefore = Nodes.Count;
        if (job.AddRadius > 0)
        {
            // Grow the per-region arrays for the new bubble; its target starts at the seeded
            // volume and ramps to the requested size.
            int k = job.Mesh.BubbleCount;
            double seeded = job.Mesh.Volumes()[k];
            TargetVolume = Grow(TargetVolume, k, seeded);
            DesiredVolume = Grow(DesiredVolume, k, 4.0 / 3.0 * Math.PI * job.AddRadius * job.AddRadius * job.AddRadius);
            Pressure = Grow(Pressure, k, 0);
            RegionScale = job.Scale ?? Grow(RegionScale, k, 1);
            var pt = new Vec3[k + 1]; Array.Copy(PullTarget, pt, PullTarget.Length); PullTarget = pt;
            var po = new bool[k + 1]; Array.Copy(PullOn, po, PullOn.Length); PullOn = po;
            Added++;
        }
        // A bubble the rebuild left a little off keeps its target: the volume correction puts the gap
        // back within a step or two, without a visible shrink-and-regrow. "A little" = its radius off by
        // under half its edge length (the Newton step then moves films less than half an edge).
        // A bigger gap starts from the rebuilt volume and the ramp brings it back at VolumeRate,
        // instead of one large Newton correction tearing the mesh.
        var rebuilt = job.Mesh.Volumes();
        var reinflate = new List<string>();
        double kept = 1;
        for (int k = 1; k < TargetVolume.Length && k < rebuilt.Length; k++)
        {
            // Fidelity of the rebuild itself: against the volumes when it was prepared (live resizes
            // in the meantime are the ramp's job, not a loss).
            if (k < job.CurrentVolume.Length && job.CurrentVolume[k] > 0) kept = Math.Min(kept, rebuilt[k] / job.CurrentVolume[k]);
            if (!(TargetVolume[k] > 0) || (job.AddRadius > 0 && k == rebuilt.Length - 1)) { TargetVolume[k] = rebuilt[k]; continue; }
            double dr = Math.Abs(Math.Pow(3 * Math.Max(rebuilt[k], 0) / (4 * Math.PI), 1.0 / 3) - Math.Pow(3 * TargetVolume[k] / (4 * Math.PI), 1.0 / 3));
            double edge = TargetEdge * (RegionScale != null && k < RegionScale.Length ? RegionScale[k] : 1);
            if (dr < 0.5 * edge) continue;
            reinflate.Add(k + " at " + (rebuilt[k] / TargetVolume[k]).ToString("P0", System.Globalization.CultureInfo.InvariantCulture));
            TargetVolume[k] = rebuilt[k];
        }
        ReplaceMesh(job.Mesh);
        failedRebuilds = 0;
        Rearrangements++;
        RearrangeLog.Add("step " + job.Step + ": " + job.What + " -> rebuilt in " + job.Ms + " ms" + when + "; nodes " +
                         nodesBefore + "->" + Nodes.Count + ", border edges " + bordersBefore + "->" + BorderEdges.Count +
                         "; smallest volume kept vs snapshot " + kept.ToString("P0", System.Globalization.CultureInfo.InvariantCulture) +
                         (reinflate.Count > 0 ? "; re-inflating bubble " + string.Join(", ", reinflate) : "") +
                         (job.Transfer != null ? "; " + job.Transfer : "") +
                         (job.Calibration != null && job.Calibration.Contains("rescued") ? "; " + job.Calibration : "") + "; " + job.Timing);
    }

    public int Added;
    public Rearranger.RebuildJob LastFailedJob;   // the most recent refused rebuild (diagnostics: harness failrepro)

    static double[] Grow(double[] a, int k, double value)
    {
        var b = new double[k + 1];
        Array.Copy(a, b, Math.Min(a.Length, k + 1));
        b[k] = value;
        return b;
    }

    public void Step()
    {
        PollRebuild();
        if (pendingJob == null && pendingPops.Count > 0) ApplyPops();
        // Adds wait until every bubble is back to size: a bubble still inflating from a previous
        // add or rebuild is small, and the next rebuild could lose it ("bubble 18 would vanish").
        if (pendingJob == null && addQueue.Count > 0 && !Reinflating()) StartAdd();
        if (Steps > 0)
        {
            if (S.Rearrange && S.RearrangeEvery > 0 && Steps % S.RearrangeEvery == 0) CheckRearrange();
            if (S.RemeshEvery > 0 && Steps % S.RemeshEvery == 0 && NeedsRemesh()) Remesh();
            else if (S.MaintainEvery > 0 && Steps % S.MaintainEvery == 0) Maintain();
        }
        int nb = Mesh.BubbleCount;
        Load();
        Array.Copy(X, X0, X.Length);
        RampVolumes();
        Pass(true);
        double ten = S.Tension;
        if (ten != 1.0) for (int i = 0; i < GA.Length; i++) GA[i] *= ten;
        ApplyPull();

        // Pressures: choose lambda so the motion keeps every volume fixed (to first order).
        var G = new double[nb, nb];
        var rhs = new double[nb];
        for (int v = 0; v < nv; v++)
        {
            double mob = Mob(v);
            double gx = GA[3 * v], gy = GA[3 * v + 1], gz = GA[3 * v + 2];
            for (int s = slotStart[v]; s < slotStart[v + 1]; s++)
            {
                int k = slotRegion[s] - 1;
                double sx = GV[3 * s], sy = GV[3 * s + 1], sz = GV[3 * s + 2];
                rhs[k] += mob * (sx * gx + sy * gy + sz * gz);
                for (int s2 = slotStart[v]; s2 < slotStart[v + 1]; s2++)
                    G[k, slotRegion[s2] - 1] += mob * (sx * GV[3 * s2] + sy * GV[3 * s2 + 1] + sz * GV[3 * s2 + 2]);
            }
        }
        double[] lambda = LinAlg.Solve(G, rhs);
        for (int k = 0; k < nb; k++) Pressure[k + 1] = lambda[k];

        double dt = S.Dt * EdgeLength * EdgeLength;
        double capBase = S.MaxMove * EdgeLength;
        double mom = S.Momentum;
        double res = 0, aSum = 0;
        for (int v = 0; v < nv; v++)
        {
            double fx = -GA[3 * v], fy = -GA[3 * v + 1], fz = -GA[3 * v + 2];
            for (int s = slotStart[v]; s < slotStart[v + 1]; s++)
            {
                double l = lambda[slotRegion[s] - 1];
                fx += GV[3 * s] * l; fy += GV[3 * s + 1] * l; fz += GV[3 * s + 2] * l;
            }
            double mob = Mob(v);
            fx *= mob; fy *= mob; fz *= mob;
            res += (fx * fx + fy * fy + fz * fz) * VA[v]; aSum += VA[v];
            double dx = fx * dt + prev[3 * v] * mom, dy = fy * dt + prev[3 * v + 1] * mom, dz = fz * dt + prev[3 * v + 2] * mom;
            double d2 = dx * dx + dy * dy + dz * dz, cap = capBase * vScale[v];
            if (d2 > cap * cap) { double sc = cap / Math.Sqrt(d2); dx *= sc; dy *= sc; dz *= sc; }
            X[3 * v] += dx; X[3 * v + 1] += dy; X[3 * v + 2] += dz;
        }
        Residual = Math.Sqrt(res / Math.Max(aSum, 1e-300));

        if (S.Smooth > 0) SmoothTangential(S.Smooth);
        for (int it = 0; it < S.VolumeIters; it++) CorrectVolumesInternal();
        SnapWet();

        // Momentum remembers the whole move (physics + smoothing + volume correction). Carrying
        // only the physics move was tried 2026-10-14 and made the random-12 benchmark worse.
        for (int i = 0; i < X.Length; i++) prev[i] = X[i] - X0[i];

        Area = TotalAreaAndVolumes();
        MaxVolumeError = 0;
        for (int k = 1; k <= nb; k++)
            if (TargetVolume[k] > 0)
                MaxVolumeError = Math.Max(MaxVolumeError, Math.Abs(vol[k] - TargetVolume[k]) / TargetVolume[k]);
        Store();
        Steps++;
    }

    void RampVolumes()
    {
        for (int k = 1; k < TargetVolume.Length; k++)
        {
            double d = DesiredVolume[k] - TargetVolume[k];
            if (d == 0) continue;
            double max = S.VolumeRate * Math.Max(TargetVolume[k], 0.1 * DesiredVolume[k]);
            TargetVolume[k] += Math.Max(-max, Math.Min(max, d));
        }
    }

    // External pull: each vertex of a pulled bubble's films gets VA * Pull * (target - centroid) / R^2,
    // subtracted from the area gradient. That way the volume constraints (pressures) see it, and the
    // pull moves the bubble without changing any volume. Total pull on a bubble is ~4 pi Pull d
    // for offset d, independent of its size.
    // Gather works the same way for every live bubble, toward S.GatherTo, with the offset capped at
    // the bubble's radius: far bubbles drift in at a steady rate instead of being yanked, and a
    // cluster is only pressed together gently. A bubble also being dragged gets both.
    void ApplyPull()
    {
        int nb = Mesh.BubbleCount;
        bool any = S.Gather > 0;
        if (S.Pull > 0) for (int k = 1; k <= nb; k++) if (PullOn[k]) { any = true; break; }
        if (!any) return;
        var forced = new bool[nb + 1];
        var cen = new double[3 * (nb + 1)];
        var vk = new double[nb + 1];
        double[] x = X;
        for (int t = 0; t < nt; t++)
        {
            int ia = 3 * tv[3 * t], ib = 3 * tv[3 * t + 1], ic = 3 * tv[3 * t + 2];
            double ax = x[ia], ay = x[ia + 1], az = x[ia + 2], bx = x[ib], by = x[ib + 1], bz = x[ib + 2], cx = x[ic], cy = x[ic + 1], cz = x[ic + 2];
            double tvol = (ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx)) / 6;
            double gx = (ax + bx + cx) / 4, gy = (ay + by + cy) / 4, gz = (az + bz + cz) / 4;
            int ng = tNeg[t], ps = tPos[t];
            if (ng > 0) { vk[ng] += tvol; cen[3 * ng] += gx * tvol; cen[3 * ng + 1] += gy * tvol; cen[3 * ng + 2] += gz * tvol; }
            if (ps > 0) { vk[ps] -= tvol; cen[3 * ps] -= gx * tvol; cen[3 * ps + 1] -= gy * tvol; cen[3 * ps + 2] -= gz * tvol; }
        }
        var fk = new double[3 * (nb + 1)];
        for (int k = 1; k <= nb; k++)
        {
            if (!(vk[k] > 0)) continue;
            double r2 = Math.Pow(3 * vk[k] / (4 * Math.PI), 2.0 / 3);
            double cx = cen[3 * k] / vk[k], cy = cen[3 * k + 1] / vk[k], cz = cen[3 * k + 2] / vk[k];
            if (S.Pull > 0 && PullOn[k])
            {
                double s = S.Pull / r2;
                fk[3 * k] = s * (PullTarget[k].X - cx);
                fk[3 * k + 1] = s * (PullTarget[k].Y - cy);
                fk[3 * k + 2] = s * (PullTarget[k].Z - cz);
                forced[k] = true;
            }
            if (S.Gather > 0)
            {
                double gx = S.GatherTo.X - cx, gy = S.GatherTo.Y - cy, gz = S.GatherTo.Z - cz;
                double d = Math.Sqrt(gx * gx + gy * gy + gz * gz), r = Math.Sqrt(r2);
                if (d > 1e-12)
                {
                    double s = S.Gather / r2 * Math.Min(d, r) / d;
                    fk[3 * k] += s * gx; fk[3 * k + 1] += s * gy; fk[3 * k + 2] += s * gz;
                    forced[k] = true;
                }
            }
        }
        for (int v = 0; v < nv; v++)
            for (int sl = slotStart[v]; sl < slotStart[v + 1]; sl++)
            {
                int k = slotRegion[sl];
                if (!forced[k]) continue;
                GA[3 * v] -= VA[v] * fk[3 * k]; GA[3 * v + 1] -= VA[v] * fk[3 * k + 1]; GA[3 * v + 2] -= VA[v] * fk[3 * k + 2];
            }
    }

    // Newton step back onto the volume constraints along the volume gradients. Uses the
    // vertex areas from the last full pass for the mobility.
    void CorrectVolumesInternal()
    {
        int nb = Mesh.BubbleCount;
        Pass(false);
        var G = new double[nb, nb];
        var rhs = new double[nb];
        for (int k = 1; k <= nb; k++) rhs[k - 1] = TargetVolume[k] - vol[k];
        for (int v = 0; v < nv; v++)
        {
            double mob = Mob(v);
            for (int s = slotStart[v]; s < slotStart[v + 1]; s++)
            {
                double sx = GV[3 * s], sy = GV[3 * s + 1], sz = GV[3 * s + 2];
                for (int s2 = slotStart[v]; s2 < slotStart[v + 1]; s2++)
                    G[slotRegion[s] - 1, slotRegion[s2] - 1] += mob * (sx * GV[3 * s2] + sy * GV[3 * s2 + 1] + sz * GV[3 * s2 + 2]);
            }
        }
        double[] mu = LinAlg.Solve(G, rhs);
        for (int v = 0; v < nv; v++)
        {
            double mob = Mob(v), dx = 0, dy = 0, dz = 0;
            for (int s = slotStart[v]; s < slotStart[v + 1]; s++)
            {
                double u = mu[slotRegion[s] - 1];
                dx += GV[3 * s] * u; dy += GV[3 * s + 1] * u; dz += GV[3 * s + 2] * u;
            }
            X[3 * v] += dx * mob; X[3 * v + 1] += dy * mob; X[3 * v + 2] += dz * mob;
        }
    }

    // Public entry (harness / tools): correct volumes on the current mesh positions.
    public void CorrectVolumes()
    {
        Load();
        Pass(true);
        CorrectVolumesInternal();
        Store();
    }

    double TotalAreaAndVolumes()
    {
        Array.Clear(vol, 0, vol.Length);
        double area = 0;
        double[] x = X;
        for (int t = 0; t < nt; t++)
        {
            int ia = 3 * tv[3 * t], ib = 3 * tv[3 * t + 1], ic = 3 * tv[3 * t + 2];
            double ax = x[ia], ay = x[ia + 1], az = x[ia + 2];
            double bx = x[ib], by = x[ib + 1], bz = x[ib + 2];
            double cx = x[ic], cy = x[ic + 1], cz = x[ic + 2];
            double ux = bx - ax, uy = by - ay, uz = bz - az, wx = cx - ax, wy = cy - ay, wz = cz - az;
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            if (!wetT[t]) area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
            double tvol = (ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx)) / 6;
            if (tNeg[t] > 0) vol[tNeg[t]] += tvol;
            if (tPos[t] > 0) vol[tPos[t]] -= tvol;
        }
        return area;
    }

    // Move vertices within the surface (films) or along the border (lines) toward their
    // neighbours' average. Keeps triangles well shaped without changing the geometry.
    void SmoothTangential(double w)
    {
        double[] x = X;
        Array.Clear(nrm, 0, nrm.Length);
        for (int t = 0; t < nt; t++)
        {
            int ia = 3 * tv[3 * t], ib = 3 * tv[3 * t + 1], ic = 3 * tv[3 * t + 2];
            double ux = x[ib] - x[ia], uy = x[ib + 1] - x[ia + 1], uz = x[ib + 2] - x[ia + 2];
            double wx = x[ic] - x[ia], wy = x[ic + 1] - x[ia + 1], wz = x[ic + 2] - x[ia + 2];
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            nrm[ia] += nx; nrm[ia + 1] += ny; nrm[ia + 2] += nz;
            nrm[ib] += nx; nrm[ib + 1] += ny; nrm[ib + 2] += nz;
            nrm[ic] += nx; nrm[ic + 1] += ny; nrm[ic + 2] += nz;
        }
        Array.Copy(x, nxt, x.Length);
        for (int v = 0; v < nv; v++)
        {
            int s0 = nbStart[v], s1 = nbStart[v + 1], cnt = s1 - s0;
            if (cls[v] == 2 || cnt == 0) continue;
            double ax = 0, ay = 0, az = 0;
            for (int s = s0; s < s1; s++) { int u = 3 * nbList[s]; ax += x[u]; ay += x[u + 1]; az += x[u + 2]; }
            int i = 3 * v;
            double lx = ax / cnt - x[i], ly = ay / cnt - x[i + 1], lz = az / cnt - x[i + 2];
            double dx, dy, dz;
            if (cls[v] == 0) { dx = nrm[i]; dy = nrm[i + 1]; dz = nrm[i + 2]; }
            else
            {
                int p = 3 * nbList[s0], q = 3 * nbList[s0 + 1];
                dx = x[q] - x[p]; dy = x[q + 1] - x[p + 1]; dz = x[q + 2] - x[p + 2];
            }
            double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dl > 1e-300) { dx /= dl; dy /= dl; dz /= dl; }
            double dot = lx * dx + ly * dy + lz * dz;
            if (cls[v] == 0) { lx -= dx * dot; ly -= dy * dot; lz -= dz * dot; }   // remove normal part
            else { lx = dx * dot; ly = dy * dot; lz = dz * dot; }                  // keep tangent part
            nxt[i] = x[i] + lx * w; nxt[i + 1] = x[i + 1] + ly * w; nxt[i + 2] = x[i + 2] + lz * w;
        }
        Array.Copy(nxt, X, X.Length);
    }
}

public static class LinAlg
{
    // Dense Gaussian elimination with partial pivoting. Singular directions get 0.
    public static double[] Solve(double[,] A0, double[] b0)
    {
        int n = b0.Length;
        var A = (double[,])A0.Clone();
        var b = (double[])b0.Clone();
        var x = new double[n];
        double scale = 0;
        for (int i = 0; i < n; i++) scale = Math.Max(scale, Math.Abs(A[i, i]));
        double eps = 1e-14 * Math.Max(scale, 1e-300);
        var dead = new bool[n];
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[p, c])) p = r;
            if (Math.Abs(A[p, c]) < eps) { dead[c] = true; continue; }
            if (p != c)
            {
                for (int k = 0; k < n; k++) { double t = A[c, k]; A[c, k] = A[p, k]; A[p, k] = t; }
                double tb = b[c]; b[c] = b[p]; b[p] = tb;
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = A[r, c] / A[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) A[r, k] -= f * A[c, k];
                b[r] -= f * b[c];
            }
        }
        for (int c = n - 1; c >= 0; c--)
        {
            if (dead[c]) { x[c] = 0; continue; }
            double s = b[c];
            for (int k = c + 1; k < n; k++) s -= A[c, k] * x[k];
            x[c] = s / A[c, c];
        }
        return x;
    }
}

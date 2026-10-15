using System.Collections.Generic;
using UnityEngine;

/// <summary>Conservative manual target guard for the steel shell, independent of the tooth contact test.</summary>
internal sealed class BucketShellTerrainGuard
{
    private static readonly Unity.Profiling.ProfilerMarker GuardMarker = new Unity.Profiling.ProfilerMarker("Soil.ShellGuard");
    private readonly List<Vector3> localSamples = new List<Vector3>();
    private readonly HashSet<Vector3Int> sampleCells = new HashSet<Vector3Int>();
    private Vector3[] worldSamples;
    private float[] permittedDepth;
    private float[] currentDepths, nextDepths, nextFloors;
    private Vector3[] nextSamples;
    private float overlapSum, overlapMaximum;
    private BucketSoilCutter activeCutter;
    private float lastPressureTime = float.NegativeInfinity;
    private readonly List<Vector3> pressingContacts = new List<Vector3>();
    private bool prepared;
    private DeformableSoilTerrain.SurfaceSnapshot surfaceSnapshot;
    private Bounds samplingBounds;
    internal float MaximumDepth { get; private set; }
    internal double YieldedVolumeM3 { get; private set; }
    private Vector3 blockingPoint;
    private float blockingDepth, blockingAllowed, blockingFloorGap;
    internal string ContactDiagnostics => $"point={blockingPoint}, nextDepth={blockingDepth:F4}, " +
        $"allowed={blockingAllowed:F4}, floorGap={blockingFloorGap:F4}, " +
        $"contacts={pressingContacts.Count}, yielded={YieldedVolumeM3:F6}";

    internal Zx200JointPose Constrain(BucketSoilCutter cutter, Zx200DigController machine,
        Zx200JointPose actual, Zx200JointPose requested, out bool limited)
    {
        using var profile = GuardMarker.Auto();
        limited = false;
        YieldedVolumeM3 = 0;
        if (!prepared) Prepare(cutter);
        if (localSamples.Count == 0 || cutter.soil == null) return requested;
        activeCutter = cutter;
        MaximumDepth = 0;
        for (int i = 0; i < localSamples.Count; i++)
        {
            worldSamples[i] = cutter.transform.TransformPoint(localSamples[i]);
            if (i == 0) samplingBounds = new Bounds(worldSamples[i], Vector3.zero);
            else samplingBounds.Encapsulate(worldSamples[i]);
        }
        samplingBounds.Expand(2f);
        surfaceSnapshot = cutter.soil.CaptureSurfaceSnapshot(samplingBounds);
        for (int i = 0; i < localSamples.Count; i++)
        {
            float depth = Depth(cutter.soil, worldSamples[i]);
            currentDepths[i] = depth;
            MaximumDepth = Mathf.Max(MaximumDepth, depth);
            // Stop at the surface to reserve room for physical actuator lag. An already
            // embedded shell may retreat, but never gets a fresh allowance to burrow farther.
            permittedDepth[i] = Mathf.Max(0f, depth);
        }
        RefreshOverlapTotals();
        // Drive-to-actual error includes the spring deflection supporting the heavy arm.
        // Guard only the NEW requested drive increment; clamping to actual each frame
        // removes that support and ratchets the shell downward under gravity.
        var drive = new Zx200JointPose(Drive(machine.swingJoint, actual.swing), Drive(machine.boomJoint, actual.boom),
            Drive(machine.armJoint, actual.arm), Drive(machine.bucketJoint, actual.bucket), 0);
        YieldPressedContacts(cutter, machine, actual, drive, requested);
        if (IsPathSafe(cutter.soil, machine, actual, drive, requested)) return requested;
        // Preserve independently safe controls, especially boom withdrawal. A common
        // fraction for all axes would let one blocked curl erase the operator's escape input.
        Zx200JointPose accepted = drive;
        for (int axis = 0; axis < 4; axis++)
        {
            var candidate = WithAxis(accepted, requested, axis);
            if (IsPathSafe(cutter.soil, machine, actual, drive, candidate)) { accepted = candidate; continue; }
            float safe = 0, unsafeFraction = 1;
            for (int iteration = 0; iteration < 9; iteration++)
            {
                float fraction = (safe + unsafeFraction) * .5f;
                var partial = Interpolate(accepted, candidate, fraction);
                if (IsPathSafe(cutter.soil, machine, actual, drive, partial)) safe = fraction;
                else unsafeFraction = fraction;
            }
            accepted = Interpolate(accepted, candidate, safe);
        }
        limited = true;
        return accepted;
    }

    private void YieldPressedContacts(BucketSoilCutter cutter, Zx200DigController machine,
        Zx200JointPose actual, Zx200JointPose drive, Zx200JointPose requested)
    {
        // Pressure-driven yielding needs explicit manual operation. An idle, initialized
        // machine must not erode its surroundings, nor may repeated queries double-spend a step.
        if (!Application.isPlaying || machine.IsBusy || machine.ManualPoseRevision == 0 ||
            !cutter.StartupReady || Time.fixedTime <= lastPressureTime) return;
        lastPressureTime = Time.fixedTime;
        Matrix4x4 motion = Motion(machine, actual, WithDriveIncrement(actual, drive, requested));
        float budget = .18f * Time.fixedDeltaTime;
        pressingContacts.Clear();
        for (int i = 0; i < worldSamples.Length; i++)
        {
            Vector3 contact = worldSamples[i];
            Vector3 next = motion.MultiplyPoint3x4(contact);
            Vector3 movement = next - contact;
            float depth = Depth(cutter.soil, contact);
            if (depth < -.005f || movement.sqrMagnitude < 1e-8f ||
                next.y <= Floor(cutter.soil, next) + .02f ||
                Depth(cutter.soil, next) <= Mathf.Max(depth, 0) + .0001f) continue;
            pressingContacts.Add(contact);
        }
        // One shared update yields the full measured contact footprint. Sequential
        // small cuts starved neighboring contacts and repeatedly rebuilt the collider.
        YieldedVolumeM3 = cutter.DisplacePressedSoil(pressingContacts, budget);
        if (YieldedVolumeM3 > 0) surfaceSnapshot = cutter.soil.CaptureSurfaceSnapshot(samplingBounds);
        // Limits must use the newly yielded surface, not stale depths from before the pressure step.
        for (int i = 0; i < worldSamples.Length; i++)
        {
            currentDepths[i] = Depth(cutter.soil, worldSamples[i]);
            permittedDepth[i] = Mathf.Max(0, currentDepths[i]);
        }
        RefreshOverlapTotals();
    }

    private static Zx200JointPose WithAxis(Zx200JointPose pose, Zx200JointPose requested, int axis)
    {
        if (axis == 0) pose.boom = requested.boom;
        else if (axis == 1) pose.bucket = requested.bucket;
        else if (axis == 2) pose.arm = requested.arm;
        else pose.swing = requested.swing;
        return pose;
    }

    private bool IsPathSafe(DeformableSoilTerrain soil, Zx200DigController machine, Zx200JointPose actual,
        Zx200JointPose drive, Zx200JointPose requested)
    {
        float largest = Mathf.Max(Mathf.Abs(Mathf.DeltaAngle(drive.swing, requested.swing)),
            Mathf.Abs(Mathf.DeltaAngle(drive.boom, requested.boom)), Mathf.Abs(Mathf.DeltaAngle(drive.arm, requested.arm)),
            Mathf.Abs(Mathf.DeltaAngle(drive.bucket, requested.bucket)));
        int steps = Mathf.Max(1, Mathf.CeilToInt(largest / .5f));
        for (int i = 1; i <= steps; i++)
        {
            var pose = WithDriveIncrement(actual, drive, Interpolate(drive, requested, (float)i / steps));
            if (!IsSafe(soil, Motion(machine, actual, pose))) return false;
        }
        return true;
    }

    private static float Drive(ArticulationBody joint, float fallback) => joint != null ? joint.xDrive.target : fallback;

    private static Zx200JointPose WithDriveIncrement(Zx200JointPose actual, Zx200JointPose drive, Zx200JointPose requested) =>
        new Zx200JointPose(actual.swing + Mathf.DeltaAngle(drive.swing, requested.swing),
            actual.boom + Mathf.DeltaAngle(drive.boom, requested.boom), actual.arm + Mathf.DeltaAngle(drive.arm, requested.arm),
            actual.bucket + Mathf.DeltaAngle(drive.bucket, requested.bucket), 0);

    private void Prepare(BucketSoilCutter cutter)
    {
        prepared = true;
        var owner = cutter.GetComponent<ArticulationBody>();
        foreach (Collider collider in cutter.GetComponentsInChildren<Collider>())
        {
            if (!collider.enabled || collider.isTrigger ||
                owner != null && collider.attachedArticulationBody != owner) continue;
            if (collider is MeshCollider meshCollider && meshCollider.sharedMesh != null && meshCollider.sharedMesh.isReadable)
            {
                Mesh mesh = meshCollider.sharedMesh;
                Vector3[] vertices = mesh.vertices;
                foreach (Vector3 vertex in vertices) Add(cutter, collider.transform.TransformPoint(vertex));
                int[] triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                    Add(cutter, collider.transform.TransformPoint((vertices[triangles[i]] +
                        vertices[triangles[i + 1]] + vertices[triangles[i + 2]]) / 3));
            }
            else if (collider is BoxCollider box)
            {
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                    if (x != 0 || y != 0 || z != 0)
                        Add(cutter, box.transform.TransformPoint(box.center + Vector3.Scale(box.size * .5f, new Vector3(x, y, z))));
            }
            else
            {
                // Non-readable imported hulls still expose their real closest surface through PhysX.
                Bounds bounds = collider.bounds;
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                    if (x != 0 || y != 0 || z != 0)
                        Add(cutter, collider.ClosestPoint(bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z))));
            }
        }
        worldSamples = new Vector3[localSamples.Count];
        permittedDepth = new float[localSamples.Count];
        currentDepths = new float[localSamples.Count];
        nextDepths = new float[localSamples.Count];
        nextFloors = new float[localSamples.Count];
        nextSamples = new Vector3[localSamples.Count];
    }

    private void Add(BucketSoilCutter cutter, Vector3 world)
    {
        Vector3 local = cutter.transform.InverseTransformPoint(world);
        Vector3 relativeToEdge = local - cutter.transform.InverseTransformPoint(cutter.TipWorldPosition);
        // The leading steel is allowed to enter and remove soil; the shell behind it is not.
        if (new Vector2(relativeToEdge.y, relativeToEdge.z).sqrMagnitude < .12f * .12f) return;
        var cell = new Vector3Int(Mathf.RoundToInt(local.x / .1f), Mathf.RoundToInt(local.y / .1f), Mathf.RoundToInt(local.z / .1f));
        if (sampleCells.Add(cell)) localSamples.Add(local);
    }

    private bool IsSafe(DeformableSoilTerrain soil, Matrix4x4 motion)
    {
        // Opening an embedded bucket exchanges contact between different parts of
        // the shell. Requiring every individual point to retreat can lock a rotation
        // that decreases overall overlap. Accept that escape if the worst depth does
        // not increase and no steel point crosses the prepared floor.
        if (RelievesShellOverlap(soil, motion)) return true;
        Vector3 tip = activeCutter.TipWorldPosition;
        Vector3 nextTip = motion.MultiplyPoint3x4(tip);
        float toothDepth = Depth(soil, tip);
        // Give actual tooth-led cuts a small deformable contact band. Never grant
        // this to an airborne edge, side/back scraping, or motion below the soil floor.
        bool yielding = toothDepth >= -.02f && float.IsFinite(Depth(soil, nextTip)) &&
            nextTip.y > Floor(soil, nextTip) + .03f &&
            activeCutter.CanCutMovement(nextTip - tip, out _);
        for (int i = 0; i < worldSamples.Length; i++)
        {
            float allowed = permittedDepth[i];
            Vector3 point = worldSamples[i];
            Vector3 next = nextSamples[i];
            Vector3 displacement = next - point;
            float depth = currentDepths[i];
            float nextDepth = nextDepths[i];
            bool pressingContact = depth >= -.005f && nextDepth > depth + .0001f &&
                displacement.sqrMagnitude > 1e-8f &&
                next.y > NextFloor(soil, i) + .015f;
            // Absolute depth cap, not a new per-frame allowance: holding an input
            // cannot ratchet the shell indefinitely into uncut ground.
            if (yielding && pressingContact) allowed = Mathf.Max(allowed, .045f);
            // A manually pressed shell needs enough contact to start the shallow
            // displacement step even when its teeth are still above the surface.
            if (Application.isPlaying && activeCutter.StartupReady &&
                lastPressureTime == Time.fixedTime && pressingContact)
                allowed = Mathf.Max(allowed, .025f);
            if (nextDepth > allowed + .0001f)
            {
                blockingPoint = point;
                blockingDepth = nextDepth;
                blockingAllowed = allowed;
                blockingFloorGap = next.y - NextFloor(soil, i);
                return false;
            }
        }
        return true;
    }

    private bool RelievesShellOverlap(DeformableSoilTerrain soil, Matrix4x4 motion)
    {
        float afterSum = 0, afterMax = 0;
        bool floorSafe = true;
        for (int i = 0; i < worldSamples.Length; i++)
        {
            Vector3 point = worldSamples[i];
            Vector3 next = motion.MultiplyPoint3x4(point);
            nextSamples[i] = next;
            nextDepths[i] = Depth(soil, next);
            nextFloors[i] = float.NaN;
            if (float.IsFinite(nextDepths[i]) && next.y < point.y - .0001f &&
                next.y < NextFloor(soil, i) + .015f) floorSafe = false;
            float after = Mathf.Max(0, nextDepths[i]);
            afterSum += after;
            afterMax = Mathf.Max(afterMax, after);
        }
        return floorSafe && overlapSum > .001f && afterSum < overlapSum - .0001f && afterMax <= overlapMaximum + .0001f;
    }

    private void RefreshOverlapTotals()
    {
        overlapSum = overlapMaximum = 0;
        foreach (float depth in currentDepths)
        {
            float positive = Mathf.Max(0, depth);
            overlapSum += positive;
            overlapMaximum = Mathf.Max(overlapMaximum, positive);
        }
    }

    private float NextFloor(DeformableSoilTerrain soil, int index)
    {
        if (float.IsNaN(nextFloors[index])) nextFloors[index] = Floor(soil, nextSamples[index]);
        return nextFloors[index];
    }

    private float Depth(DeformableSoilTerrain soil, Vector3 point) => surfaceSnapshot != null
        ? surfaceSnapshot.Depth(point)
        : soil.HasSurfaceAt(point) ? soil.Terrain.SampleHeight(point) + soil.transform.position.y - point.y : float.NegativeInfinity;

    private float Floor(DeformableSoilTerrain soil, Vector3 point) => surfaceSnapshot != null
        ? surfaceSnapshot.FloorWorldY(point) : soil.GetFloorWorldY(point);

    private static Matrix4x4 Motion(Zx200DigController machine, Zx200JointPose actual, Zx200JointPose target)
    {
        Quaternion rotation = Quaternion.identity;
        Vector3 translation = Vector3.zero;
        Apply(machine.swingJoint, actual.swing, target.swing, ref rotation, ref translation);
        Apply(machine.boomJoint, actual.boom, target.boom, ref rotation, ref translation);
        Apply(machine.armJoint, actual.arm, target.arm, ref rotation, ref translation);
        Apply(machine.bucketJoint, actual.bucket, target.bucket, ref rotation, ref translation);
        return Matrix4x4.TRS(translation, rotation, Vector3.one);
    }

    private static void Apply(ArticulationBody joint, float actual, float target, ref Quaternion accumulated, ref Vector3 translation)
    {
        if (joint == null) return;
        Vector3 pivot = accumulated * joint.transform.TransformPoint(joint.anchorPosition) + translation;
        Vector3 axis = accumulated * joint.transform.TransformDirection(joint.anchorRotation * Vector3.right);
        Quaternion rotation = Quaternion.AngleAxis(Mathf.DeltaAngle(actual, target), axis);
        translation = rotation * translation + pivot - rotation * pivot;
        accumulated = rotation * accumulated;
    }

    private static Zx200JointPose Interpolate(Zx200JointPose from, Zx200JointPose to, float fraction) =>
        new Zx200JointPose(Mathf.LerpAngle(from.swing, to.swing, fraction), Mathf.LerpAngle(from.boom, to.boom, fraction),
            Mathf.LerpAngle(from.arm, to.arm, fraction), Mathf.LerpAngle(from.bucket, to.bucket, fraction), 0);
}

using System.Collections.Generic;
using UnityEngine;

/// <summary>Conservative manual target guard for the steel shell, independent of the tooth contact test.</summary>
internal sealed class BucketShellTerrainGuard
{
    private readonly List<Vector3> localSamples = new List<Vector3>();
    private readonly HashSet<Vector3Int> sampleCells = new HashSet<Vector3Int>();
    private Vector3[] worldSamples;
    private float[] permittedDepth;
    private bool prepared;
    internal float MaximumDepth { get; private set; }

    internal Zx200JointPose Constrain(BucketSoilCutter cutter, Zx200DigController machine,
        Zx200JointPose actual, Zx200JointPose requested, out bool limited)
    {
        limited = false;
        if (!prepared) Prepare(cutter);
        if (localSamples.Count == 0 || cutter.soil == null) return requested;
        MaximumDepth = 0;
        for (int i = 0; i < localSamples.Count; i++)
        {
            worldSamples[i] = cutter.transform.TransformPoint(localSamples[i]);
            MaximumDepth = Mathf.Max(MaximumDepth, Depth(cutter.soil, worldSamples[i]));
            // Stop at the surface to reserve room for physical actuator lag. An already
            // embedded shell may retreat, but never gets a fresh allowance to burrow farther.
            permittedDepth[i] = Mathf.Max(0f, Depth(cutter.soil, worldSamples[i]));
        }
        // Drive-to-actual error includes the spring deflection supporting the heavy arm.
        // Guard only the NEW requested drive increment; clamping to actual each frame
        // removes that support and ratchets the shell downward under gravity.
        var drive = new Zx200JointPose(Drive(machine.swingJoint, actual.swing), Drive(machine.boomJoint, actual.boom),
            Drive(machine.armJoint, actual.arm), Drive(machine.bucketJoint, actual.bucket), 0);
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

    private static Zx200JointPose WithAxis(Zx200JointPose pose, Zx200JointPose requested, int axis)
    {
        if (axis == 0) pose.boom = requested.boom;
        else if (axis == 1) pose.arm = requested.arm;
        else if (axis == 2) pose.bucket = requested.bucket;
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
        for (int i = 0; i < worldSamples.Length; i++)
            if (Depth(soil, motion.MultiplyPoint3x4(worldSamples[i])) > permittedDepth[i] + .0001f) return false;
        return true;
    }

    private static float Depth(DeformableSoilTerrain soil, Vector3 point) => soil.HasSurfaceAt(point)
        ? soil.Terrain.SampleHeight(point) + soil.transform.position.y - point.y : float.NegativeInfinity;

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

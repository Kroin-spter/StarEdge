using UnityEngine;

/// <summary>Bounded cutting force plus drive-target penetration guard for the local soil heightfield.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BucketSoilCutter))]
[DefaultExecutionOrder(-150)]
public sealed class BucketSoilResistance : MonoBehaviour
{
    private const float FloorBrakingLeadMeters = .045f;
    [Min(.01f)] public float maximumPenetration = .18f;
    [Min(.001f)] public float contactMargin = .03f;
    [Min(.1f)] public float cuttingSpeedDegrees = 6;
    [Min(.05f)] public float floorBrakingLookAheadSeconds = .22f;
    [Tooltip("Additional measured tooth closing speed converted to an earlier floor-braking trigger.")]
    [Min(0)] public float floorBrakingResponseSeconds = .1f;
    [Tooltip("Clamp clearance-closing speed so a teleport or noisy sample cannot trigger braking far from the floor.")]
    [Min(.1f)] public float maximumFloorBrakingSpeedMps = 1.5f;
    [Tooltip("Keep curling during floor braking, but limit bucket speed to this fraction of normal cutting speed.")]
    [Range(.1f, 1f)] public float floorBrakingCurlSpeedScale = .5f;
    [Tooltip("Legacy base cutting pressure, Pa; retained for scene compatibility.")]
    [Min(0)] public float specificResistancePa = 10000;
    [Min(0)] public float soilUnitWeightNPerM3 = 17000;
    [Min(0)] public float cohesionPa = 3500;
    [Min(0)] public float surchargePa = 0;
    [Range(0, 50)] public float internalFrictionDegrees = 30;
    [Min(0)] public float viscousResistance = 900;
    [Min(0)] public float maximumForceN = 12000;
    public float PenetrationM { get; private set; }
    public float ResistanceForceN { get; private set; }
    public bool IsBlocked { get; private set; }
    public string BlockReason { get; private set; } = "空中";
    public bool IsContacting { get; private set; }
    public Vector3 AppliedForce { get; private set; }
    /// <summary>Number of physics-step requests limited to prevent combined-joint crossing of the prepared soil floor.</summary>
    public int CombinedFloorClampCount { get; private set; }
    /// <summary>Number of physics steps that applied velocity-aware upward braking near the prepared soil floor.</summary>
    public int VelocityFloorBrakeCount { get; private set; }
    /// <summary>Measured cutting-edge velocity in world units per second from the latest physics step.</summary>
    public Vector3 TipVelocity { get; private set; }
    /// <summary>Largest sampled manual bucket-shell depth below the live Terrain surface, in metres.</summary>
    public float ShellPenetrationM => shellGuard != null ? shellGuard.MaximumDepth : 0;
    /// <summary>True while measured shell pressure is actually displacing soft soil above the floor.</summary>
    public bool IsYieldingSoil { get; private set; }
    /// <summary>On-demand description of the latest shell restriction for scene diagnostics.</summary>
    public string ShellContactDiagnostics => shellGuard != null ? shellGuard.ContactDiagnostics : string.Empty;
    private BucketSoilCutter cutter;
    private ArticulationBody body;
    private Vector3 previous;
    private bool hasPrevious;
    private bool floorMomentumBrakeActive;
    private bool allowManualSlopeRelief;
    private bool full, atFloor, tooDeep;
    private BucketShellTerrainGuard shellGuard;
    private readonly System.Collections.Generic.Dictionary<ArticulationBody, float> holds =
        new System.Collections.Generic.Dictionary<ArticulationBody, float>();

    private void Awake() => Prepare();

    private void FixedUpdate()
    {
        EvaluateContact();
        Vector3 tip = cutter.TipWorldPosition;
        Vector3 velocity = hasPrevious ? (tip - previous) / Time.fixedDeltaTime : Vector3.zero;
        TipVelocity = velocity;
        hasPrevious = true;
        previous = tip;
        AppliedForce = Vector3.zero;
        ResistanceForceN = 0;
        if (!IsContacting || velocity.sqrMagnitude < .000001f || velocity.magnitude > 5 || body == null) return;
        // Withdrawal is deliberately unopposed. This approximates cutting pressure, not hydraulic dynamics.
        if (velocity.y > .02f) return;
        float alignment = Vector3.Dot(velocity.normalized, transform.TransformDirection(cutter.cutDirectionLocal).normalized);
        float force = EstimateResistanceForce(PenetrationM, velocity.magnitude, alignment);
        AppliedForce = -velocity.normalized * force;
        ResistanceForceN = force;
        body.AddForceAtPosition(AppliedForce, tip, ForceMode.Force);
    }

    /// <summary>Estimate bounded earth pressure using depth-squared weight and depth-linear strength terms.</summary>
    /// <param name="depth">Actual penetration in metres; nonpositive depth produces no cutting force.</param>
    /// <param name="speed">Nonnegative blade speed in metres per second.</param>
    /// <param name="alignment">Signed motion alignment with the mouth; influences pressure continuously.</param>
    /// <returns>Newtons, limited by maximumForceN. This is an uncalibrated passive-pressure approximation, not a full Reece solver.</returns>
    public float EstimateResistanceForce(float depth, float speed, float alignment)
    {
        if (!float.IsFinite(depth) || !float.IsFinite(speed) || !float.IsFinite(alignment) || depth <= 0) return 0;
        Prepare();
        float d = Mathf.Min(depth, maximumPenetration);
        float phi = Mathf.Clamp(internalFrictionDegrees, 0, 50) * Mathf.Deg2Rad;
        float passive = Mathf.Pow(Mathf.Tan(Mathf.PI * .25f + phi * .5f), 2);
        float pressure = specificResistancePa + 2 * cohesionPa * Mathf.Sqrt(passive) + surchargePa * passive;
        float width = 2 * cutter.bladeHalfWidth * Mathf.Abs(transform.lossyScale.x);
        float angleFactor = Mathf.Lerp(.7f, 1, Mathf.Clamp01((alignment + 1) * .5f));
        float force = width * (pressure * d + .5f * soilUnitWeightNPerM3 * d * d * passive) * angleFactor;
        force += Mathf.Max(0, speed) * viscousResistance * Mathf.Clamp01(d / Mathf.Max(.01f, maximumPenetration));
        return Mathf.Clamp(force, 0, Mathf.Max(0, maximumForceN));
    }

    /// <summary>Move the floor-braking trigger earlier by the measured tooth stopping distance.</summary>
    /// <param name="nominalTriggerClearance">Base clearance at which floor braking begins, in metres.</param>
    /// <param name="clearanceClosingSpeedMps">Nonnegative measured speed toward the floor, in metres per second.</param>
    /// <param name="responseSeconds">Additional actuator response horizon, in seconds.</param>
    /// <returns>The speed-adjusted trigger clearance in metres.</returns>
    public static float CalculateVelocityBrakeTriggerClearance(
        float nominalTriggerClearance,
        float clearanceClosingSpeedMps,
        float responseSeconds)
    {
        if (!float.IsFinite(nominalTriggerClearance) ||
            !float.IsFinite(clearanceClosingSpeedMps) || !float.IsFinite(responseSeconds))
            return float.IsFinite(nominalTriggerClearance) ? Mathf.Max(0, nominalTriggerClearance) : 0;

        float responseHorizon = Mathf.Clamp(responseSeconds, 0, .5f);
        float measuredStoppingDistance = Mathf.Max(0, clearanceClosingSpeedMps) * responseHorizon;
        return Mathf.Max(0, nominalTriggerClearance) + measuredStoppingDistance;
    }

    /// <summary>Refresh soil contact, fullness, depth and floor feedback from the actual cutting edge.</summary>
    public void EvaluateContact()
    {
        Prepare();
        Vector3 tip = cutter.TipWorldPosition;
        IsContacting = TrySurface(tip, out float surface);
        PenetrationM = IsContacting ? Mathf.Max(0, surface - tip.y) : 0;
        IsContacting &= tip.y <= surface + contactMargin;
        IsBlocked = false;
        BlockReason = "空中";
        full = atFloor = tooDeep = false;
        if (!IsContacting) return;
        full = cutter.EffectiveCapacityM3 - cutter.VolumeM3 <= .00001;
        atFloor = tip.y <= cutter.soil.GetFloorWorldY(tip) + contactMargin;
        tooDeep = PenetrationM >= maximumPenetration;
        IsBlocked = atFloor || tooDeep;
        BlockReason = atFloor ? "土层底部：禁止深入" : tooDeep ? "切深超限：限制深入，可沿面切削或抬升" : full ? "满斗切削：多余土侧落" : "切削接触";
    }

    /// <summary>Limit each joint command using its predicted cutting-edge displacement; never write solver pose.</summary>
    /// <param name="machine">Controller whose four articulation joints own this bucket.</param>
    /// <param name="requested">Requested joint targets in degrees.</param>
    /// <param name="deltaSeconds">Positive physics timestep.</param>
    /// <returns>Targets retaining floor/direction/shell protection and withdrawal; ordinary manual advance uses controller smoothing, automatic cutting remains rate-limited.</returns>
    public Zx200JointPose ConstrainPose(Zx200DigController machine, Zx200JointPose requested, float deltaSeconds)
    {
        EvaluateContact();
        IsYieldingSoil = false;
        allowManualSlopeRelief = machine != null && cutter.ManualSoilControlActive;
        if (!isActiveAndEnabled || machine == null || deltaSeconds <= 0)
        { holds.Clear(); floorMomentumBrakeActive = false; return requested; }
        var actual = machine.ReadActualPose();
        if (!IsContacting)
        {
            holds.Clear();
            floorMomentumBrakeActive = false;
            return GuardManualShell(machine, actual, requested);
        }
        // Manual cuts can enter deep soft soil above the floor. Depth alone must not
        // switch the controller from smooth advance to immediate blocked-target delivery.
        if (allowManualSlopeRelief && !atFloor)
        {
            IsBlocked = false;
            BlockReason = full ? "满斗切削：多余土侧落" : "切削接触";
        }
        if (ApplyFloorMomentumBrake(machine, actual, ref requested, deltaSeconds))
            return GuardManualShell(machine, actual, requested);
        requested.swing = GuardJoint(machine.swingJoint, actual.swing, requested.swing, deltaSeconds);
        requested.boom = GuardJoint(machine.boomJoint, actual.boom, requested.boom, deltaSeconds);
        requested.arm = GuardJoint(machine.armJoint, actual.arm, requested.arm, deltaSeconds);
        requested.bucket = GuardJoint(machine.bucketJoint, actual.bucket, requested.bucket, deltaSeconds);
        requested = ConstrainCombinedFloorCrossing(machine, actual, requested);
        return GuardManualShell(machine, actual, requested);
    }

    private Zx200JointPose GuardManualShell(Zx200DigController machine, Zx200JointPose actual, Zx200JointPose requested)
    {
        // Keep the autonomous controller's validated trajectories unchanged in this manual-focused change.
        if (machine.IsBusy || !cutter.ManualSoilControlActive) return requested;
        if (shellGuard == null) shellGuard = new BucketShellTerrainGuard();
        var result = shellGuard.Constrain(cutter, machine, actual, requested, out bool limited);
        IsYieldingSoil = shellGuard.YieldedVolumeM3 > 0 && !atFloor && !floorMomentumBrakeActive;
        if (limited)
        {
            IsBlocked = true;
            BlockReason = IsYieldingSoil ? "斗壳压土：持续让位并侧落" : "斗体接触未切土：限制深入，允许退出";
        }
        return result;
    }

    private bool ApplyFloorMomentumBrake(
        Zx200DigController machine, Zx200JointPose actual, ref Zx200JointPose requested,
        float deltaSeconds)
    {
        if (machine.boomJoint == null || machine.bucketJoint == null)
        {
            floorMomentumBrakeActive = false;
            return false;
        }
        Vector3 tip = cutter.TipWorldPosition;
        if (!cutter.soil.HasSurfaceAt(tip))
        {
            floorMomentumBrakeActive = false;
            return false;
        }

        float currentClearance = tip.y - cutter.soil.GetFloorWorldY(tip);
        float predictionSeconds = Mathf.Max(.05f, floorBrakingLookAheadSeconds);
        Vector3 predictedTip = tip + TipVelocity * predictionSeconds;
        float predictedClearance = cutter.soil.HasSurfaceAt(predictedTip)
            ? predictedTip.y - cutter.soil.GetFloorWorldY(predictedTip)
            : currentClearance;
        float clearanceClosingSpeedMps = Mathf.Clamp(
            (currentClearance - predictedClearance) / predictionSeconds,
            0,
            Mathf.Max(.1f, maximumFloorBrakingSpeedMps));
        Vector3 axis = machine.boomJoint.transform.TransformDirection(
            machine.boomJoint.anchorRotation * Vector3.right).normalized;
        Vector3 pivot = machine.boomJoint.transform.TransformPoint(machine.boomJoint.anchorPosition);
        float heightDerivative = Vector3.Cross(axis, tip - pivot).y;
        float raiseSign = Mathf.Abs(heightDerivative) > .0001f ? Mathf.Sign(heightDerivative) : 0f;
        // Leave enough lead for measured articulation lag before the protected floor;
        // the velocity term below advances this threshold further when the tooth is moving fast.
        float nominalTriggerClearance = contactMargin + FloorBrakingLeadMeters;
        float triggerClearance = CalculateVelocityBrakeTriggerClearance(
            nominalTriggerClearance, clearanceClosingSpeedMps, floorBrakingResponseSeconds);
        float releaseClearance = triggerClearance + .06f;
        bool closingOnFloor = clearanceClosingSpeedMps > .02f;

        if (!floorMomentumBrakeActive && closingOnFloor && predictedClearance <= triggerClearance)
            floorMomentumBrakeActive = true;
        else if (floorMomentumBrakeActive &&
            (!closingOnFloor && predictedClearance >= currentClearance - .005f ||
             currentClearance >= releaseClearance && predictedClearance >= currentClearance - .01f))
            floorMomentumBrakeActive = false;

        if (!floorMomentumBrakeActive) return false;

        // A direct withdrawal request is already the safest way to arrest downward motion.
        // Do not replace the larger recovery movement with the brake's smaller incremental target.
        float requestedBoomRaise = Mathf.DeltaAngle(actual.boom, requested.boom) * raiseSign;
        if (raiseSign != 0f && requestedBoomRaise > .15f)
        {
            floorMomentumBrakeActive = false;
            return false;
        }

        // Replace a stale downward target with a short boom withdrawal, but continue a
        // kinematically safe bucket curl at the same time. Freezing the bucket here made
        // the operator-visible sequence look like "lift first, close afterward".
        Zx200JointPose recovery = actual;
        recovery.duration = 0f;
        if (raiseSign != 0f)
        {
            // Keep the proven 3.5 cm actuator/terrain margin for the recovery move;
            // tooth velocity advances the trigger rather than amplifying the boom step.
            float requiredRaiseMeters = Mathf.Max(
                .025f, nominalTriggerClearance - predictedClearance + .035f);
            float raiseDegrees = Mathf.Clamp(
                requiredRaiseMeters / Mathf.Abs(heightDerivative) * Mathf.Rad2Deg,
                .5f,
                3.5f);
            ArticulationDrive boomDrive = machine.boomJoint.xDrive;
            recovery.boom = Zx200DigController.ClampTarget(
                actual.boom + raiseSign * raiseDegrees,
                boomDrive.lowerLimit,
                boomDrive.upperLimit);
        }
        float guardedBucketTarget = GuardJoint(
            machine.bucketJoint, actual.bucket, requested.bucket, deltaSeconds);
        if (!machine.IsBusy)
        {
            float guardedArmTarget = GuardJoint(
                machine.armJoint, actual.arm, requested.arm, deltaSeconds);
            // Manual withdrawal may follow a falling local floor; the autonomous scoop
            // keeps its original coordinated boom/bucket brake trajectory.
            recovery.arm = Mathf.MoveTowardsAngle(actual.arm, guardedArmTarget,
                cuttingSpeedDegrees * deltaSeconds);
        }
        float curlStep = cuttingSpeedDegrees * Mathf.Clamp(floorBrakingCurlSpeedScale, .1f, 1f) * deltaSeconds;
        recovery.bucket = Zx200DigController.ClampTarget(
            Mathf.MoveTowardsAngle(actual.bucket, guardedBucketTarget, curlStep),
            machine.bucketJoint.xDrive.lowerLimit,
            machine.bucketJoint.xDrive.upperLimit);
        requested = ConstrainCombinedFloorCrossing(machine, actual, recovery);

        holds.Clear();
        IsBlocked = true;
        BlockReason = "底面预测净空不足：动臂制动抬升";
        VelocityFloorBrakeCount++;
        return true;
    }

    private Zx200JointPose ConstrainCombinedFloorCrossing(
        Zx200DigController machine, Zx200JointPose actual, Zx200JointPose requested)
    {
        Vector3 tip = cutter.TipWorldPosition;
        if (!cutter.soil.HasSurfaceAt(tip)) return requested;
        float currentClearance = tip.y - cutter.soil.GetFloorWorldY(tip);
        float minimumClearance = Mathf.Min(contactMargin, currentClearance);
        Vector3 predicted = PredictTipPosition(machine, actual, requested);
        if (!cutter.soil.HasSurfaceAt(predicted)) return requested;
        float predictedClearance = predicted.y - cutter.soil.GetFloorWorldY(predicted);
        if (predictedClearance >= minimumClearance - .0001f ||
            predictedClearance >= currentClearance - .0001f)
            return requested;

        // GuardJoint checks each joint against the same current tooth point. Small individually
        // safe boom/arm/bucket steps can still combine into a floor-crossing motion, so find the
        // largest common fraction of this physics-step target that remains above the local floor.
        float safeFraction = 0f;
        float unsafeFraction = 1f;
        for (int iteration = 0; iteration < 10; iteration++)
        {
            float fraction = (safeFraction + unsafeFraction) * .5f;
            Zx200JointPose candidate = InterpolatePose(actual, requested, fraction);
            Vector3 candidateTip = PredictTipPosition(machine, actual, candidate);
            float candidateClearance = cutter.soil.HasSurfaceAt(candidateTip)
                ? candidateTip.y - cutter.soil.GetFloorWorldY(candidateTip)
                : float.PositiveInfinity;
            if (candidateClearance >= minimumClearance - .0001f)
                safeFraction = fraction;
            else
                unsafeFraction = fraction;
        }

        IsBlocked = true;
        BlockReason = "土层底部：限制组合关节深入";
        CombinedFloorClampCount++;
        return InterpolatePose(actual, requested, safeFraction);
    }

    private Vector3 PredictTipPosition(
        Zx200DigController machine, Zx200JointPose actual, Zx200JointPose target)
    {
        Vector3 tip = cutter.TipWorldPosition;
        Quaternion accumulatedRotation = Quaternion.identity;
        Vector3 accumulatedTranslation = Vector3.zero;
        ApplyPredictedJoint(machine.swingJoint, actual.swing, target.swing,
            ref tip, ref accumulatedRotation, ref accumulatedTranslation);
        ApplyPredictedJoint(machine.boomJoint, actual.boom, target.boom,
            ref tip, ref accumulatedRotation, ref accumulatedTranslation);
        ApplyPredictedJoint(machine.armJoint, actual.arm, target.arm,
            ref tip, ref accumulatedRotation, ref accumulatedTranslation);
        ApplyPredictedJoint(machine.bucketJoint, actual.bucket, target.bucket,
            ref tip, ref accumulatedRotation, ref accumulatedTranslation);
        return tip;
    }

    private static void ApplyPredictedJoint(
        ArticulationBody joint,
        float actualDegrees,
        float targetDegrees,
        ref Vector3 tip,
        ref Quaternion accumulatedRotation,
        ref Vector3 accumulatedTranslation)
    {
        if (joint == null) return;
        float delta = Mathf.DeltaAngle(actualDegrees, targetDegrees);
        if (Mathf.Abs(delta) < .0001f) return;
        Vector3 pivot = accumulatedRotation * joint.transform.TransformPoint(joint.anchorPosition) +
            accumulatedTranslation;
        Vector3 axis = (accumulatedRotation *
            joint.transform.TransformDirection(joint.anchorRotation * Vector3.right)).normalized;
        Quaternion rotation = Quaternion.AngleAxis(delta, axis);
        tip = pivot + rotation * (tip - pivot);
        accumulatedTranslation = rotation * accumulatedTranslation + pivot - rotation * pivot;
        accumulatedRotation = rotation * accumulatedRotation;
    }

    private static Zx200JointPose InterpolatePose(
        Zx200JointPose from, Zx200JointPose to, float fraction)
    {
        return new Zx200JointPose(
            Mathf.LerpAngle(from.swing, to.swing, fraction),
            Mathf.LerpAngle(from.boom, to.boom, fraction),
            Mathf.LerpAngle(from.arm, to.arm, fraction),
            Mathf.LerpAngle(from.bucket, to.bucket, fraction),
            0f);
    }

    private float GuardJoint(ArticulationBody joint, float actual, float requested, float dt)
    {
        if (joint == null) return requested;
        Vector3 tip = cutter.TipWorldPosition;
        Vector3 axis = joint.transform.TransformDirection(joint.anchorRotation * Vector3.right);
        Vector3 pivot = joint.transform.TransformPoint(joint.anchorPosition);
        float actualDelta = Mathf.DeltaAngle(actual, requested);
        Vector3 nextFromActual = pivot + Quaternion.AngleAxis(
            Mathf.Clamp(actualDelta, -1, 1), axis) * (tip - pivot);
        // A held cutting target must never prevent the operator/controller from reversing
        // toward a measured pose that raises the tooth out of the cut.
        if (nextFromActual.y - tip.y > .0001f ||
            allowManualSlopeRelief && ImprovesFloorClearance(tip, nextFromActual))
        {
            holds.Remove(joint);
            return requested;
        }

        // While held, compare input against the latched target, not gravity-induced tracking error.
        // Otherwise a downward command can look like withdrawal after the joint sags past its target.
        float reference = holds.TryGetValue(joint, out float held) ? held :
            allowManualSlopeRelief ? joint.xDrive.target : actual;
        float delta = requested - reference;
        if (Mathf.Abs(delta) < .00001f) return reference;
        Vector3 next = pivot + Quaternion.AngleAxis(Mathf.Clamp(delta, -1, 1), axis) * (tip - pivot);
        Vector3 movement = next - tip;
        // A positive vertical step always allows escape, even at capacity or at the excavation floor.
        if (movement.y > .0001f || allowManualSlopeRelief && ImprovesFloorClearance(tip, next))
        { holds.Remove(joint); return requested; }
        // Contact limits are independent of another joint's direction rejection in this command.
        // On a mound, overhead height grows during a horizontal cut into its face. It is
        // not a downward plunge. The real arm cuts at about 32 degrees below horizontal;
        // automatic control allows mouth-leading oblique cuts up to about 60 degrees
        // at restricted speed. Manual entry relies on the floor and shell guards.
        if (atFloor) return Hold(joint, actual);
        float horizontalTravel = new Vector2(movement.x, movement.z).magnitude;
        if (tooDeep && !allowManualSlopeRelief)
        {
            float mouthAlignment = Vector3.Dot(movement.normalized,
                transform.TransformDirection(cutter.cutDirectionLocal).normalized);
            if (movement.y < -1.7f * horizontalTravel - .00001f || mouthAlignment < 0f)
                return Hold(joint, actual);
        }
        // A manual bucket must also open or roll out of soil without collecting it.
        // The measured shell guard handles displacement, and floor guards run separately.
        if (allowManualSlopeRelief && joint == body)
        { holds.Remove(joint); return requested; }
        if (!cutter.CanCutMovement(movement, out string reason))
        {
            IsBlocked = true;
            BlockReason = reason;
            return Hold(joint, actual);
        }
        holds.Remove(joint);
        // Avoid rate-limiting manual advance before the controller smooths it again.
        return allowManualSlopeRelief ? requested :
            Mathf.MoveTowards(actual, requested, cuttingSpeedDegrees * dt);
    }

    private bool ImprovesFloorClearance(Vector3 current, Vector3 next)
    {
        if (cutter.soil == null || !cutter.soil.HasSurfaceAt(next)) return false;
        // A sloped floor may fall faster than the tooth. Compare local clearance, not only Y.
        return next.y - cutter.soil.GetFloorWorldY(next) >
            current.y - cutter.soil.GetFloorWorldY(current) + .0001f;
    }

    private float Hold(ArticulationBody joint, float actual)
    {
        // Latch once: recapturing actual angles every frame would ratchet down under gravity.
        if (!holds.TryGetValue(joint, out float target))
        {
            // Preserve existing spring deflection supporting the arm's weight; replacing it with
            // actual angle removes gravity support and causes a fresh sag even with a latched hold.
            target = joint.xDrive.target;
            holds.Add(joint, target);
        }
        return target;
    }

    private bool TrySurface(Vector3 point, out float surface)
    {
        surface = 0;
        if (cutter.soil == null) return false;
        cutter.soil.Initialize(cutter.soil.GetComponent<Terrain>());
        if (!cutter.soil.HasSurfaceAt(point)) return false;
        var terrain = cutter.soil.Terrain;
        surface = terrain.SampleHeight(point) + terrain.transform.position.y;
        return true;
    }

    private void Prepare()
    {
        if (cutter == null) cutter = GetComponent<BucketSoilCutter>();
        if (body == null) body = GetComponent<ArticulationBody>();
    }

    private void OnDisable()
    {
        shellGuard = null;
        holds.Clear();
        hasPrevious = false;
        floorMomentumBrakeActive = false;
        TipVelocity = Vector3.zero;
        AppliedForce = Vector3.zero;
        ResistanceForceN = 0;
    }
}

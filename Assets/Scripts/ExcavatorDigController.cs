using System.Collections;
using UnityEngine;

/// <summary>
/// 按下空格键播放一轮简化挖掘动作。
/// </summary>
public class ExcavatorDigController : MonoBehaviour
{
    [Header("Joint References")]
    public Transform boomLink;
    public Transform stickLink;
    public Transform bucketLink;

    [Header("Cycle Angles (local degrees)")]
    [Tooltip("Axis used by the imported visual links for the digging plane.")]
    // 导入模型的铰接轴与各连杆的局部 Z 轴对齐。
    public Vector3 visualJointAxis = Vector3.forward;

    public float boomDigAngle = 12f;
    public float stickDigAngle = -24f;
    public float bucketDigAngle = -14f;
    public float boomLiftAngle = -6f;
    public float stickLiftAngle = 4f;
    public float bucketDumpAngle = 14f;

    [Min(0.05f)]
    public float phaseDuration = 1f;

    private Quaternion boomRest;
    private Quaternion stickRest;
    private Quaternion bucketRest;
    private bool isDigging;
    private Vector4 topicAxes;

    private void Awake()
    {
        // 保持导入实例使用已验证的铰接轴。
        if (visualJointAxis.sqrMagnitude < 0.0001f ||
            Vector3.Dot(visualJointAxis.normalized, Vector3.left) > 0.99f)
            visualJointAxis = Vector3.forward;
        ResolveLinks();
        CaptureRestRotations();
    }

    private void Update()
    {
        if (!isDigging && Input.GetKeyDown(KeyCode.Space))
        {
            StartCoroutine(DigCycle());
        }
        if (!isDigging && topicAxes != Vector4.zero)
        {
            Vector3 axis = visualJointAxis.normalized;
            float dt = Time.deltaTime;
            if (boomLink != null) boomLink.localRotation *= Quaternion.AngleAxis(topicAxes.y * 25f * dt, axis);
            if (stickLink != null) stickLink.localRotation *= Quaternion.AngleAxis(topicAxes.z * 30f * dt, axis);
            if (bucketLink != null) bucketLink.localRotation *= Quaternion.AngleAxis(topicAxes.w * 35f * dt, axis);
        }
    }

    /// <summary>保存归一化的 AZ action3 到 action6 输入，用于连续控制关节。</summary>
    public void ApplyTopicPose(float swing, float boom, float arm, float bucket)
    {
        topicAxes = new Vector4(swing, boom, arm, bucket);
    }

    /// <summary>
    /// 未在检视面板赋值时，查找导入的 URDF 连杆。
    /// </summary>
    public void ResolveLinks()
    {
        boomLink = boomLink != null ? boomLink : FindChildByName("boom_link");
        stickLink = stickLink != null ? stickLink : FindChildByName("stick_link");
        bucketLink = bucketLink != null ? bucketLink : FindChildByName("bucket_link");
    }

    private Transform FindChildByName(string targetName)
    {
        foreach (Transform child in GetComponentsInChildren<Transform>(true))
        {
            if (child.name == targetName)
                return child;
        }

        Debug.LogWarning($"ExcavatorDigController could not find {targetName}.", this);
        return null;
    }

    private void CaptureRestRotations()
    {
        boomRest = boomLink != null ? boomLink.localRotation : Quaternion.identity;
        stickRest = stickLink != null ? stickLink.localRotation : Quaternion.identity;
        bucketRest = bucketLink != null ? bucketLink.localRotation : Quaternion.identity;
    }

    private IEnumerator DigCycle()
    {
        isDigging = true;

        yield return AnimateTo(boomDigAngle, stickDigAngle, bucketDigAngle);
        yield return AnimateTo(boomLiftAngle, stickLiftAngle, bucketDigAngle);
        yield return AnimateTo(boomLiftAngle, stickLiftAngle, bucketDumpAngle);
        yield return AnimateTo(0f, 0f, 0f);

        isDigging = false;
    }

    private IEnumerator AnimateTo(float boomAngle, float stickAngle, float bucketAngle)
    {
        // 先应用关节轴旋转，再组合导入的初始姿态，
        // 使运动保持在 URDF 铰接平面内，避免网格扭转。
        Vector3 axis = visualJointAxis.sqrMagnitude > 0.0001f ? visualJointAxis.normalized : Vector3.forward;
        Quaternion boomTarget = boomRest * Quaternion.AngleAxis(boomAngle, axis);
        Quaternion stickTarget = stickRest * Quaternion.AngleAxis(stickAngle, axis);
        Quaternion bucketTarget = bucketRest * Quaternion.AngleAxis(bucketAngle, axis);
        Quaternion boomStart = boomLink != null ? boomLink.localRotation : Quaternion.identity;
        Quaternion stickStart = stickLink != null ? stickLink.localRotation : Quaternion.identity;
        Quaternion bucketStart = bucketLink != null ? bucketLink.localRotation : Quaternion.identity;

        float elapsed = 0f;
        while (elapsed < phaseDuration)
        {
            float progress = SmoothProgress(elapsed / phaseDuration);
            if (boomLink != null) boomLink.localRotation = Quaternion.Slerp(boomStart, boomTarget, progress);
            if (stickLink != null) stickLink.localRotation = Quaternion.Slerp(stickStart, stickTarget, progress);
            if (bucketLink != null) bucketLink.localRotation = Quaternion.Slerp(bucketStart, bucketTarget, progress);
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (boomLink != null) boomLink.localRotation = boomTarget;
        if (stickLink != null) stickLink.localRotation = stickTarget;
        if (bucketLink != null) bucketLink.localRotation = bucketTarget;
    }

    /// <summary>
    /// 将动画进度限制在包含端点的 0 到 1 范围内。
    /// </summary>
    public static float SmoothProgress(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }
}

using System;
using UnityEngine;

/// <summary>
/// 为 ZX200 提供游戏式驾驶和关节键盘控制。
/// </summary>
[DefaultExecutionOrder(-100)]
public class Zx200KeyboardController : MonoBehaviour
{
    [Serializable]
    private sealed class AzControlFrame
    {
        public float action1 = 127f;
        public float action2 = 127f;
        public float action3 = 127f;
        public float action4 = 127f;
        public float action5 = 127f;
        public float action6 = 127f;
    }

    [Header("References")]
    public Zx200DigController digController;
    public ArticulationBody articulationRoot;

    [Header("Driving")]
    [Tooltip("启用后固定底盘；关闭后使用履带受力移动。")]
    public bool fixedBase = true;
    [Min(0f)] public float moveSpeed = 2f;
    [Min(0f)] public float turnSpeed = 35f;
    public LayerMask groundLayers = ~0;

    [Header("Manual Joint Speeds (degrees/second)")]
    [Min(0f)] public float swingSpeed = 35f;
    [Min(0f)] public float boomSpeed = 25f;
    [Min(0f)] public float armSpeed = 30f;
    [Min(0f)] public float bucketSpeed = 45f;

    [Header("Topic Input")]
    [Min(0f)] public float neutralDeadzone = 1.5f;

    private Zx200TrackDrive trackDrive;
    private float lastTopicTime = float.NegativeInfinity;
    private const float DriveTopicTimeout = 0.5f;
    private AzControlFrame topicFrame;

    private void Awake()
    {
        if (digController == null) digController = GetComponent<Zx200DigController>();
        ResolveArticulationRoot();
        trackDrive = GetComponent<Zx200TrackDrive>();
        if (trackDrive == null) trackDrive = gameObject.AddComponent<Zx200TrackDrive>();
    }

    private void Update()
    {
        float deltaTime = Time.deltaTime;
        float vertical = Input.GetAxisRaw("Vertical");
        float horizontal = Input.GetAxisRaw("Horizontal");
        bool hasControlTopic = HasControlTopic();
        if (hasControlTopic && Time.time - lastTopicTime <= DriveTopicTimeout &&
            Mathf.Approximately(vertical, 0f) && Mathf.Approximately(horizontal, 0f))
        {
            // AZ action1/2 是左、右履带。差速驱动中，两侧速度相同表示直行，
            // 速度不同表示转弯，方向相反时原地旋转。
            // 当前手柄的履带前后方向与 Unity 模型相反，因此左右履带同时取反。
            CalculateTrackMotion(
                -NormalizeAction(topicFrame.action1, neutralDeadzone),
                -NormalizeAction(topicFrame.action2, neutralDeadzone),
                out vertical, out horizontal);
        }
        MoveArticulationRoot(
            vertical,
            horizontal,
            deltaTime);

        if (digController == null || digController.IsBusy) return;
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            digController.ApplyWorkReadyPose();
            return;
        }

        // AZ 映射：action3=回旋、action4=斗杆、action5=铲斗、action6=动臂。
        // 对应键按下时键盘优先；该轴没有键盘输入时才使用 topic。
        float topicSwing = hasControlTopic
            ? -NormalizeAction(topicFrame.action3, neutralDeadzone) : 0f;
        float topicArm = hasControlTopic
            ? NormalizeAction(topicFrame.action4, neutralDeadzone) : 0f;
        float topicBucket = hasControlTopic
            ? -NormalizeAction(topicFrame.action5, neutralDeadzone) : 0f;
        float topicBoom = hasControlTopic
            ? -NormalizeAction(topicFrame.action6, neutralDeadzone) : 0f;
        // 导入模型的回转正方向与设备向左回转的方向相反。
        float swing = ResolveAxisInput(
            Input.GetKey(KeyCode.E), Input.GetKey(KeyCode.Q), hasControlTopic, topicSwing) *
            swingSpeed * deltaTime;
        float boom = ResolveAxisInput(
            Input.GetKey(KeyCode.F), Input.GetKey(KeyCode.R), hasControlTopic, topicBoom) *
            boomSpeed * deltaTime;
        float arm = ResolveAxisInput(
            Input.GetKey(KeyCode.T), Input.GetKey(KeyCode.G), hasControlTopic, topicArm) *
            armSpeed * deltaTime;
        float bucket = ResolveAxisInput(
            Input.GetKey(KeyCode.Y), Input.GetKey(KeyCode.H), hasControlTopic, topicBucket) *
            bucketSpeed * deltaTime;
        if (!Mathf.Approximately(swing, 0f) || !Mathf.Approximately(boom, 0f) ||
            !Mathf.Approximately(arm, 0f) || !Mathf.Approximately(bucket, 0f))
        {
            digController.AdjustManualPose(swing, boom, arm, bucket);
        }
    }

    /// <summary>
    /// 接收 WebGL 宿主页面转发的 AZ 动作数据帧。
    /// </summary>
    /// <param name="json">包含 action1 到 action6 的 JSON 字符串，数值范围为 0 到 255。</param>
    public void ReceiveControlTopic(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var parsed = JsonUtility.FromJson<AzControlFrame>(json);
            if (parsed != null)
            {
                topicFrame = parsed;
                lastTopicTime = Time.time;
            }
        }
        catch (Exception error)
        {
            Debug.LogWarning($"Invalid AZ control topic: {error.Message}", this);
        }
    }

    /// <summary>将 AZ 无符号动作值转换为以 0 为中心的 -1 到 1 控制量。</summary>
    /// <param name="value">范围通常为 0 到 255 的 AZ 动作值。</param>
    /// <param name="deadzone">围绕 127.5 中位值的输入死区半径。</param>
    /// <returns>死区内为零、死区外限制在 -1 到 1 的控制量。</returns>
    public static float NormalizeAction(float value, float deadzone = 1.5f)
    {
        const float center = 127.5f;
        float offset = value - center;
        if (Mathf.Abs(offset) <= Mathf.Max(0f, deadzone)) return 0f;

        float range = offset < 0f ? center : 126.5f;
        return Mathf.Clamp(offset / range, -1f, 1f);
    }

    /// <summary>判断当前是否已经收到 WebGL topic 控制数据。</summary>
    /// <returns>至少成功解析过一帧控制数据时返回 true。</returns>
    public bool HasControlTopic()
    {
        return topicFrame != null;
    }

    /// <summary>将左右履带指令转换为前进和转向输入。</summary>
    /// <param name="leftTrack">左履带归一化输入，范围为 -1 到 1。</param>
    /// <param name="rightTrack">右履带归一化输入，范围为 -1 到 1。</param>
    /// <param name="forward">输出前后行驶输入。</param>
    /// <param name="steering">输出转向输入，正数表示右转。</param>
    public static void CalculateTrackMotion(
        float leftTrack,
        float rightTrack,
        out float forward,
        out float steering)
    {
        leftTrack = Mathf.Clamp(leftTrack, -1f, 1f);
        rightTrack = Mathf.Clamp(rightTrack, -1f, 1f);
        forward = (leftTrack + rightTrack) * 0.5f;
        // Unity 的正 yaw 会使车头朝本地 +X 方向转动，因此左履带更快时产生正转向，
        // 右履带更快时产生反方向转向。
        steering = (leftTrack - rightTrack) * 0.5f;
    }

    /// <summary>
    /// 解析单个关节输入轴，按住键盘按键时优先使用键盘输入。
    /// </summary>
    /// <param name="negativePressed">是否按住负方向按键。</param>
    /// <param name="positivePressed">是否按住正方向按键。</param>
    /// <param name="hasTopic">是否存在有效的主题输入帧。</param>
    /// <param name="topicInput">该轴归一化后的主题输入值。</param>
    /// <returns>范围为 -1 到 1 的归一化输入值。</returns>
    public static float ResolveAxisInput(
        bool negativePressed,
        bool positivePressed,
        bool hasTopic,
        float topicInput)
    {
        if (negativePressed || positivePressed)
        {
            return (positivePressed ? 1f : 0f) - (negativePressed ? 1f : 0f);
        }

        return hasTopic ? Mathf.Clamp(topicInput, -1f, 1f) : 0f;
    }

    /// <summary>
    /// 计算一帧输入对应的参考位移和偏航角；此兼容接口不直接移动设备。
    /// </summary>
    /// <param name="vertical">前进或后退输入，范围为 -1 到 1。</param>
    /// <param name="horizontal">左右转向输入，范围为 -1 到 1。</param>
    /// <param name="deltaTime">当前帧耗时，单位为秒。</param>
    /// <param name="translation">挖掘机局部坐标系中的位移。</param>
    /// <param name="yaw">世界坐标系中的 yaw 角，单位为度。</param>
    public void CalculateMotion(
        float vertical,
        float horizontal,
        float deltaTime,
        out Vector3 translation,
        out float yaw)
    {
        // ROS 的前方（+X）经 URDF 导入器转换为 Unity 局部 +Z 方向。
        translation = Vector3.forward * vertical * moveSpeed * deltaTime;
        yaw = horizontal * turnSpeed * deltaTime;
    }

    /// <summary>
    /// 查找导入的移动机构层级所对应的根关节刚体。
    /// </summary>
    /// <returns>找到根关节刚体时返回 true。</returns>
    public bool ResolveArticulationRoot()
    {
        if (articulationRoot != null) return true;
        foreach (ArticulationBody body in GetComponentsInChildren<ArticulationBody>(true))
        {
            if (!HasArticulationParent(body.transform))
            {
                articulationRoot = body;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 提交底盘行驶请求；实际移动由履带组件在物理步中施力完成。
    /// </summary>
    /// <param name="vertical">前进或后退输入，范围为 -1 到 1。</param>
    /// <param name="horizontal">左右转向输入，范围为 -1 到 1。</param>
    /// <param name="deltaTime">保留的兼容参数，物理施力使用固定时间步。</param>
    /// <returns>行驶请求成功提交时返回 true，不代表已经发生位移。</returns>
    public bool MoveArticulationRoot(float vertical, float horizontal, float deltaTime)
    {
        if (fixedBase) return false;
        if (!ResolveArticulationRoot()) return false;
        if (trackDrive == null) trackDrive = GetComponent<Zx200TrackDrive>();
        if (trackDrive == null) trackDrive = gameObject.AddComponent<Zx200TrackDrive>();
        trackDrive.SetInput(vertical, horizontal);
        return true;
    }

    private void OnDisable()
    {
        if (trackDrive != null) trackDrive.Stop();
    }

    private void OnApplicationFocus(bool focused)
    {
        if (focused) return;
        lastTopicTime = float.NegativeInfinity;
        if (trackDrive != null) trackDrive.Stop();
    }

    private static bool HasArticulationParent(Transform child)
    {
        Transform parent = child.parent;
        while (parent != null)
        {
            if (parent.GetComponent<ArticulationBody>() != null) return true;
            parent = parent.parent;
        }
        return false;
    }

}

using UnityEngine;

/// <summary>
/// 使用旧版 WASD 输入轴移动演示挖掘机的根对象。
/// </summary>
public class ExcavatorWASDController : MonoBehaviour
{
    [System.Serializable]
    private class AzControlFrame { public float action1=127, action2=127, action3=127, action4=127, action5=127, action6=127; }
    private AzControlFrame topicFrame;
    private ExcavatorDigController digController;
    [Tooltip("Disable URDF physics and colliders while using this simple visual controller.")]
    public bool disableUrdfPhysics = true;

    [Min(0f)]
    [Tooltip("Forward and backward speed in meters per second.")]
    public float moveSpeed = 3f;

    [Min(0f)]
    [Tooltip("Left and right turn speed in degrees per second.")]
    public float turnSpeed = 90f;

    [Tooltip("Layers considered ground when following terrain height.")]
    public LayerMask groundLayers = ~0;

    [Min(0f)]
    [Tooltip("Ray start height above the excavator root in meters.")]
    public float groundRayHeight = 5f;

    [Min(0f)]
    [Tooltip("Maximum distance searched below the ray start in meters.")]
    public float groundRayDistance = 50f;

    [Tooltip("Height to keep the excavator root above the detected ground.")]
    public float groundOffset = 0f;

    private void Update()
    {
        float vertical = Input.GetAxisRaw("Vertical");
        float horizontal = Input.GetAxisRaw("Horizontal");
        if (topicFrame != null)
        {
            vertical = Mathf.Clamp((topicFrame.action2 - topicFrame.action1) / 127f, -1f, 1f);
            horizontal = Mathf.Clamp((topicFrame.action3 - 127f) / 127f, -1f, 1f);
            digController?.ApplyTopicPose(
                Normalize(topicFrame.action3), Normalize(topicFrame.action4),
                Normalize(topicFrame.action5), Normalize(topicFrame.action6));
        }
        CalculateMotion(
            vertical, horizontal,
            Time.deltaTime,
            out Vector3 localTranslation,
            out float yawDegrees);

        transform.Translate(localTranslation, Space.Self);
        transform.Rotate(Vector3.up, yawDegrees, Space.World);
    }

    private void LateUpdate()
    {
        FollowGroundHeight();
    }

    private void Awake()
    {
        digController = GetComponent<ExcavatorDigController>();
        if (disableUrdfPhysics)
        {
            DisablePhysicsComponents();
        }
    }

    /// <summary>接收 WebGL 宿主转发的 AZ 动作值。</summary>
    public void ReceiveControlTopic(string json)
    {
        if (!string.IsNullOrWhiteSpace(json)) topicFrame = JsonUtility.FromJson<AzControlFrame>(json);
    }

    private static float Normalize(float value) => Mathf.Clamp((value - 127f) / 127f, -1f, 1f);

    /// <summary>
    /// 禁用子级关节刚体和碰撞体，使旧版演示由 Transform 驱动。
    /// </summary>
    public void DisablePhysicsComponents()
    {
        foreach (ArticulationBody articulationBody in GetComponentsInChildren<ArticulationBody>(true))
        {
            articulationBody.enabled = false;
        }

        foreach (Collider collider in GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = false;
        }
    }

    /// <summary>
    /// 仅调整根节点高度，使其跟随下方第一个地面碰撞体。
    /// </summary>
    public void FollowGroundHeight()
    {
        Vector3 rayOrigin = transform.position + Vector3.up * groundRayHeight;
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, groundRayDistance, groundLayers,
            QueryTriggerInteraction.Ignore))
        {
            Vector3 position = transform.position;
            position.y = hit.point.y + groundOffset;
            transform.position = position;
        }
    }

    /// <summary>
    /// 根据输入计算一帧局部位移和世界偏航角。
    /// </summary>
    /// <param name="verticalInput">归一化的前后输入。</param>
    /// <param name="horizontalInput">归一化的左右输入。</param>
    /// <param name="deltaTime">当前帧时长，单位为秒。</param>
    /// <param name="localTranslation">在对象局部坐标系中应用的位移。</param>
    /// <param name="yawDegrees">绕世界 Y 轴应用的偏航角，单位为度。</param>
    public void CalculateMotion(
        float verticalInput,
        float horizontalInput,
        float deltaTime,
        out Vector3 localTranslation,
        out float yawDegrees)
    {
        // 此 URDF 模型的前方为局部 -X 方向。
        localTranslation = Vector3.left * verticalInput * moveSpeed * deltaTime;
        yawDegrees = horizontalInput * turnSpeed * deltaTime;
    }
}

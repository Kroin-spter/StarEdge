using UnityEngine;

/// <summary>
/// 控制 ZX200 挖掘机的驾驶室视角和第三人称环绕视角。
/// </summary>
[DefaultExecutionOrder(100)]
public class Zx200CameraController : MonoBehaviour
{
    [Header("References")]
    public Camera targetCamera;
    public Transform cabinAnchor;
    public Transform orbitAnchor;

    [Header("View")]
    public KeyCode toggleKey = KeyCode.C;
    public bool startInThirdPerson = true;
    public float initialThirdPersonYaw;
    public Vector3 firstPersonLocalPosition = new Vector3(-0.35f, 1.75f, 0.45f);
    public Vector3 firstPersonForwardEuler = Vector3.zero;

    [Header("Look")]
    [Min(0f)] public float mouseSensitivity = 3f;
    public float minPitch = -25f;
    public float maxPitch = 70f;

    [Header("Third Person")]
    public Vector3 orbitPivotLocalPosition = new Vector3(0f, 1.4f, 0f);
    [Min(0.1f)] public float initialDistance = 8f;
    [Min(0.1f)] public float minDistance = 3f;
    [Min(0.1f)] public float maxDistance = 14f;
    [Min(0f)] public float zoomSpeed = 2f;
    [Min(0.01f)] public float followSmoothTime = 0.08f;

    /// <summary>获取当前是否为第三人称视角。</summary>
    public bool IsThirdPerson { get; private set; } = true;

    /// <summary>获取累计的水平观察角度，单位为度。</summary>
    public float OrbitYaw { get; private set; }

    /// <summary>获取限制后的垂直观察角度，单位为度。</summary>
    public float OrbitPitch { get; private set; } = 20f;

    /// <summary>获取当前第三人称相机距离。</summary>
    public float Distance { get; private set; } = 8f;

    private Vector3 followVelocity;

    private void Awake()
    {
        ResolveReferences();
        IsThirdPerson = startInThirdPerson;
        Distance = Mathf.Clamp(initialDistance, minDistance, maxDistance);
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey)) ToggleView();
        if (Input.GetMouseButton(1))
        {
            AddLookInput(
                Input.GetAxis("Mouse X") * mouseSensitivity,
                -Input.GetAxis("Mouse Y") * mouseSensitivity);
        }
        if (IsThirdPerson) AddZoomInput(Input.mouseScrollDelta.y);
    }

    private void LateUpdate()
    {
        if (targetCamera == null) return;
        if (IsThirdPerson) ApplyThirdPerson();
        else ApplyFirstPerson();
    }

    /// <summary>
    /// 在引用未赋值时查找主相机和 URDF 驾驶室锚点。
    /// </summary>
    public void ResolveReferences()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        foreach (Transform child in GetComponentsInChildren<Transform>(true))
        {
            if (cabinAnchor == null && child.name == "body_link") cabinAnchor = child;
            if (orbitAnchor == null && child.name == "track_link") orbitAnchor = child;
        }
    }

    /// <summary>在驾驶室视角和第三人称视角之间切换。</summary>
    public void ToggleView()
    {
        IsThirdPerson = !IsThirdPerson;
        followVelocity = Vector3.zero;
    }

    /// <summary>
    /// 增加水平和垂直相机观察输入。
    /// </summary>
    /// <param name="yawDelta">水平角度变化，单位为度。</param>
    /// <param name="pitchDelta">垂直角度变化，单位为度。</param>
    public void AddLookInput(float yawDelta, float pitchDelta)
    {
        OrbitYaw += yawDelta;
        OrbitPitch = Mathf.Clamp(OrbitPitch + pitchDelta, minPitch, maxPitch);
    }

    /// <summary>
    /// 应用鼠标滚轮缩放，同时遵守第三人称距离限制。
    /// </summary>
    /// <param name="scrollDelta">鼠标滚轮变化量，正值表示拉近。</param>
    public void AddZoomInput(float scrollDelta)
    {
        Distance = Mathf.Clamp(Distance - scrollDelta * zoomSpeed,
            minDistance, maxDistance);
    }

    private void ApplyFirstPerson()
    {
        Transform anchor = cabinAnchor != null ? cabinAnchor : transform;
        targetCamera.transform.position = anchor.TransformPoint(firstPersonLocalPosition);
        targetCamera.transform.rotation = anchor.rotation *
            Quaternion.Euler(firstPersonForwardEuler + new Vector3(OrbitPitch, OrbitYaw, 0f));
    }

    private void ApplyThirdPerson()
    {
        Transform anchor = orbitAnchor != null ? orbitAnchor : transform;
        Vector3 pivot = anchor.TransformPoint(orbitPivotLocalPosition);
        Quaternion orbit = anchor.rotation *
            Quaternion.Euler(OrbitPitch, initialThirdPersonYaw + OrbitYaw, 0f);
        Vector3 desired = pivot + orbit * Vector3.back * Distance;
        targetCamera.transform.position = Vector3.SmoothDamp(
            targetCamera.transform.position, desired, ref followVelocity, followSmoothTime);
        Vector3 direction = pivot - targetCamera.transform.position;
        if (direction.sqrMagnitude > 0.0001f)
            targetCamera.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
    }
}

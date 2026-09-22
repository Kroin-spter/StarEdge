using UnityEngine;

/// <summary>
/// 切换并跟随挖掘机的第一人称和第三人称视角。
/// </summary>
public class ExcavatorViewController : MonoBehaviour
{
    [Header("Camera")]
    public Camera targetCamera;
    public KeyCode toggleKey = KeyCode.C;
    public bool startInThirdPerson = true;

    [Header("First Person")]
    public Vector3 firstPersonLocalPosition = new Vector3(-0.25f, 2.05f, 0.05f);
    // 挖掘机前方为局部 -X，相机朝向为局部 +Z。
    public Vector3 firstPersonLocalEulerAngles = new Vector3(0f, -90f, 0f);

    [Header("Third Person")]
    public Vector3 thirdPersonLocalOffset = new Vector3(5.5f, 3.2f, -6.5f);
    public Vector3 thirdPersonLookAtLocalPosition = new Vector3(-0.8f, 1.5f, 0f);
    [Min(0.01f)]
    public float followSmoothTime = 0.12f;

    private bool thirdPerson;
    private Vector3 followVelocity;

    private void Awake()
    {
        if (targetCamera == null)
            targetCamera = Camera.main;

        // 迁移仍使用旧版零朝向的场景实例。
        if (firstPersonLocalEulerAngles.sqrMagnitude < 0.0001f)
            firstPersonLocalEulerAngles = new Vector3(0f, -90f, 0f);

        thirdPerson = startInThirdPerson;
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            thirdPerson = !thirdPerson;
    }

    private void LateUpdate()
    {
        if (targetCamera == null)
            return;

        if (thirdPerson)
            ApplyThirdPerson();
        else
            ApplyFirstPerson();
    }

    private void ApplyFirstPerson()
    {
        targetCamera.transform.position = transform.TransformPoint(firstPersonLocalPosition);
        targetCamera.transform.rotation = transform.rotation * Quaternion.Euler(firstPersonLocalEulerAngles);
    }

    private void ApplyThirdPerson()
    {
        Vector3 desiredPosition = transform.TransformPoint(thirdPersonLocalOffset);
        targetCamera.transform.position = Vector3.SmoothDamp(
            targetCamera.transform.position,
            desiredPosition,
            ref followVelocity,
            followSmoothTime);

        Vector3 lookAt = transform.TransformPoint(thirdPersonLookAtLocalPosition);
        Vector3 direction = lookAt - targetCamera.transform.position;
        if (direction.sqrMagnitude > 0.0001f)
            targetCamera.transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
    }
}

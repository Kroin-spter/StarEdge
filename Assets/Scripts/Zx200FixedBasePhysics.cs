using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 配置 ZX200 关节树的质量、重力与碰撞，并根据驾驶模式固定或释放底盘。
/// </summary>
[DefaultExecutionOrder(-200)]
public class Zx200FixedBasePhysics : MonoBehaviour
{
    [Header("Articulation Bodies")]
    public ArticulationBody rootBody;
    public ArticulationBody swingBody;
    public ArticulationBody boomBody;
    public ArticulationBody armBody;
    public ArticulationBody bucketBody;

    [Header("Engineering Proxy Masses (kg)")]
    [Min(0.001f)] public float rootMass = 7000f;
    [Min(0.001f)] public float swingMass = 8000f;
    [Min(0.001f)] public float boomMass = 2000f;
    [Min(0.001f)] public float armMass = 1200f;
    [Min(0.001f)] public float bucketMass = 800f;

    [Header("上车体后部附加配重")]
    [Min(0f)] public float counterweightMass = 2000f;
    public Vector3 counterweightLocalPosition = new Vector3(0f, 0.85f, -1.65f);
    public ArticulationBody counterweightBody;

    [Header("Stability")]
    [Min(0f)] public float linearDamping = 0.1f;
    [Min(0f)] public float angularDamping = 0.2f;
    [Min(0f)] public float jointFriction = 0.05f;

    private void Awake()
    {
        if (Configure()) return;

        ValidateConfiguration(out string error);
        Debug.LogError($"ZX200 fixed-base physics configuration failed: {error}", this);
        enabled = false;
    }

    /// <summary>
    /// 按名称从 ZX200 层级中查找底盘及四个可动关节刚体。
    /// </summary>
    /// <returns>找到底盘和全部四个可动关节刚体时返回 true。</returns>
    public bool ResolveBodies()
    {
        foreach (ArticulationBody body in GetComponentsInChildren<ArticulationBody>(true))
        {
            switch (body.name)
            {
                case "track_link": rootBody = rootBody != null ? rootBody : body; break;
                case "body_link": swingBody = swingBody != null ? swingBody : body; break;
                case "boom_link": boomBody = boomBody != null ? boomBody : body; break;
                case "arm_link": armBody = armBody != null ? armBody : body; break;
                case "bucket_link": bucketBody = bucketBody != null ? bucketBody : body; break;
            }
        }

        return rootBody != null && swingBody != null && boomBody != null &&
               armBody != null && bucketBody != null;
    }

    /// <summary>
    /// 配置代理质量、重力、阻尼以及相邻连杆间的碰撞过滤。
    /// </summary>
    /// <returns>全部必需刚体和配置值有效时返回 true。</returns>
    public bool Configure()
    {
        if (!ResolveBodies() || !ValidateProxyValues(out _)) return false;

        var keyboard = GetComponent<Zx200KeyboardController>();
        bool fixedBase = keyboard == null || keyboard.fixedBase;
        rootBody.immovable = fixedBase;
        rootBody.useGravity = !fixedBase;
        rootBody.linearDamping = linearDamping;
        rootBody.angularDamping = angularDamping;
        rootBody.mass = rootMass;
        rootBody.automaticCenterOfMass = true;
        rootBody.automaticInertiaTensor = true;
        ConfigureMovingBody(swingBody, swingMass);
        ConfigureMovingBody(boomBody, boomMass);
        ConfigureMovingBody(armBody, armMass);
        ConfigureMovingBody(bucketBody, bucketMass);
        ConfigureCounterweight();

        EnableOwnedColliders(rootBody);
        EnableOwnedColliders(swingBody);
        EnableOwnedColliders(boomBody);
        EnableOwnedColliders(armBody);
        EnableOwnedColliders(bucketBody);

        IgnoreCollisions(rootBody, swingBody);
        IgnoreCollisions(swingBody, boomBody);
        IgnoreCollisions(boomBody, armBody);
        IgnoreCollisions(armBody, bucketBody);

        ArticulationBody bucketEnd = FindBody("bucket_end_link");
        if (bucketEnd != null) IgnoreCollisions(bucketBody, bucketEnd);

        return ValidateConfiguration(out _);
    }

    /// <summary>
    /// 校验刚体引用及当前质量、惯量和驾驶模式。
    /// </summary>
    /// <param name="error">输出第一个无效字段或状态的说明。</param>
    /// <returns>物理配置满足当前驾驶模式要求时返回 true。</returns>
    public bool ValidateConfiguration(out string error)
    {
        if (!ResolveBodies())
        {
            error = "one or more required articulation bodies are missing";
            return false;
        }
        if (!ValidateProxyValues(out error)) return false;
        var keyboard = GetComponent<Zx200KeyboardController>();
        bool expectedFixed = keyboard == null || keyboard.fixedBase;
        if (rootBody.immovable != expectedFixed || rootBody.useGravity == expectedFixed)
        {
            error = "底盘固定状态和重力必须与驾驶模式一致";
            return false;
        }

        if (!ValidateBody(swingBody, "body_link", out error) ||
            !ValidateBody(boomBody, "boom_link", out error) ||
            !ValidateBody(armBody, "arm_link", out error) ||
            !ValidateBody(bucketBody, "bucket_link", out error))
        {
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 判断物理参数是否为有限正数。
    /// </summary>
    /// <param name="value">需要检查的数值。</param>
    /// <returns>数值有限且大于零时返回 true。</returns>
    public static bool IsFinitePositive(float value)
    {
        return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void ConfigureMovingBody(ArticulationBody body, float mass)
    {
        body.mass = mass;
        body.useGravity = true;
        body.linearDamping = linearDamping;
        body.angularDamping = angularDamping;
        body.jointFriction = jointFriction;
        body.automaticCenterOfMass = true;
        body.automaticInertiaTensor = true;
    }

    private void ConfigureCounterweight()
    {
        // 独立固定关节只增加后部质量与惯量，不增加运动自由度或重叠碰撞体。
        // 保留上车原质量和自动惯量，避免把新增质量均匀摊到驾驶室和机械臂上。
        if (counterweightBody == null)
        {
            Transform existing = swingBody.transform.Find("counterweight_link");
            if (existing == null)
            {
                existing = new GameObject("counterweight_link").transform;
                existing.SetParent(swingBody.transform, false);
            }
            counterweightBody = existing.GetComponent<ArticulationBody>();
            if (counterweightBody == null) counterweightBody = existing.gameObject.AddComponent<ArticulationBody>();
        }
        counterweightBody.gameObject.SetActive(counterweightMass > 0f);
        if (counterweightMass <= 0f) return;
        counterweightBody.transform.localPosition = counterweightLocalPosition;
        counterweightBody.transform.localRotation = Quaternion.identity;
        counterweightBody.jointType = ArticulationJointType.FixedJoint;
        counterweightBody.matchAnchors = true;
        counterweightBody.mass = counterweightMass;
        counterweightBody.useGravity = true;
        counterweightBody.automaticCenterOfMass = false;
        counterweightBody.centerOfMass = Vector3.zero;
        counterweightBody.automaticInertiaTensor = false;
        // 以 2×0.8×0.7 米的均匀箱体估算配重自身惯量；安装偏移由关节树计算。
        counterweightBody.inertiaTensor = counterweightMass / 12f *
            new Vector3(0.8f * 0.8f + 0.7f * 0.7f, 2f * 2f + 0.7f * 0.7f, 2f * 2f + 0.8f * 0.8f);
        counterweightBody.inertiaTensorRotation = Quaternion.identity;
        counterweightBody.linearDamping = linearDamping;
        counterweightBody.angularDamping = angularDamping;
    }

    private bool ValidateProxyValues(out string error)
    {
        if (!IsFinitePositive(rootMass)) return Invalid("rootMass", out error);
        if (!IsFinitePositive(swingMass)) return Invalid("swingMass", out error);
        if (!IsFinitePositive(boomMass)) return Invalid("boomMass", out error);
        if (!IsFinitePositive(armMass)) return Invalid("armMass", out error);
        if (!IsFinitePositive(bucketMass)) return Invalid("bucketMass", out error);
        if (!IsFiniteNonNegative(counterweightMass)) return InvalidNonNegative("counterweightMass", out error);
        if (!float.IsFinite(counterweightLocalPosition.x) || !float.IsFinite(counterweightLocalPosition.y) ||
            !float.IsFinite(counterweightLocalPosition.z)) return Invalid("counterweightLocalPosition", out error);
        if (!IsFiniteNonNegative(linearDamping))
            return InvalidNonNegative("linearDamping", out error);
        if (!IsFiniteNonNegative(angularDamping))
            return InvalidNonNegative("angularDamping", out error);
        if (!IsFiniteNonNegative(jointFriction))
            return InvalidNonNegative("jointFriction", out error);

        error = string.Empty;
        return true;
    }

    private static bool ValidateBody(ArticulationBody body, string bodyName, out string error)
    {
        Vector3 inertia = body.inertiaTensor;
        if (!IsFinitePositive(body.mass) || !IsFinitePositive(inertia.x) ||
            !IsFinitePositive(inertia.y) || !IsFinitePositive(inertia.z))
        {
            error = $"{bodyName} has an invalid mass or inertia tensor";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool Invalid(string fieldName, out string error)
    {
        error = $"{fieldName} must be finite and greater than zero";
        return false;
    }

    private static bool InvalidNonNegative(string fieldName, out string error)
    {
        error = $"{fieldName} must be finite and non-negative";
        return false;
    }

    private static bool IsFiniteNonNegative(float value)
    {
        return value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private ArticulationBody FindBody(string bodyName)
    {
        foreach (ArticulationBody body in GetComponentsInChildren<ArticulationBody>(true))
        {
            if (body.name == bodyName) return body;
        }
        return null;
    }

    private static void IgnoreCollisions(ArticulationBody first, ArticulationBody second)
    {
        List<Collider> firstColliders = GetOwnedColliders(first);
        List<Collider> secondColliders = GetOwnedColliders(second);
        foreach (Collider firstCollider in firstColliders)
        {
            foreach (Collider secondCollider in secondColliders)
            {
                Physics.IgnoreCollision(firstCollider, secondCollider, true);
            }
        }
    }

    private static void EnableOwnedColliders(ArticulationBody owner)
    {
        foreach (Collider collider in GetOwnedColliders(owner))
        {
            collider.enabled = true;
        }
    }

    private static List<Collider> GetOwnedColliders(ArticulationBody owner)
    {
        var owned = new List<Collider>();
        foreach (Collider collider in owner.GetComponentsInChildren<Collider>(true))
        {
            if (collider.GetComponentInParent<ArticulationBody>() == owner)
            {
                owned.Add(collider);
            }
        }
        return owned;
    }
}

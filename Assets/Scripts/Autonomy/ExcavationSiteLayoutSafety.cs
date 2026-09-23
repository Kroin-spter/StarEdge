using UnityEngine;

/// <summary>提供施工场地土区与车辆停车位的二维安全间距判定。</summary>
public static class ExcavationSiteLayoutSafety
{
    /// <summary>判断车辆包围盒是否完全位于土区扩大边界之外。</summary>
    /// <param name="soilBounds">可挖土区的世界包围盒。</param>
    /// <param name="truckBounds">车辆全部实体碰撞体合并后的世界包围盒。</param>
    /// <param name="clearanceMeters">土区与车辆之间要求保留的水平净距，单位为米。</param>
    /// <returns>车辆与扩大后的土区边界没有水平重叠时返回 true。</returns>
    public static bool HasRequiredClearance(Bounds soilBounds, Bounds truckBounds, float clearanceMeters)
    {
        if (!IsFinite(clearanceMeters) || clearanceMeters < 0 ||
            !IsValidSize(soilBounds.size) || !IsValidSize(truckBounds.size)) return false;

        Bounds expandedSoil = soilBounds;
        expandedSoil.Expand(new Vector3(clearanceMeters * 2f, 0f, clearanceMeters * 2f));
        return truckBounds.max.x <= expandedSoil.min.x ||
            truckBounds.min.x >= expandedSoil.max.x ||
            truckBounds.max.z <= expandedSoil.min.z ||
            truckBounds.min.z >= expandedSoil.max.z;
    }

    /// <summary>判断一个世界点是否进入土区水平包围盒及其安全扩展区域。</summary>
    /// <param name="point">待检查的世界坐标点。</param>
    /// <param name="bounds">土区世界包围盒。</param>
    /// <param name="marginMeters">在 X/Z 方向扩大的边界，单位为米。</param>
    /// <returns>点位于扩大边界内时返回 true。</returns>
    public static bool IsInsideExpandedBounds(Vector3 point, Bounds bounds, float marginMeters)
    {
        if (!IsFinite(point) || !IsFinite(marginMeters) || marginMeters < 0 || !IsValidSize(bounds.size))
            return false;
        return point.x >= bounds.min.x - marginMeters && point.x <= bounds.max.x + marginMeters &&
            point.z >= bounds.min.z - marginMeters && point.z <= bounds.max.z + marginMeters;
    }

    private static bool IsValidSize(Vector3 size)
    {
        return IsFinite(size) && size.x > 0 && size.y > 0 && size.z > 0;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

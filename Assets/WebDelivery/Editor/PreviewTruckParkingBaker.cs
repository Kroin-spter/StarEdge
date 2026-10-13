using System;

/// <summary>Retains the legacy batch entry point but prevents regeneration of constant-height truck pads.</summary>
public static class PreviewTruckParkingBaker
{
    /// <summary>Rejects the obsolete flattening operation without opening or changing any scene or asset.</summary>
    /// <exception cref="NotSupportedException">Always thrown because natural wheel-bearing parking replaced this operation.</exception>
    public static void ApplyBatch()
    {
        throw new NotSupportedException("等高停车平台烘焙入口已停用；请使用自然地面恢复流程，避免再次压平地形或改变轮胎支撑高度。");
    }
}

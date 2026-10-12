using UnityEngine;

/// <summary>Continuous mound-top and corner excavation profile with a protected track-bearing core.</summary>
public static class MoundTopExcavationProfile
{
    /// <summary>Evaluate surface and excavation floor in machine-local horizontal coordinates.</summary>
    /// <param name="local">Horizontal metres relative to the excavator, positive Y facing its boom.</param>
    /// <param name="benchY">World elevation of the lower truck bench.</param>
    /// <param name="supportY">World elevation of the excavator's level track support.</param>
    /// <returns>World surface elevation in X and non-excavatable floor elevation in Y.</returns>
    public static Vector2 Evaluate(Vector2 local, float benchY, float supportY)
    {
        float radius = local.magnitude;
        float slope = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 10f, radius));
        float surface = Mathf.Lerp(supportY, benchY, slope);
        // The truck parks beside, not under, the mound. Blend its level receiving pad into the foot.
        float parkingDistance = Mathf.Max(Mathf.Abs(local.x + 7f) / 2.8f, Mathf.Abs(local.y) / 4.8f);
        float parkingBlend = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.8f, 1.25f, parkingDistance));
        surface = Mathf.Lerp(surface, benchY, parkingBlend);
        float protectedCore = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2.6f, 3.6f, radius));
        float floor = Mathf.Lerp(benchY - .65f, surface, protectedCore);
        return new Vector2(surface, floor);
    }
}

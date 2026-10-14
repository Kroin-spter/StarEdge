using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>Reversible natural-mine grading and camera smoothing, independent of machine controls.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera), typeof(UniversalAdditionalCameraData))]
public sealed class MineVisualPreset : MonoBehaviour
{
    [Header("Presentation")]
    public bool enhancementEnabled = true;
    public bool showToggle = true;
    public KeyCode toggleKey = KeyCode.F8;
    public Light sunlight;
    [Tooltip("Serialized profile also keeps the required post-processing variants in player builds.")]
    public VolumeProfile presetProfile;
    [Range(-1, 1)] public float exposure = .2f;
    [Range(-20, 20)] public float contrast = 8;
    [Range(-20, 20)] public float saturation = 3;
    [Range(-20, 20)] public float temperature = 2;
    [Range(.5f, 1)] public float shadowStrengthScale = .82f;

    /// <summary>Whether the owned preset is currently applied to the camera.</summary>
    public bool IsEnhanced { get; private set; }

    private UniversalAdditionalCameraData cameraData;
    private Volume volume;
    private VolumeProfile profile;
    private bool captured;
    private bool originalPostProcessing;
    private AntialiasingMode originalAntialiasing;
    private LayerMask originalVolumeMask;
    private float originalShadowStrength;
    private Light capturedSunlight;

    private void OnEnable() => SetEnhanced(enhancementEnabled);

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey)) SetEnhanced(!IsEnhanced);
        else if (enhancementEnabled != IsEnhanced) SetEnhanced(enhancementEnabled);
    }

    /// <summary>Apply the owned natural preset, or restore the camera and light's captured presentation.</summary>
    /// <param name="enabled">True to enable mild grading and FXAA; false to restore the original settings.</param>
    public void SetEnhanced(bool enabled)
    {
        enhancementEnabled = enabled;
        if (!isActiveAndEnabled) return;
        if (enabled)
        {
            CaptureOriginal();
            EnsureVolume();
            ApplyTuning();
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            cameraData.volumeLayerMask = originalVolumeMask | (1 << gameObject.layer);
            volume.enabled = true;
            if (capturedSunlight != null)
                capturedSunlight.shadowStrength = originalShadowStrength * shadowStrengthScale;
        }
        else
        {
            RestoreOriginal();
            // While the preset is off, lighting/camera edits become the next baseline.
            captured = false;
        }
        IsEnhanced = enabled;
    }

    private void CaptureOriginal()
    {
        if (captured) return;
        cameraData = GetComponent<UniversalAdditionalCameraData>();
        originalPostProcessing = cameraData.renderPostProcessing;
        originalAntialiasing = cameraData.antialiasing;
        originalVolumeMask = cameraData.volumeLayerMask;
        capturedSunlight = sunlight;
        if (capturedSunlight != null) originalShadowStrength = capturedSunlight.shadowStrength;
        captured = true;
    }

    private void EnsureVolume()
    {
        if (volume != null) return;
        var owner = new GameObject("Natural mine visual volume") { hideFlags = HideFlags.DontSave };
        owner.transform.SetParent(transform, false);
        owner.layer = gameObject.layer;
        volume = owner.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 100;
        volume.weight = 1;
        volume.sharedProfile = presetProfile;
        profile = volume.profile;
        profile.name = "Natural mine (runtime)";
        profile.hideFlags = HideFlags.DontSave;
    }

    private void ApplyTuning()
    {
        // Neutral compresses highlights without noticeably changing safety-yellow/orange paint.
        GetOrAdd<Tonemapping>().mode.Override(TonemappingMode.Neutral);
        var grading = GetOrAdd<ColorAdjustments>();
        grading.postExposure.Override(exposure);
        grading.contrast.Override(contrast);
        grading.saturation.Override(saturation);
        GetOrAdd<WhiteBalance>().temperature.Override(temperature);
        // Keep machine-control details sharp even if the pipeline defaults later change.
        GetOrAdd<Bloom>().intensity.Override(0);
        GetOrAdd<DepthOfField>().mode.Override(DepthOfFieldMode.Off);
        GetOrAdd<MotionBlur>().intensity.Override(0);
        GetOrAdd<FilmGrain>().intensity.Override(0);
        GetOrAdd<ChromaticAberration>().intensity.Override(0);
        GetOrAdd<Vignette>().intensity.Override(0);
    }

    private T GetOrAdd<T>() where T : VolumeComponent
        => profile.TryGet<T>(out var component) ? component : profile.Add<T>();

    private void RestoreOriginal()
    {
        if (!captured) return;
        if (volume != null) volume.enabled = false;
        if (cameraData != null)
        {
            cameraData.renderPostProcessing = originalPostProcessing;
            cameraData.antialiasing = originalAntialiasing;
            cameraData.volumeLayerMask = originalVolumeMask;
        }
        if (capturedSunlight != null) capturedSunlight.shadowStrength = originalShadowStrength;
        IsEnhanced = false;
    }

    private void OnDisable()
    {
        RestoreOriginal();
        captured = false;
    }

    private void OnDestroy()
    {
        RestoreOriginal();
        if (volume != null) Release(volume.gameObject);
        if (profile == null) return;
        foreach (var component in profile.components) Release(component);
        Release(profile);
    }

    private static void Release(Object resource)
    {
        if (Application.isPlaying) Destroy(resource);
        else DestroyImmediate(resource);
    }

    private void OnGUI()
    {
        if (!showToggle) return;
        if (GUI.Button(new Rect(Screen.width - 184, 20, 164, 30),
            IsEnhanced ? "画面：自然  [F8]" : "画面：原始  [F8]")) SetEnhanced(!IsEnhanced);
    }
}

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>Reversible natural-mine grading and optional background blur, independent of machine controls.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera), typeof(UniversalAdditionalCameraData))]
public sealed class MineVisualPreset : MonoBehaviour
{
    [Header("Presentation")]
    public bool enhancementEnabled = true;
    public bool showToggle = true;
    public KeyCode toggleKey = KeyCode.F8;
    public bool presentationEnabled = false;
    public KeyCode presentationToggleKey = KeyCode.F9;
    public DepthOfFieldMode presentationMode = DepthOfFieldMode.Bokeh;
    [Range(1, 32)] public float aperture = 2.8f;
    [Range(1, 300)] public float focalLength = 85f;
    [Tooltip("Explicit subject reference; missing or behind-camera subjects keep the view clear.")]
    public Transform focusTarget;
    public ExcavatorAppearance equipmentAppearance;
    [Min(0)] public float focusSafetyAllowance = 3f;
    [Min(3)] public float clearViewDistance = 3f;
    [Min(.01f)] public float focusSmoothTime = .35f;
    [Min(.1f)] public float backgroundBlurRange = 15f;
    [Range(.5f, 1)] public float backgroundBlurRadius = .75f;
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
    private float smoothedFocusDistance;
    private float focusVelocity;
    private bool focusInitialized;

    private void OnEnable() => SetEnhanced(enhancementEnabled);

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey)) SetEnhanced(!IsEnhanced);
        else if (enhancementEnabled != IsEnhanced) SetEnhanced(enhancementEnabled);
        if (Input.GetKeyDown(presentationToggleKey)) SetPresentation(!presentationEnabled);
    }

    private void LateUpdate() => ApplyPresentation(Time.unscaledDeltaTime);

    /// <summary>Request showcase background blur; close or invalid subjects automatically remain clear.</summary>
    /// <param name="enabled">Whether presentation blur is requested while enhancement is active.</param>
    public void SetPresentation(bool enabled)
    {
        presentationEnabled = enabled;
        if (isActiveAndEnabled) ApplyPresentation(0);
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
        if (equipmentAppearance != null) equipmentAppearance.SetDetailed(enabled);
        if (enabled) ApplyPresentation(0);
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

    private void ApplyPresentation(float deltaTime)
    {
        if (profile == null) return;
        var dof = GetOrAdd<DepthOfField>();
        // Both focus models use forward camera depth, not radial distance to the subject.
        float depth = focusTarget != null
            ? Vector3.Dot(transform.forward, focusTarget.position - transform.position) : 0;
        if (!IsEnhanced || !presentationEnabled || presentationMode == DepthOfFieldMode.Off || focusTarget == null ||
            float.IsNaN(depth) || float.IsInfinity(depth) || depth <= Mathf.Max(3, clearViewDistance))
        {
            dof.mode.Override(DepthOfFieldMode.Off);
            focusInitialized = false;
            focusVelocity = 0;
            return;
        }
        if (!focusInitialized)
        {
            smoothedFocusDistance = depth;
            focusInitialized = true;
        }
        else if (deltaTime > 0)
            smoothedFocusDistance = Mathf.SmoothDamp(smoothedFocusDistance, depth, ref focusVelocity,
                Mathf.Max(.01f, focusSmoothTime), Mathf.Infinity, deltaTime);
        dof.active = true;
        if (presentationMode == DepthOfFieldMode.Bokeh)
        {
            dof.focusDistance.Override(smoothedFocusDistance);
            dof.aperture.Override(Mathf.Clamp(aperture, 1, 32));
            dof.focalLength.Override(Mathf.Clamp(focalLength, 1, 300));
            dof.mode.Override(DepthOfFieldMode.Bokeh);
            return;
        }
        // Gaussian outward motion must never place the subject inside blur while focus catches up.
        float blurStart = Mathf.Max(depth, smoothedFocusDistance) + Mathf.Max(0, focusSafetyAllowance);
        dof.gaussianStart.Override(blurStart);
        dof.gaussianEnd.Override(blurStart + Mathf.Max(.1f, backgroundBlurRange));
        dof.gaussianMaxRadius.Override(Mathf.Clamp(backgroundBlurRadius, .5f, 1));
        dof.highQualitySampling.Override(false);
        dof.mode.Override(DepthOfFieldMode.Gaussian);
    }

    private T GetOrAdd<T>() where T : VolumeComponent
        => profile.TryGet<T>(out var component) ? component : profile.Add<T>();

    private void RestoreOriginal()
    {
        if (profile != null) GetOrAdd<DepthOfField>().mode.Override(DepthOfFieldMode.Off);
        focusInitialized = false;
        focusVelocity = 0;
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
        if (equipmentAppearance != null) equipmentAppearance.SetDetailed(false);
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
        if (GUI.Button(new Rect(Screen.width - 184, 56, 164, 30),
            presentationEnabled ? "展示景深：开  [F9]" : "展示景深：关  [F9]"))
            SetPresentation(!presentationEnabled);
    }
}

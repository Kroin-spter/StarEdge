using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Owned, reversible PBR surfaces and link-attached hardware for the existing URDF visuals.</summary>
[DisallowMultipleComponent]
public sealed class ExcavatorAppearance : MonoBehaviour
{
    public Shader equipmentShader;
    public bool detailedAppearance = true;
    [Range(0,1)] public float weathering = .4f;
    [Range(0,1)] public float surfaceDetail = .7f;
    public bool addMechanicalDetails = true;

    /// <summary>True while upgraded materials and optional mechanical details are applied.</summary>
    public bool IsDetailed { get; private set; }

    private sealed class Visual
    {
        internal MeshRenderer renderer;
        internal Material[] original, upgraded;
    }
    private readonly List<Visual> visuals = new List<Visual>();
    private readonly List<Material> ownedMaterials = new List<Material>();
    private readonly List<Mesh> ownedMeshes = new List<Mesh>();
    private readonly List<GameObject> detailObjects = new List<GameObject>();
    private bool prepared;
    private ReflectionProbe environmentProbe;
    private bool reflectionCaptured, captureScheduled;

    private void OnEnable() => SetDetailed(detailedAppearance);

    private IEnumerator CaptureEnvironment()
    {
        // The baked construction site is populated after scene load. Capture its
        // sky/soil once, after initialization, rather than rendering six views per frame.
        for (int frame = 0; frame < 4; frame++) yield return null;
        if (environmentProbe != null && IsDetailed)
        {
            int capture = environmentProbe.RenderProbe();
            while (capture >= 0 && environmentProbe != null && !environmentProbe.IsFinishedRendering(capture))
                yield return null;
            reflectionCaptured = capture >= 0;
        }
        captureScheduled = false;
    }

    /// <summary>Swap only visual materials/details, preserving every collider and articulation component.</summary>
    /// <param name="enabled">True for weathered materials and hardware, false for original URDF visuals.</param>
    public void SetDetailed(bool enabled)
    {
        detailedAppearance = enabled;
        if (!isActiveAndEnabled) return;
        if (!enabled && captureScheduled)
        {
            StopAllCoroutines();
            captureScheduled = false;
        }
        if (!prepared && enabled) Prepare();
        if (enabled)
            foreach (var material in ownedMaterials)
                if (material != null)
                {
                    material.SetFloat("_Weathering",weathering);
                    material.SetFloat("_DetailStrength",surfaceDetail);
                }
        foreach (var visual in visuals)
            if (visual.renderer != null)
                visual.renderer.sharedMaterials = enabled ? visual.upgraded : visual.original;
        foreach (var detail in detailObjects)
            if (detail != null) detail.SetActive(enabled && addMechanicalDetails);
        if (environmentProbe != null) environmentProbe.enabled = enabled;
        IsDetailed = enabled && prepared;
        if (IsDetailed && environmentProbe != null && !reflectionCaptured && !captureScheduled)
        {
            captureScheduled = true;
            StartCoroutine(CaptureEnvironment());
        }
    }

    private void Prepare()
    {
        if (equipmentShader == null) equipmentShader = Shader.Find("Mine/Weathered Equipment");
        if (equipmentShader == null || !equipmentShader.isSupported)
        {
            Debug.LogWarning("Weathered equipment shader unavailable; retaining original robot appearance.",this);
            return;
        }
        var links = new Dictionary<string,Transform>();
        foreach (var node in GetComponentsInChildren<Transform>(true))
            if (node.name.EndsWith("_link")) links[node.name] = node;
        foreach (var renderer in GetComponentsInChildren<MeshRenderer>(true))
        {
            // Imported collision meshes can have identical names. Upgrade only URDF Visuals.
            Transform link = null;
            bool inVisuals = false;
            for (Transform ancestor = renderer.transform; ancestor != null && ancestor != transform; ancestor = ancestor.parent)
            {
                inVisuals |= ancestor.name == "Visuals";
                if (ancestor.name.EndsWith("_link")) { link = ancestor; break; }
            }
            if (!inVisuals || link == null || renderer.name.StartsWith("lamp-")) continue;
            Material[] originals = renderer.sharedMaterials;
            var upgrades = new Material[originals.Length];
            float kind = link.name == "track_link" ? 1 : link.name == "bucket_link" ? 2 : 0;
            for (int i = 0; i < originals.Length; i++)
            {
                Material original = originals[i];
                if (original == null) continue;
                var material = NewMaterial(original.name + " weathered",kind);
                if (original.HasProperty("_BaseMap"))
                {
                    material.SetTexture("_BaseMap",original.GetTexture("_BaseMap"));
                    material.SetTextureScale("_BaseMap",original.GetTextureScale("_BaseMap"));
                    material.SetTextureOffset("_BaseMap",original.GetTextureOffset("_BaseMap"));
                }
                if (original.HasProperty("_BaseColor")) material.SetColor("_BaseColor",original.GetColor("_BaseColor"));
                material.SetMatrix("_ObjectToLink",link.worldToLocalMatrix * renderer.transform.localToWorldMatrix);
                material.SetFloat("_IsBody",link.name == "body_link" ? 1 : 0);
                if (link.name == "body_link") material.EnableKeyword("_CLEARCOAT");
                upgrades[i] = material;
            }
            visuals.Add(new Visual { renderer = renderer, original = originals, upgraded = upgrades });
        }
        if (addMechanicalDetails)
        {
            if (links.TryGetValue("boom_link",out var boom) && links.TryGetValue("arm_link",out var arm))
                AddDetails(boom,boom.InverseTransformPoint(arm.position),.26f,.24f,.5f);
            if (links.TryGetValue("arm_link",out arm) && links.TryGetValue("bucket_link",out var bucket))
                AddDetails(arm,arm.InverseTransformPoint(bucket.position),.22f,.2f,.1f);
        }
        // Respect the project's reflection quality setting. The weathered shader
        // still uses the existing sky/environment atlas when realtime captures are off.
        if (QualitySettings.realtimeReflectionProbes) PrepareEnvironmentProbe();
        prepared = true;
    }

    private void PrepareEnvironmentProbe()
    {
        var reflection = new GameObject("Equipment environment reflection") { hideFlags = HideFlags.DontSave };
        reflection.transform.SetParent(transform,false);
        reflection.transform.localPosition = Vector3.up * 5;
        environmentProbe = reflection.AddComponent<ReflectionProbe>();
        environmentProbe.mode = ReflectionProbeMode.Realtime;
        environmentProbe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
        environmentProbe.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
        environmentProbe.resolution = 128;
        environmentProbe.size = new Vector3(36,24,36);
        environmentProbe.blendDistance = 4;
        environmentProbe.importance = 1;
        environmentProbe.nearClipPlane = .2f;
        environmentProbe.farClipPlane = 80;
        environmentProbe.hdr = true;
    }

    private Material NewMaterial(string name, float kind)
    {
        var material = new Material(equipmentShader) { name = name, hideFlags = HideFlags.DontSave };
        material.SetFloat("_PartKind",kind);
        material.SetFloat("_Weathering",weathering);
        material.SetFloat("_DetailStrength",surfaceDetail);
        ownedMaterials.Add(material);
        return material;
    }

    private void AddDetails(Transform link, Vector3 end, float halfWidth, float hoseOffset, float arch)
    {
        var hardware = NewMaterial("Retaining pins · worn steel",3);
        hardware.SetColor("_BaseColor",new Color(.29f,.32f,.35f));
        AddMesh(link,ExcavatorDetailMeshes.JointHardware(end,halfWidth),hardware);
        var hose = NewMaterial("Hydraulic hoses · rubber",4);
        hose.SetColor("_BaseColor",new Color(.045f,.045f,.045f));
        AddMesh(link,ExcavatorDetailMeshes.Hoses(end,hoseOffset,arch),hose);
    }

    private void AddMesh(Transform link, Mesh mesh, Material material)
    {
        var detail = new GameObject(mesh.name) { hideFlags = HideFlags.DontSave };
        detail.transform.SetParent(link,false);
        detail.layer = link.gameObject.layer;
        detail.AddComponent<MeshFilter>().sharedMesh = mesh;
        detail.AddComponent<MeshRenderer>().sharedMaterial = material;
        material.SetMatrix("_ObjectToLink",Matrix4x4.identity);
        ownedMeshes.Add(mesh);
        detailObjects.Add(detail);
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        captureScheduled = false;
        foreach (var visual in visuals)
            if (visual.renderer != null) visual.renderer.sharedMaterials = visual.original;
        foreach (var detail in detailObjects)
            if (detail != null) detail.SetActive(false);
        IsDetailed = false;
        if (environmentProbe != null) environmentProbe.enabled = false;
    }

    private void OnDestroy()
    {
        OnDisable();
        if (environmentProbe != null) Release(environmentProbe.gameObject);
        foreach (var detail in detailObjects) if (detail != null) Release(detail);
        foreach (var mesh in ownedMeshes) if (mesh != null) Release(mesh);
        foreach (var material in ownedMaterials) if (material != null) Release(material);
    }

    private static void Release(Object resource)
    {
        if (Application.isPlaying) Destroy(resource);
        else DestroyImmediate(resource);
    }
}

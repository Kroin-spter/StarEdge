using System.Collections;
using System.Linq;
using CesiumForUnity;
using UnityEngine;

/// <summary>Hold the excavator at spawn until streamed Cesium collision meshes support its tracks.</summary>
[DefaultExecutionOrder(-11000)]
public sealed class CesiumWebGroundReady : MonoBehaviour
{
    private Zx200KeyboardController keyboard;
    private ArticulationBody rootBody;
    private bool originalFixedBase;
    private bool originalKeyboardEnabled;

    private void Awake()
    {
        // The mound initializer owns the whole site's loading gate. A second gate would
        // capture its temporarily disabled keyboard as the state to restore forever.
        if (FindObjectsByType<CesiumMoundSite>(FindObjectsSortMode.None)
            .Any(site => site.isActiveAndEnabled && site.machine != null && site.machine.gameObject == gameObject))
        {
            enabled = false;
            return;
        }
        keyboard = GetComponent<Zx200KeyboardController>();
        rootBody = GetComponentsInChildren<ArticulationBody>(true).FirstOrDefault(body => body.isRoot);
        if (keyboard == null || rootBody == null) return;
        originalFixedBase = keyboard.fixedBase;
        originalKeyboardEnabled = keyboard.enabled;
        keyboard.fixedBase = true;
        keyboard.enabled = false;
        rootBody.immovable = true;
    }

    private IEnumerator Start()
    {
        if (keyboard == null || rootBody == null) yield break;
        var tilesets = FindObjectsByType<Cesium3DTileset>(FindObjectsSortMode.None);
        float stableSince = Time.realtimeSinceStartup;
        float previousHeight = float.NaN;
        // Sample the track footprint only, excluding the moving arm and bucket.
        var tracks = GetComponentsInChildren<Collider>().Where(collider =>
            collider.attachedArticulationBody == rootBody && !collider.isTrigger).ToArray();
        while (true)
        {
            Bounds bounds = new Bounds(rootBody.transform.position, Vector3.one);
            if (tracks.Length > 0)
            {
                bounds = tracks[0].bounds;
                foreach (var track in tracks) bounds.Encapsulate(track.bounds);
            }
            var center = bounds.center;
            var half = bounds.extents * 0.7f;
            Vector3[] samples = {
                center, center + new Vector3(half.x, 0, half.z),
                center + new Vector3(-half.x, 0, half.z),
                center + new Vector3(half.x, 0, -half.z),
                center + new Vector3(-half.x, 0, -half.z)
            };
            var colliders = tilesets.Where(tileset => tileset != null)
                .SelectMany(tileset => tileset.GetComponentsInChildren<MeshCollider>()).ToArray();
            bool supported = true;
            float groundHeight = float.NegativeInfinity;
            foreach (var sample in samples)
            {
                bool found = false;
                var ray = new Ray(sample + Vector3.up * 50f, Vector3.down);
                foreach (var collider in colliders)
                    if (collider.enabled && collider.Raycast(ray, out var hit, 100f))
                    {
                        found = true;
                        groundHeight = Mathf.Max(groundHeight, hit.point.y);
                    }
                supported &= found;
            }
            if (!supported || float.IsNaN(previousHeight) || Mathf.Abs(groundHeight - previousHeight) > 0.1f)
                stableSince = Time.realtimeSinceStartup;
            previousHeight = groundHeight;
            if (supported && Time.realtimeSinceStartup - stableSince >= 0.75f)
            {
                Vector3 position = rootBody.transform.position;
                position.y += groundHeight + 0.05f - bounds.min.y;
                rootBody.TeleportRoot(position, rootBody.transform.rotation);
                rootBody.linearVelocity = Vector3.zero;
                rootBody.angularVelocity = Vector3.zero;
                keyboard.fixedBase = originalFixedBase;
                keyboard.enabled = originalKeyboardEnabled;
                rootBody.immovable = originalFixedBase;
                rootBody.useGravity = !originalFixedBase;
                Debug.Log("Cesium ground ready; excavator released at " + position);
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.25f);
        }
    }
}

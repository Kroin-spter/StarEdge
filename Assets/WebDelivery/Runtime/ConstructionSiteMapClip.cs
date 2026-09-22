using System.Collections.Generic;
using System.Linq;
using CesiumForUnity;
using UnityEngine;

/// <summary>Remove streamed map triangles over the local work pad from both rendering and collision.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-12000)]
public sealed class ConstructionSiteMapClip : MonoBehaviour
{
    [Tooltip("World-space work area. Triangles whose horizontal bounds overlap it are removed.")]
    public Bounds workArea = new Bounds(Vector3.zero, new Vector3(20,200,20));
    private Cesium3DTileset tileset;
    private readonly List<Replacement> replacements = new List<Replacement>();
    private CesiumTerrainConformer conformer;

    private sealed class Replacement
    {
        internal MeshFilter filter;
        internal Mesh source;
        internal Mesh clipped;
        internal MeshCollider[] colliders;
    }

    private void OnEnable()
    {
        if (!Application.isPlaying) return;
        tileset = GetComponent<Cesium3DTileset>();
        if (tileset == null) return;
        conformer = FindObjectsByType<CesiumTerrainConformer>(FindObjectsSortMode.None)
            .FirstOrDefault(candidate => candidate.tileset == null || candidate.tileset == tileset);
        if (conformer == null)
        {
            var soil = FindObjectsByType<DeformableSoilTerrain>(FindObjectsSortMode.None).FirstOrDefault();
            if (soil != null)
            {
                conformer = soil.gameObject.AddComponent<CesiumTerrainConformer>();
                conformer.tileset = tileset;
            }
        }
        tileset.OnTileGameObjectCreated += ClipTile;
        if (conformer == null || conformer.SurfaceReady) ClipTile(gameObject);
    }

    private void Update()
    {
        if (conformer != null && !conformer.SurfaceReady) return;
        // Cesium evicts individual tiles while this component stays enabled for the whole session.
        for (int i = replacements.Count - 1; i >= 0; i--)
        {
            var entry = replacements[i];
            if (entry.filter != null) continue;
            foreach (var collider in entry.colliders)
                if (collider != null && collider.sharedMesh == entry.clipped) collider.sharedMesh = entry.source;
            if (entry.clipped != null)
            {
                if (Application.isPlaying) Destroy(entry.clipped); else DestroyImmediate(entry.clipped);
            }
            replacements.RemoveAt(i);
        }
    }

    /// <summary>Clip newly streamed tile meshes conservatively and replace matching physics meshes with the same result.</summary>
    /// <param name="tile">Tile root; its children are inspected, while original shared meshes remain untouched.</param>
    public void ClipTile(GameObject tile)
    {
        if (conformer != null && !conformer.SurfaceReady) return;
        foreach (MeshFilter filter in tile.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh source = filter.sharedMesh;
            if (source == null || replacements.Exists(r => r.filter == filter && r.clipped == source)) continue;
            var renderer = filter.GetComponent<Renderer>();
            if (renderer != null && !HorizontalOverlap(renderer.bounds.min,renderer.bounds.max)) continue;
            if (!source.isReadable)
            {
                Debug.LogWarning("Cannot clip unreadable streamed work-area mesh: " + filter.name,filter);
                continue;
            }
            Vector3[] vertices = source.vertices;
            var world = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) world[i] = filter.transform.TransformPoint(vertices[i]);
            var retained = new List<int>[source.subMeshCount];
            bool changed = false;
            for (int submesh = 0; submesh < source.subMeshCount; submesh++)
            {
                int[] triangles = source.GetTriangles(submesh);
                retained[submesh] = new List<int>(triangles.Length);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 a = world[triangles[i]], b = world[triangles[i+1]], c = world[triangles[i+2]];
                    // Removing whole touching triangles avoids a rendered hole with an invisible collision sheet.
                    // The pad extends two metres past the mask to cover ordinary detailed-map triangle seams.
                    if (HorizontalOverlap(Vector3.Min(a,Vector3.Min(b,c)),Vector3.Max(a,Vector3.Max(b,c))))
                    { changed = true; continue; }
                    retained[submesh].Add(triangles[i]);
                    retained[submesh].Add(triangles[i+1]);
                    retained[submesh].Add(triangles[i+2]);
                }
            }
            if (!changed) continue;
            Mesh clipped = Instantiate(source);
            clipped.name = source.name + " (work area clipped)";
            clipped.hideFlags = HideFlags.DontSave;
            for (int i = 0; i < retained.Length; i++) clipped.SetTriangles(retained[i],i,false);
            clipped.RecalculateBounds();
            var colliders = filter.GetComponents<MeshCollider>();
            var matching = new List<MeshCollider>();
            foreach (var collider in colliders)
                if (collider.sharedMesh == source)
                {
                    matching.Add(collider);
                    collider.sharedMesh = clipped;
                }
            filter.sharedMesh = clipped;
            replacements.Add(new Replacement { filter = filter, source = source, clipped = clipped, colliders = matching.ToArray() });
        }
    }

    private bool HorizontalOverlap(Vector3 min, Vector3 max) =>
        min.x <= workArea.max.x && max.x >= workArea.min.x && min.z <= workArea.max.z && max.z >= workArea.min.z;

    private void OnDisable()
    {
        if (tileset != null) tileset.OnTileGameObjectCreated -= ClipTile;
        foreach (var replacement in replacements)
        {
            if (replacement.filter != null && replacement.filter.sharedMesh == replacement.clipped)
                replacement.filter.sharedMesh = replacement.source;
            foreach (var collider in replacement.colliders)
                if (collider != null && collider.sharedMesh == replacement.clipped) collider.sharedMesh = replacement.source;
            if (replacement.clipped != null)
            {
                if (Application.isPlaying) Destroy(replacement.clipped);
                else DestroyImmediate(replacement.clipped);
            }
        }
        replacements.Clear();
    }
}

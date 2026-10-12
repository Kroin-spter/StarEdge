using System.Collections.Generic;
using System.Linq;
using CesiumForUnity;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Remove the work-area footprint from streamed Cesium rendering and collision meshes.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(-12000)]
[ExecuteAlways]
public sealed class ConstructionSiteMapClip : MonoBehaviour
{
    [Tooltip("World-space broad-phase bounds for the slope-only Cesium replacement footprint.")]
    public Bounds workArea = new Bounds(Vector3.zero, new Vector3(20, 200, 20));
    public bool useOrientedWorkArea;
    public Vector3 workAreaCenter;
    public Quaternion workAreaRotation = Quaternion.identity;
    public Vector2 workAreaSize;
    public Vector2[] workAreaPolygonXZ;

    private Cesium3DTileset tileset;
    private readonly List<Replacement> replacements = new List<Replacement>();
    private CesiumTerrainConformer conformer;
    private CesiumMoundSite moundSite;

    private sealed class Replacement
    {
        internal MeshFilter filter;
        internal Mesh source;
        internal Mesh clipped;
        internal Mesh colliderMesh;
        internal MeshCollider[] colliders;
        internal bool colliderMeshCleared;
    }

    private struct ClipVertex
    {
        internal Vector3 world;
        internal Vector3 local;
        internal Vector3 barycentric;

        internal ClipVertex(Vector3 world, Vector3 local, Vector3 barycentric)
        {
            this.world = world;
            this.local = local;
            this.barycentric = barycentric;
        }
    }

    private struct ClipPlane
    {
        internal Vector3 normal;
        internal float boundary;
        internal bool keepGreater;

        internal ClipPlane(Vector3 normal, float boundary, bool keepGreater)
        {
            this.normal = normal;
            this.boundary = boundary;
            this.keepGreater = keepGreater;
        }
    }

    private void OnEnable()
    {
        tileset = GetComponent<Cesium3DTileset>();
        if (tileset == null) return;
        moundSite = FindObjectsByType<CesiumMoundSite>(FindObjectsSortMode.None)
            .FirstOrDefault(candidate => candidate.tileset == tileset);
        if (moundSite != null)
        {
            workArea = ReplacementBounds();
            tileset.OnTileGameObjectCreated += ClipTile;
            if (MoundSurfaceAvailable()) ClipTile(gameObject);
            return;
        }
        if (!Application.isPlaying) return;
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

    /// <summary>Refresh loaded Cesium tile meshes after the replacement terrain has been sampled and positioned.</summary>
    public void RefreshLoadedTiles()
    {
        if (tileset == null) tileset = GetComponent<Cesium3DTileset>();
        if (tileset == null) return;
        if (moundSite == null)
            moundSite = FindObjectsByType<CesiumMoundSite>(FindObjectsSortMode.None)
                .FirstOrDefault(candidate => candidate.tileset == tileset);
        if (moundSite != null)
        {
            workArea = ReplacementBounds();
            if (MoundSurfaceAvailable()) ClipTile(tileset.gameObject);
            return;
        }
        if (conformer == null || conformer.SurfaceReady) ClipTile(tileset.gameObject);
    }

    private void Update()
    {
        if (!Application.isPlaying && moundSite == null && tileset != null)
        {
            moundSite = FindObjectsByType<CesiumMoundSite>(FindObjectsSortMode.None)
                .FirstOrDefault(candidate => candidate.tileset == tileset);
            if (moundSite != null)
            {
                workArea = ReplacementBounds();
                tileset.OnTileGameObjectCreated += ClipTile;
                if (MoundSurfaceAvailable()) ClipTile(tileset.gameObject);
            }
        }
        if (conformer != null && !conformer.SurfaceReady) return;
        // Cesium can evict an individual tile while this component remains enabled for the session.
        for (int i = replacements.Count - 1; i >= 0; i--)
        {
            var entry = replacements[i];
            if (entry.filter != null) continue;
            foreach (var collider in entry.colliders)
                if (collider != null && (collider.sharedMesh == entry.colliderMesh ||
                    (entry.colliderMeshCleared && collider.sharedMesh == null))) collider.sharedMesh = entry.source;
            DestroyReplacementMeshes(entry);
            replacements.RemoveAt(i);
        }
    }

    /// <summary>Clip streamed triangles to the work-area rectangle and assign the same result to matching colliders.</summary>
    /// <param name="tile">Tile root; its child meshes are inspected without modifying their source assets.</param>
    public void ClipTile(GameObject tile)
    {
        if (tile == null || (moundSite != null && !MoundSurfaceAvailable())) return;
        if (conformer != null && !conformer.SurfaceReady) return;

        foreach (MeshFilter filter in tile.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh source = filter.sharedMesh;
            if (source == null || replacements.Exists(r => r.filter == filter && r.clipped == source)) continue;
            var renderer = filter.GetComponent<Renderer>();
            if (renderer != null && !OverlapsWorkArea(renderer.bounds.min, renderer.bounds.max)) continue;
            if (!source.isReadable)
            {
                Debug.LogWarning("Cannot clip unreadable streamed work-area mesh: " + filter.name, filter);
                continue;
            }

            Mesh clipped = CreateClippedMesh(source, filter.transform);
            if (clipped == null) continue;
            bool hasRetainedTriangles = HasTriangles(clipped);
            Mesh colliderMesh = hasRetainedTriangles ? CreateColliderMesh(clipped) : null;
            if (colliderMesh == null) hasRetainedTriangles = false;
            var matchingColliders = new List<MeshCollider>();
            foreach (var collider in filter.GetComponents<MeshCollider>())
            {
                if (collider.sharedMesh != source) continue;
                matchingColliders.Add(collider);
                collider.sharedMesh = hasRetainedTriangles ? colliderMesh : null;
            }
            filter.sharedMesh = clipped;
            replacements.Add(new Replacement
            {
                filter = filter,
                source = source,
                clipped = clipped,
                colliderMesh = colliderMesh,
                colliders = matchingColliders.ToArray(),
                colliderMeshCleared = !hasRetainedTriangles
            });
        }
    }

    private bool MoundSurfaceAvailable() => moundSite != null &&
        (moundSite.MapSurfacePrepared || (!Application.isPlaying &&
            moundSite.siteMode == CesiumMoundSite.SitePreparationMode.Baked &&
            moundSite.bakedSite != null && moundSite.bakedSite.terrainData != null));

    private Bounds ReplacementBounds()
    {
        if (!Application.isPlaying && moundSite.bakedSite != null && moundSite.bakedSite.terrainData != null)
        {
            BakedConstructionSiteData baked = moundSite.bakedSite;
            if (baked.workFaceSize.x <= 0f || baked.workFaceSize.y <= 0f)
            {
                useOrientedWorkArea = false;
                return workArea;
            }
            SetWorkFaceFootprint(baked.workFaceCenter, baked.workFaceRotation, baked.workFaceSize,
                baked.terrainOrigin.y, baked.terrainOrigin.y + baked.terrainData.size.y);
            workAreaPolygonXZ = baked.replacementPolygonXZ;
            return workArea;
        }
        if (moundSite != null && moundSite.MapSurfacePrepared)
        {
            Terrain terrain = moundSite.soil.GetComponent<Terrain>();
            float minimumY = terrain.transform.position.y;
            SetWorkFaceFootprint(moundSite.WorkFaceCenter, moundSite.WorkFaceRotation,
                moundSite.WorkFaceSize, minimumY, minimumY + terrain.terrainData.size.y);
            workAreaPolygonXZ = moundSite.bakedSite != null ? moundSite.bakedSite.replacementPolygonXZ : null;
            return workArea;
        }
        return moundSite.WorkAreaBounds;
    }

    /// <summary>Set the oriented slope-only footprint used to clip both Cesium rendering and collision.</summary>
    /// <param name="center">World-space center of the cuttable slope face.</param>
    /// <param name="rotation">World orientation whose horizontal axes define the face.</param>
    /// <param name="size">Face width along local X and depth along local Z, in metres.</param>
    /// <param name="minimumY">Bottom of the replacement Terrain's vertical range.</param>
    /// <param name="maximumY">Top of the replacement Terrain's vertical range.</param>
    public void SetWorkFaceFootprint(Vector3 center, Quaternion rotation, Vector2 size,
        float minimumY, float maximumY)
    {
        workAreaCenter = center;
        workAreaRotation = rotation;
        workAreaSize = size;
        workAreaPolygonXZ = null;
        useOrientedWorkArea = true;
        workArea = ConstructionSiteFootprint.CalculateBounds(center, rotation, size, minimumY, maximumY);
    }

    private Mesh CreateClippedMesh(Mesh source, Transform meshTransform)
    {
        Vector3[] sourceVertices = source.vertices;
        var worldVertices = new Vector3[sourceVertices.Length];
        for (int i = 0; i < sourceVertices.Length; i++)
            worldVertices[i] = meshTransform.TransformPoint(sourceVertices[i]);

        Vector3[] sourceNormals = source.normals;
        Vector4[] sourceTangents = source.tangents;
        Color[] sourceColors = source.colors;
        var uvChannels = new List<Vector4>[8];
        var uvDimensions = new int[8];
        for (int channel = 0; channel < uvChannels.Length; channel++)
        {
            var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
            if (!source.HasVertexAttribute(attribute)) continue;
            uvDimensions[channel] = source.GetVertexAttributeDimension(attribute);
            uvChannels[channel] = new List<Vector4>(source.vertexCount);
            source.GetUVs(channel, uvChannels[channel]);
        }

        var retainedBySubmesh = new List<int>[source.subMeshCount];
        var outputTopologies = new MeshTopology[source.subMeshCount];
        for (int i = 0; i < retainedBySubmesh.Length; i++)
        {
            retainedBySubmesh[i] = new List<int>(source.GetIndices(i).Length);
            outputTopologies[i] = source.GetTopology(i) == MeshTopology.Quads
                ? MeshTopology.Triangles
                : source.GetTopology(i);
        }
        var addedVertices = new List<Vector3>();
        var addedNormals = sourceNormals.Length == source.vertexCount ? new List<Vector3>(sourceNormals) : null;
        var addedTangents = sourceTangents.Length == source.vertexCount ? new List<Vector4>(sourceTangents) : null;
        var addedColors = sourceColors.Length == source.vertexCount ? new List<Color>(sourceColors) : null;
        foreach (var channel in uvChannels)
            if (channel != null && channel.Count != source.vertexCount) channel.Clear();

        bool changed = false;
        ClipPlane[] workAreaPlanes = GetWorkAreaPlanes();
        for (int submesh = 0; submesh < source.subMeshCount; submesh++)
        {
            int[] indices = source.GetIndices(submesh);
            List<int> retained = retainedBySubmesh[submesh];
            MeshTopology topology = source.GetTopology(submesh);
            if (topology == MeshTopology.Lines || topology == MeshTopology.LineStrip || topology == MeshTopology.Points)
            {
                // Lines and points are not terrain surfaces or MeshCollider faces; preserve them without
                // letting their presence prevent the triangle submeshes in this tile from being clipped.
                retained.AddRange(indices);
                continue;
            }
            if (topology == MeshTopology.Quads)
                indices = TriangulateQuads(indices);
            else if (topology != MeshTopology.Triangles)
            {
                Debug.LogWarning("Cannot clip unsupported streamed mesh topology " + topology + ": " + source.name);
                return null;
            }
            for (int i = 0; i < indices.Length; i += 3)
            {
                int aIndex = indices[i], bIndex = indices[i + 1], cIndex = indices[i + 2];
                Vector3 a = worldVertices[aIndex], b = worldVertices[bIndex], c = worldVertices[cIndex];
                Vector3 min = Vector3.Min(a, Vector3.Min(b, c));
                Vector3 max = Vector3.Max(a, Vector3.Max(b, c));
                if (!OverlapsWorkArea(min, max))
                {
                    AddOriginalTriangle(retained, aIndex, bIndex, cIndex);
                    continue;
                }

                if (IsTriangleInsideWorkArea(a, b, c, workAreaPlanes))
                {
                    changed = true;
                    continue;
                }

                var triangle = new List<ClipVertex>(3)
                {
                    new ClipVertex(a, sourceVertices[aIndex], Vector3.right),
                    new ClipVertex(b, sourceVertices[bIndex], Vector3.up),
                    new ClipVertex(c, sourceVertices[cIndex], Vector3.forward)
                };
                var outsidePieces = new List<List<ClipVertex>>(4);
                List<ClipVertex> inside = triangle;
                foreach (ClipPlane plane in workAreaPlanes)
                {
                    List<ClipVertex> outside = ClipPolygon(inside, plane, !plane.keepGreater);
                    if (outside.Count >= 3) outsidePieces.Add(outside);
                    inside = ClipPolygon(inside, plane, plane.keepGreater);
                    if (inside.Count < 3) break;
                }

                if (PolygonArea(inside) <= 1e-8f)
                {
                    AddOriginalTriangle(retained, aIndex, bIndex, cIndex);
                    continue;
                }

                changed = true;
                foreach (List<ClipVertex> polygon in outsidePieces)
                {
                    if (PolygonArea(polygon) <= 1e-8f) continue;
                    AddPolygon(polygon, retained, sourceVertices.Length, addedVertices, addedNormals, addedTangents, addedColors,
                        uvChannels, sourceNormals, sourceTangents, sourceColors,
                        new[] { aIndex, bIndex, cIndex });
                }
            }
        }
        if (!changed) return null;

        Mesh result = Instantiate(source);
        result.name = source.name + " (work area clipped)";
        result.hideFlags = HideFlags.DontSave;
        if (source.indexFormat == IndexFormat.UInt16 && sourceVertices.Length + addedVertices.Count > ushort.MaxValue)
            result.indexFormat = IndexFormat.UInt32;
        result.vertices = sourceVertices.Concat(addedVertices).ToArray();
        if (addedNormals != null) result.normals = addedNormals.ToArray();
        if (addedTangents != null) result.tangents = addedTangents.ToArray();
        if (addedColors != null) result.colors = addedColors.ToArray();
        for (int channel = 0; channel < uvChannels.Length; channel++)
            if (uvChannels[channel] != null && uvChannels[channel].Count == sourceVertices.Length + addedVertices.Count)
                SetUvChannel(result, channel, uvDimensions[channel], uvChannels[channel]);

        result.subMeshCount = source.subMeshCount;
        for (int i = 0; i < retainedBySubmesh.Length; i++)
            result.SetIndices(retainedBySubmesh[i], outputTopologies[i], i, false, 0);
        result.RecalculateBounds();
        return result;
    }

    private ClipPlane[] GetWorkAreaPlanes()
    {
        if (workAreaPolygonXZ != null && workAreaPolygonXZ.Length >= 3)
        {
            var planes = new ClipPlane[workAreaPolygonXZ.Length + 2];
            for (int i = 0; i < workAreaPolygonXZ.Length; i++)
            {
                Vector2 a = workAreaPolygonXZ[i], edge = workAreaPolygonXZ[(i + 1) % workAreaPolygonXZ.Length] - a;
                Vector3 inward = new Vector3(-edge.y, 0f, edge.x).normalized;
                planes[i] = new ClipPlane(inward, Vector3.Dot(new Vector3(a.x, 0f, a.y), inward), true);
            }
            planes[planes.Length - 2] = new ClipPlane(Vector3.up, workArea.min.y, true);
            planes[planes.Length - 1] = new ClipPlane(Vector3.up, workArea.max.y, false);
            return planes;
        }
        if (!useOrientedWorkArea)
            return new[]
            {
                new ClipPlane(Vector3.right, workArea.min.x, true),
                new ClipPlane(Vector3.right, workArea.max.x, false),
                new ClipPlane(Vector3.forward, workArea.min.z, true),
                new ClipPlane(Vector3.forward, workArea.max.z, false),
                new ClipPlane(Vector3.up, workArea.min.y, true),
                new ClipPlane(Vector3.up, workArea.max.y, false)
            };

        Quaternion horizontalRotation = ConstructionSiteFootprint.GetHorizontalRotation(workAreaRotation);
        Vector3 right = horizontalRotation * Vector3.right;
        Vector3 forward = horizontalRotation * Vector3.forward;
        float rightCenter = Vector3.Dot(workAreaCenter, right);
        float forwardCenter = Vector3.Dot(workAreaCenter, forward);
        return new[]
        {
            new ClipPlane(right, rightCenter - workAreaSize.x * .5f, true),
            new ClipPlane(right, rightCenter + workAreaSize.x * .5f, false),
            new ClipPlane(forward, forwardCenter - workAreaSize.y * .5f, true),
            new ClipPlane(forward, forwardCenter + workAreaSize.y * .5f, false),
            new ClipPlane(Vector3.up, workArea.min.y, true),
            new ClipPlane(Vector3.up, workArea.max.y, false)
        };
    }

    private static List<ClipVertex> ClipPolygon(List<ClipVertex> polygon, ClipPlane plane, bool keepGreater)
    {
        var output = new List<ClipVertex>(polygon.Count + 2);
        if (polygon.Count == 0) return output;
        ClipVertex previous = polygon[polygon.Count - 1];
        float previousDistance = PlaneDistance(previous.world, plane);
        bool previousInside = IsInside(previousDistance, keepGreater);
        foreach (ClipVertex current in polygon)
        {
            float currentDistance = PlaneDistance(current.world, plane);
            bool currentInside = IsInside(currentDistance, keepGreater);
            if (currentInside != previousInside)
                AddDistinct(output, Intersect(previous, current, previousDistance, currentDistance));
            if (currentInside) AddDistinct(output, current);
            previous = current;
            previousDistance = currentDistance;
            previousInside = currentInside;
        }
        if (output.Count > 1 && SamePoint(output[0].world, output[output.Count - 1].world))
            output.RemoveAt(output.Count - 1);
        return output;
    }

    private static ClipVertex Intersect(ClipVertex a, ClipVertex b, float distanceA, float distanceB)
    {
        float denominator = distanceA - distanceB;
        float t = Mathf.Abs(denominator) < 1e-12f ? 0f : Mathf.Clamp01(distanceA / denominator);
        return new ClipVertex(Vector3.LerpUnclamped(a.world, b.world, t),
            Vector3.LerpUnclamped(a.local, b.local, t),
            Vector3.LerpUnclamped(a.barycentric, b.barycentric, t));
    }

    private static bool IsInside(float distance, bool keepGreater) => keepGreater ? distance >= 0f : distance <= 0f;

    private static float PlaneDistance(Vector3 point, ClipPlane plane) =>
        Vector3.Dot(point, plane.normal) - plane.boundary;

    private static void AddDistinct(List<ClipVertex> vertices, ClipVertex candidate)
    {
        if (vertices.Count == 0 || !SamePoint(vertices[vertices.Count - 1].world, candidate.world))
            vertices.Add(candidate);
    }

    private static bool SamePoint(Vector3 a, Vector3 b) => (a - b).sqrMagnitude <= 1e-12f;

    private void AddPolygon(List<ClipVertex> polygon, List<int> targetTriangles, int originalVertexCount,
        List<Vector3> addedVertices,
        List<Vector3> addedNormals, List<Vector4> addedTangents, List<Color> addedColors,
        List<Vector4>[] uvChannels, Vector3[] sourceNormals, Vector4[] sourceTangents,
        Color[] sourceColors, int[] sourceTriangle)
    {
        int start = originalVertexCount + addedVertices.Count;
        foreach (ClipVertex vertex in polygon)
        {
            if (addedNormals != null)
                addedNormals.Add(Interpolate(sourceNormals, sourceTriangle, vertex.barycentric).normalized);
            if (addedTangents != null)
            {
                Vector4 tangent = Interpolate(sourceTangents, sourceTriangle, vertex.barycentric);
                Vector3 direction = new Vector3(tangent.x, tangent.y, tangent.z).normalized;
                addedTangents.Add(new Vector4(direction.x, direction.y, direction.z, tangent.w < 0f ? -1f : 1f));
            }
            if (addedColors != null) addedColors.Add(Interpolate(sourceColors, sourceTriangle, vertex.barycentric));
            for (int channel = 0; channel < uvChannels.Length; channel++)
            {
                List<Vector4> uv = uvChannels[channel];
                if (uv == null || uv.Count != originalVertexCount + addedVertices.Count) continue;
                uv.Add(Interpolate(uv, sourceTriangle, vertex.barycentric));
            }
            addedVertices.Add(vertex.local);
        }
        for (int i = 1; i < polygon.Count - 1; i++)
        {
            Vector3 a = polygon[0].world, b = polygon[i].world, c = polygon[i + 1].world;
            if (Vector3.Cross(b - a, c - a).sqrMagnitude <= 1e-12f) continue;
            targetTriangles.Add(start);
            targetTriangles.Add(start + i);
            targetTriangles.Add(start + i + 1);
        }
    }

    private static Vector3 Interpolate(Vector3[] values, int[] triangle, Vector3 barycentric) =>
        values[triangle[0]] * barycentric.x + values[triangle[1]] * barycentric.y + values[triangle[2]] * barycentric.z;

    private static Vector4 Interpolate(Vector4[] values, int[] triangle, Vector3 barycentric) =>
        values[triangle[0]] * barycentric.x + values[triangle[1]] * barycentric.y + values[triangle[2]] * barycentric.z;

    private static Color Interpolate(Color[] values, int[] triangle, Vector3 barycentric) =>
        values[triangle[0]] * barycentric.x + values[triangle[1]] * barycentric.y + values[triangle[2]] * barycentric.z;

    private static Vector4 Interpolate(List<Vector4> values, int[] triangle, Vector3 barycentric) =>
        values[triangle[0]] * barycentric.x + values[triangle[1]] * barycentric.y + values[triangle[2]] * barycentric.z;

    private static float PolygonArea(List<ClipVertex> polygon)
    {
        float area = 0f;
        for (int i = 1; i < polygon.Count - 1; i++)
            area += Vector3.Cross(polygon[i].world - polygon[0].world,
                polygon[i + 1].world - polygon[0].world).magnitude * .5f;
        return area;
    }

    private static void AddOriginalTriangle(List<int> triangles, int a, int b, int c)
    {
        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }

    private static bool HasTriangles(Mesh mesh)
    {
        for (int i = 0; i < mesh.subMeshCount; i++)
            if (mesh.GetTopology(i) == MeshTopology.Triangles && mesh.GetIndexCount(i) >= 3) return true;
        return false;
    }

    private static int[] TriangulateQuads(int[] quadIndices)
    {
        var triangles = new int[quadIndices.Length / 4 * 6];
        int destination = 0;
        for (int i = 0; i + 3 < quadIndices.Length; i += 4)
        {
            triangles[destination++] = quadIndices[i];
            triangles[destination++] = quadIndices[i + 1];
            triangles[destination++] = quadIndices[i + 2];
            triangles[destination++] = quadIndices[i];
            triangles[destination++] = quadIndices[i + 2];
            triangles[destination++] = quadIndices[i + 3];
        }
        return triangles;
    }

    private static Mesh CreateColliderMesh(Mesh renderMesh)
    {
        bool hasNonTriangleSubmesh = false;
        for (int i = 0; i < renderMesh.subMeshCount; i++)
            if (renderMesh.GetTopology(i) != MeshTopology.Triangles) hasNonTriangleSubmesh = true;
        if (!hasNonTriangleSubmesh) return renderMesh;

        var triangleIndices = new List<List<int>>();
        for (int i = 0; i < renderMesh.subMeshCount; i++)
            if (renderMesh.GetTopology(i) == MeshTopology.Triangles && renderMesh.GetIndexCount(i) >= 3)
                triangleIndices.Add(new List<int>(renderMesh.GetTriangles(i)));
        if (triangleIndices.Count == 0) return null;

        var colliderMesh = new Mesh
        {
            name = renderMesh.name + " (triangle collision only)",
            hideFlags = HideFlags.DontSave,
            indexFormat = renderMesh.indexFormat,
            vertices = renderMesh.vertices
        };
        colliderMesh.subMeshCount = triangleIndices.Count;
        for (int i = 0; i < triangleIndices.Count; i++)
            colliderMesh.SetTriangles(triangleIndices[i], i, false, 0);
        colliderMesh.RecalculateBounds();
        return colliderMesh;
    }

    private static void DestroyReplacementMeshes(Replacement entry)
    {
        if (entry.colliderMesh != null && entry.colliderMesh != entry.clipped)
        {
            if (Application.isPlaying) Destroy(entry.colliderMesh); else DestroyImmediate(entry.colliderMesh);
        }
        if (entry.clipped != null)
        {
            if (Application.isPlaying) Destroy(entry.clipped); else DestroyImmediate(entry.clipped);
        }
    }

    private static void SetUvChannel(Mesh mesh, int channel, int dimension, List<Vector4> values)
    {
        switch (dimension)
        {
            case 2:
                var uv2 = new List<Vector2>(values.Count);
                foreach (Vector4 uv in values) uv2.Add(new Vector2(uv.x, uv.y));
                mesh.SetUVs(channel, uv2);
                break;
            case 3:
                var uv3 = new List<Vector3>(values.Count);
                foreach (Vector4 uv in values) uv3.Add(new Vector3(uv.x, uv.y, uv.z));
                mesh.SetUVs(channel, uv3);
                break;
            default:
                mesh.SetUVs(channel, values);
                break;
        }
    }

    private static bool IsTriangleInsideWorkArea(Vector3 a, Vector3 b, Vector3 c, ClipPlane[] planes) =>
        IsInsideWorkArea(a, planes) && IsInsideWorkArea(b, planes) && IsInsideWorkArea(c, planes);

    private static bool IsInsideWorkArea(Vector3 point, ClipPlane[] planes)
    {
        foreach (ClipPlane plane in planes)
            if (!IsInside(PlaneDistance(point, plane), plane.keepGreater)) return false;
        return true;
    }

    private bool OverlapsWorkArea(Vector3 min, Vector3 max) =>
        min.x <= workArea.max.x && max.x >= workArea.min.x &&
        min.y <= workArea.max.y && max.y >= workArea.min.y &&
        min.z <= workArea.max.z && max.z >= workArea.min.z;

    private void OnDisable()
    {
        if (tileset != null) tileset.OnTileGameObjectCreated -= ClipTile;
        foreach (var replacement in replacements)
        {
            if (replacement.filter != null && replacement.filter.sharedMesh == replacement.clipped)
                replacement.filter.sharedMesh = replacement.source;
            foreach (var collider in replacement.colliders)
                if (collider != null && (collider.sharedMesh == replacement.colliderMesh ||
                    (replacement.colliderMeshCleared && collider.sharedMesh == null))) collider.sharedMesh = replacement.source;
            DestroyReplacementMeshes(replacement);
        }
        replacements.Clear();
    }
}

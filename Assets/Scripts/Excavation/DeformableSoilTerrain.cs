using System;
using UnityEngine;

/// <summary>在独立高度图副本中切削；体积统一为网格四角均值积分的立方米。</summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Terrain), typeof(TerrainCollider))]
public sealed class DeformableSoilTerrain : MonoBehaviour
{
    [Min(0f)] public float floorHeight = .05f;
    [Min(.01f)] public float maximumCutDepth = .8f;
    [Min(.001f)] public float minimumCutDepth = .015f;
    [HideInInspector] public bool deferAutomaticInitialization;
    public Terrain Terrain { get; private set; }
    /// <summary>Persistent source TerrainData; the runtime excavation copy is kept separate.</summary>
    public TerrainData SourceTerrainData => source;
    public TerrainCollider SoilCollider => GetComponent<TerrainCollider>();
    public float FloorWorldY => transform.position.y + floorHeight;
    private TerrainData source;
    private TerrainData runtime;
    private float[,] initial;
    private float[,] groundFloor;

    private void Start()
    {
        if (Application.isPlaying && !deferAutomaticInitialization) Initialize(GetComponent<Terrain>());
    }

    /// <summary>克隆指定地形数据并绑定碰撞体；重复初始化同一地形无副作用。</summary>
    /// <param name="terrain">本物体的无旋转、单位缩放Terrain；源数据永不改写。</param>
    public void Initialize(Terrain terrain)
    {
        InitializeCore(terrain, false, true);
    }

    /// <summary>Rebuild the baked height field into a separate runtime TerrainData without cloning its native splat subassets.</summary>
    /// <param name="terrain">This object's Terrain bearing the persistent baked source.</param>
    public void InitializeBaked(Terrain terrain)
    {
        InitializeCore(terrain, true, true);
    }

    /// <summary>Rebuild a site TerrainData from its first soil layer without reading imported splat data.</summary>
    /// <param name="terrain">This object's source Terrain.</param>
    public void InitializeForConstructionSite(Terrain terrain)
    {
        InitializeCore(terrain, true, false);
    }

    private void InitializeCore(Terrain terrain, bool rebuildBakedData, bool preserveAllLayers)
    {
        if (runtime != null) return;
        if (terrain == null || terrain.gameObject != gameObject || terrain.terrainData == null)
            throw new ArgumentException("必须指定当前物体上具有数据的Terrain。", nameof(terrain));
        if (Quaternion.Angle(transform.rotation, Quaternion.identity) > .001f ||
            (transform.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
            throw new InvalidOperationException("可挖Terrain必须保持世界旋转为零且缩放为1。");
        Terrain = terrain;
        source = terrain.terrainData;
        if (rebuildBakedData)
        {
            // The editor-authored TerrainData contains TerrainLayer subassets. Unity 6 can
            // crash natively when Instantiate clones that entire splat object island in batch mode.
            // Rebuild only the height, holes and appearance data that this cuttable site uses.
            runtime = new TerrainData
            {
                heightmapResolution = source.heightmapResolution,
                size = source.size,
                alphamapResolution = source.alphamapResolution
            };
            runtime.SetHeights(0, 0, source.GetHeights(0, 0,
                source.heightmapResolution, source.heightmapResolution));
            int holes = source.holesResolution;
            if (holes != runtime.holesResolution)
                throw new InvalidOperationException("烘焙地形的孔洞分辨率与运行副本不一致。");
            runtime.SetHoles(0, 0, source.GetHoles(0, 0, holes, holes));
            TerrainLayer[] sourceLayers = source.terrainLayers ?? Array.Empty<TerrainLayer>();
            runtime.terrainLayers = preserveAllLayers || sourceLayers.Length <= 1
                ? sourceLayers
                : new[] { sourceLayers[0] };
            if (runtime.terrainLayers.Length == 1)
            {
                // Headless map baking retains the single soil layer; Unity may serialize
                // no splat texture for that trivial case. Assign explicit full coverage.
                int resolution = runtime.alphamapResolution;
                var weights = new float[resolution, resolution, 1];
                for (int z = 0; z < resolution; z++)
                    for (int x = 0; x < resolution; x++) weights[z, x, 0] = 1f;
                runtime.SetAlphamaps(0, 0, weights);
            }
            else if (runtime.terrainLayers.Length > 1 && preserveAllLayers)
                runtime.SetAlphamaps(0, 0, source.GetAlphamaps(0, 0,
                    source.alphamapResolution, source.alphamapResolution));
        }
        else runtime = Instantiate(source);
        runtime.name = source.name + " (runtime excavation copy)";
        initial = runtime.GetHeights(0, 0, runtime.heightmapResolution, runtime.heightmapResolution);
        terrain.terrainData = runtime;
        SoilCollider.terrainData = runtime;
    }

    /// <summary>将世界空间刃口线段扫掠范围内的地形降低，返回实际读回的移除体积。</summary>
    /// <param name="from">上次刃口世界位置。</param>
    /// <param name="to">本次刃口世界位置；高程沿线段插值。</param>
    /// <param name="radius">水平圆盘近似刃口半径，米。</param>
    /// <param name="availableVolumeM3">斗内剩余容量；不足的土保留在地形中。</param>
    /// <returns>实际移除的m³，容量不足或无交集时可为0。</returns>
    public double CutSweep(Vector3 from, Vector3 to, float radius, float availableVolumeM3)
    {
        return CutSweepInternal(from, to, radius, availableVolumeM3, Vector3.zero, float.PositiveInfinity);
    }

    /// <summary>Cut a sweep clipped to the bucket's lateral mouth bounds; return actual removed cubic metres.</summary>
    /// <param name="from">Previous cutting-edge position in world metres.</param>
    /// <param name="to">Current cutting-edge position in world metres.</param>
    /// <param name="radius">Horizontal sweep radius in metres.</param>
    /// <param name="availableVolumeM3">Remaining bucket capacity in cubic metres.</param>
    /// <param name="widthAxis">World direction across the bucket mouth.</param>
    /// <param name="halfWidth">Inner mouth half-width in metres.</param>
    /// <returns>Actual removed volume; invalid mouth geometry returns zero.</returns>
    public double CutBucketSweep(Vector3 from, Vector3 to, float radius, float availableVolumeM3,
        Vector3 widthAxis, float halfWidth)
    {
        if (!Finite(widthAxis) || widthAxis.sqrMagnitude < 1e-8f ||
            !float.IsFinite(halfWidth) || halfWidth <= 0) return 0;
        return CutSweepInternal(from, to, radius, availableVolumeM3, widthAxis.normalized, halfWidth);
    }

    /// <summary>Cut the Terrain under the projected faces of a blade box and its endpoint-to-endpoint sweep.</summary>
    /// <param name="previousCorners">Previous world-space box corners; bits 0, 1 and 2 select local X, Y and Z sides.</param>
    /// <param name="currentCorners">Current world-space box corners in the same bit-indexed order.</param>
    /// <param name="availableVolumeM3">Maximum removable volume in cubic metres.</param>
    /// <returns>Actual volume removed according to authoritative Terrain read-back, or zero for invalid/no-op input.</returns>
    /// <remarks>The connecting edge quads linearly approximate rotation between the two endpoint boxes.</remarks>
    public double CutBladeSweep(Vector3[] previousCorners, Vector3[] currentCorners, float availableVolumeM3)
    {
        if (!ValidCorners(previousCorners) || !ValidCorners(currentCorners) ||
            !float.IsFinite(availableVolumeM3) || availableVolumeM3 <= 0) return 0;
        Initialize(GetComponent<Terrain>());
        Vector3 origin = transform.position;
        var previous = new Vector3[8];
        var current = new Vector3[8];
        float minimumX = float.PositiveInfinity, maximumX = float.NegativeInfinity;
        float minimumZ = float.PositiveInfinity, maximumZ = float.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            previous[i] = previousCorners[i] - origin;
            current[i] = currentCorners[i] - origin;
            minimumX = Mathf.Min(minimumX, previous[i].x, current[i].x);
            maximumX = Mathf.Max(maximumX, previous[i].x, current[i].x);
            minimumZ = Mathf.Min(minimumZ, previous[i].z, current[i].z);
            maximumZ = Mathf.Max(maximumZ, previous[i].z, current[i].z);
        }

        Vector3 size = runtime.size;
        int resolution = runtime.heightmapResolution;
        float dx = size.x / (resolution - 1), dz = size.z / (resolution - 1);
        int x0 = Mathf.Max(1, Mathf.CeilToInt(minimumX / dx));
        int x1 = Mathf.Min(resolution - 2, Mathf.FloorToInt(maximumX / dx));
        int z0 = Mathf.Max(1, Mathf.CeilToInt(minimumZ / dz));
        int z1 = Mathf.Min(resolution - 2, Mathf.FloorToInt(maximumZ / dz));
        if (x1 < x0 || z1 < z0) return 0;

        var triangles = new Vector3[48, 3];
        int triangleCount = 0;
        AddBoxTriangles(previous, triangles, ref triangleCount);
        AddBoxTriangles(current, triangles, ref triangleCount);
        AddSweepTriangles(previous, current, triangles, ref triangleCount);
        int width = x1 - x0 + 1, depth = z1 - z0 + 1;
        float[,] before = runtime.GetHeights(x0, z0, width, depth);
        float[,] after = (float[,])before.Clone();
        double remaining = availableVolumeM3;
        double vertexVolume = (double)dx * dz * size.y;
        const float heightQuantum = 1f / 32766f;
        bool changed = false;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                float targetY = float.PositiveInfinity;
                Vector2 point = new Vector2(x * dx, z * dz);
                for (int triangle = 0; triangle < triangleCount; triangle++)
                    if (ProjectedHeight(triangles[triangle, 0], triangles[triangle, 1], triangles[triangle, 2],
                        point, out float height)) targetY = Mathf.Min(targetY, height);
                if (!float.IsFinite(targetY) || VertexTouchesHole(x, z)) continue;
                float old = before[z - z0, x - x0];
                float floor = VertexFloor(x, z);
                float target = Mathf.Max(floor, targetY) / size.y;
                if ((old - target) * size.y < minimumCutDepth) continue;
                float reduction = Mathf.Min(old - target, (float)(remaining / vertexVolume));
                reduction = Mathf.Floor(Mathf.Max(0, reduction - heightQuantum) / heightQuantum) * heightQuantum;
                if (reduction <= 0) continue;
                after[z - z0, x - x0] = old - reduction;
                remaining -= reduction * vertexVolume;
                changed = true;
            }
        if (!changed) return 0;
        runtime.SetHeights(x0, z0, after);
        float[,] actual = runtime.GetHeights(x0, z0, width, depth);
        double removed = 0;
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++) removed += (before[z, x] - actual[z, x]) * vertexVolume;
        if (removed < 0 || removed > availableVolumeM3)
        {
            runtime.SetHeights(x0, z0, before);
            return 0;
        }
        return removed;
    }

    private double CutSweepInternal(Vector3 from, Vector3 to, float radius, float availableVolumeM3,
        Vector3 widthAxis, float halfWidth)
    {
        Initialize(GetComponent<Terrain>());
        if (!Finite(from) || !Finite(to) || !float.IsFinite(radius) ||
            !float.IsFinite(availableVolumeM3) || radius <= 0 || availableVolumeM3 <= 0) return 0;
        Vector3 a = from - transform.position;
        Vector3 b = to - transform.position;
        Vector3 size = runtime.size;
        int n = runtime.heightmapResolution;
        float dx = size.x / (n - 1), dz = size.z / (n - 1);
        // 最外圈保持不变，避免切开土区边缘；只读取/提交扫掠覆盖的局部矩形。
        int x0 = Mathf.Max(1, Mathf.CeilToInt((Mathf.Min(a.x, b.x) - radius) / dx));
        int x1 = Mathf.Min(n - 2, Mathf.FloorToInt((Mathf.Max(a.x, b.x) + radius) / dx));
        int z0 = Mathf.Max(1, Mathf.CeilToInt((Mathf.Min(a.z, b.z) - radius) / dz));
        int z1 = Mathf.Min(n - 2, Mathf.FloorToInt((Mathf.Max(a.z, b.z) + radius) / dz));
        if (x1 < x0 || z1 < z0) return 0;
        float[,] before = runtime.GetHeights(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
        float[,] after = (float[,])before.Clone();
        Vector2 start = new Vector2(a.x, a.z), delta = new Vector2(b.x - a.x, b.z - a.z);
        double remaining = availableVolumeM3;
        double vertexVolume = dx * dz * size.y;
        // Unity高度图有量化；预留一个高度单位，最终仍以GetHeights读回为准。
        const float heightQuantum = 1f / 32766f;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                Vector2 point = new Vector2(x * dx, z * dz);
                float t = delta.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector2.Dot(point - start, delta) / delta.sqrMagnitude) : 0;
                if ((point - start - delta * t).sqrMagnitude > radius * radius) continue;
                Vector3 edge = Vector3.Lerp(a, b, t);
                Vector3 lateralOffset = new Vector3(point.x, edge.y, point.y) - edge;
                if (Mathf.Abs(Vector3.Dot(lateralOffset, widthAxis)) > halfWidth) continue;
                if (VertexTouchesHole(x, z)) continue;
                float old = before[z - z0, x - x0];
                float floor = VertexFloor(x, z);
                float target = Mathf.Max(floor, Mathf.Lerp(a.y, b.y, t)) / size.y;
                if ((old - target) * size.y < minimumCutDepth) continue;
                float reduction = Mathf.Min(old - target, (float)(remaining / vertexVolume));
                reduction = Mathf.Floor(Mathf.Max(0, reduction - heightQuantum) / heightQuantum) * heightQuantum;
                if (reduction <= 0) continue;
                after[z - z0, x - x0] = old - reduction;
                remaining -= reduction * vertexVolume;
            }
        }
        runtime.SetHeights(x0, z0, after);
        float[,] actual = runtime.GetHeights(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
        double removed = 0;
        for (int z = 0; z < actual.GetLength(0); z++)
            for (int x = 0; x < actual.GetLength(1); x++) removed += (before[z, x] - actual[z, x]) * vertexVolume;
        if (removed < 0 || removed > availableVolumeM3)
        {
            runtime.SetHeights(x0, z0, before);
            return 0;
        }
        return removed;
    }

    /// <summary>Raise a localized radial mound on the runtime terrain, bounded by its maximum height.</summary>
    /// <param name="worldPoint">World-space mound center; its vertical position does not change deposited volume.</param>
    /// <param name="radius">Horizontal mound radius in metres; the terrain's outer rim remains unchanged.</param>
    /// <param name="volumeM3">Maximum soil volume to add in cubic metres.</param>
    /// <returns>Actual read-back volume added, never above the request; invalid or sub-quantum requests return zero.</returns>
    public double Deposit(Vector3 worldPoint, float radius, double volumeM3)
        => Deposit(worldPoint, radius, volumeM3, 0);

    // Flight deposits must not spread their radial footprint back up an unstable face.
    internal double Deposit(Vector3 worldPoint, float radius, double volumeM3, float minimumNormalY)
    {
        if (!Finite(worldPoint) || !float.IsFinite(radius) || radius <= 0 ||
            double.IsNaN(volumeM3) || double.IsInfinity(volumeM3) || volumeM3 <= 0) return 0;
        Initialize(GetComponent<Terrain>());
        Vector3 size = runtime.size;
        int n = runtime.heightmapResolution;
        double dx = (double)size.x / (n - 1), dz = (double)size.z / (n - 1);
        double centerX = (double)worldPoint.x - transform.position.x;
        double centerZ = (double)worldPoint.z - transform.position.z;
        // Clamp in double precision before converting indices, including extreme finite inputs.
        int x0 = (int)Math.Max(1, Math.Min(n - 1, Math.Ceiling((centerX - radius) / dx)));
        int x1 = (int)Math.Max(0, Math.Min(n - 2, Math.Floor((centerX + radius) / dx)));
        int z0 = (int)Math.Max(1, Math.Min(n - 1, Math.Ceiling((centerZ - radius) / dz)));
        int z1 = (int)Math.Max(0, Math.Min(n - 2, Math.Floor((centerZ + radius) / dz)));
        if (x1 < x0 || z1 < z0) return 0;
        int width = x1 - x0 + 1, depth = z1 - z0 + 1;
        float[,] before = runtime.GetHeights(x0, z0, width, depth);
        var weights = new double[depth, width];
        double vertexVolume = dx * dz * size.y;
        double radiusSquared = (double)radius * radius;
        double capacity = 0, maximumAmplitude = 0;
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                double ox = (x + x0) * dx - centerX, oz = (z + z0) * dz - centerZ;
                double weight = Math.Max(0, 1 - (ox * ox + oz * oz) / radiusSquared);
                if (weight <= 0 || VertexTouchesHole(x + x0, z + z0)) continue;
                if (minimumNormalY > 0 && runtime.GetInterpolatedNormal(
                    (float)(x + x0) / (n - 1), (float)(z + z0) / (n - 1)).y < minimumNormalY) continue;
                weights[z, x] = weight;
                double room = 1.0 - before[z, x];
                capacity += room * vertexVolume;
                maximumAmplitude = Math.Max(maximumAmplitude, room / weight);
            }
        if (capacity <= 0) return 0;
        // Solve one shared mound amplitude so the requested volume is radial even when peaks reach the ceiling.
        double low = 0, high = maximumAmplitude;
        bool fillToCeiling = volumeM3 >= capacity;
        if (!fillToCeiling)
            for (int iteration = 0; iteration < 60; iteration++)
            {
                double amplitude = (low + high) * .5;
                double candidateVolume = 0;
                for (int z = 0; z < depth; z++)
                    for (int x = 0; x < width; x++)
                        candidateVolume += Math.Min(1.0 - before[z, x], amplitude * weights[z, x]) * vertexVolume;
                if (candidateVolume <= volumeM3) low = amplitude;
                else high = amplitude;
            }
        float[,] after = (float[,])before.Clone();
        const double heightQuantum = 1.0 / 32766.0;
        bool changed = false;
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                if (weights[z, x] <= 0) continue;
                double rise = Math.Min(1.0 - before[z, x], low * weights[z, x]);
                // Reserve one quantization unit; actual read-back remains the authoritative transfer amount.
                rise = Math.Floor(Math.Max(0, rise - heightQuantum) / heightQuantum) * heightQuantum;
                after[z, x] = fillToCeiling ? 1f : (float)Math.Min(1, before[z, x] + rise);
                changed |= after[z, x] > before[z, x];
            }
        if (!changed) return 0;
        runtime.SetHeights(x0, z0, after);
        float[,] actual = runtime.GetHeights(x0, z0, width, depth);
        double added = 0;
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++) added += (actual[z, x] - before[z, x]) * vertexVolume;
        if (added < 0 || added > volumeM3)
        {
            runtime.SetHeights(x0, z0, before);
            return 0;
        }
        return added;
    }

    /// <summary>从初始与当前完整高度图独立积分已移除m³，供守恒检查。</summary>
    /// <returns>相对原地形净减少的体积，m³；堆土超过挖土时为负。</returns>
    public double RecalculateRemovedVolumeM3()
    {
        if (runtime == null) return 0;
        int n = runtime.heightmapResolution;
        float[,] heights = runtime.GetHeights(0, 0, n, n);
        double cellScale = (double)runtime.size.x * runtime.size.z * runtime.size.y / ((n - 1) * (n - 1));
        double sum = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                sum += (initial[z, x] - heights[z, x]) * cellScale *
                    (x == 0 || x == n - 1 ? .5 : 1) * (z == 0 || z == n - 1 ? .5 : 1);
        return sum;
    }

    /// <summary>Check every height sample against the prepared surface before authoring a persistent site.</summary>
    /// <returns>True only when a prepared runtime heightmap exists and no vertex differs from its baseline.</returns>
    public bool IsPreparedSurfaceUnchanged()
    {
        if (runtime == null || initial == null) return false;
        int n = runtime.heightmapResolution;
        if (initial.GetLength(0) != n || initial.GetLength(1) != n) return false;
        float[,] heights = runtime.GetHeights(0, 0, n, n);
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                if (heights[z, x] != initial[z, x]) return false;
        return true;
    }

    /// <summary>Set the prepared surface and reset baseline before excavation; source assets stay unchanged.</summary>
    /// <param name="heights">Complete normalized heightmap matching the runtime resolution.</param>
    public void SetPreparedSurface(float[,] heights)
    {
        Initialize(GetComponent<Terrain>());
        int n = runtime.heightmapResolution;
        if (heights == null || heights.GetLength(0) != n || heights.GetLength(1) != n)
            throw new ArgumentException("Prepared surface must match the terrain resolution.", nameof(heights));
        if (!IsPreparedSurfaceUnchanged())
            throw new InvalidOperationException("Cannot prepare soil after excavation or deposition.");
        foreach (float height in heights)
            if (!float.IsFinite(height) || height < 0 || height > 1)
                throw new ArgumentException("Prepared heights must be finite and normalized.", nameof(heights));
        runtime.SetHeights(0, 0, heights);
        initial = runtime.GetHeights(0, 0, n, n);
        groundFloor = null;
    }

    /// <summary>Prepare a runtime mound and its immutable map-ground cutting floor; source assets stay unchanged.</summary>
    /// <param name="normalizedHeights">Complete finite normalized mound heightmap matching the Terrain resolution.</param>
    /// <param name="normalizedGround">Complete finite normalized ground heightmap, never above the mound.</param>
    /// <exception cref="ArgumentException">A heightmap is malformed or the mound lies below the ground.</exception>
    /// <exception cref="InvalidOperationException">Excavation or deposition has already changed the prepared surface.</exception>
    public void PrepareMoundSurface(float[,] normalizedHeights, float[,] normalizedGround)
    {
        Initialize(GetComponent<Terrain>());
        int n = runtime.heightmapResolution;
        ValidateHeightmap(normalizedHeights, n, nameof(normalizedHeights));
        ValidateHeightmap(normalizedGround, n, nameof(normalizedGround));
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                if (normalizedHeights[z, x] < normalizedGround[z, x])
                    throw new ArgumentException("Mound heights cannot be below map ground.", nameof(normalizedHeights));
        SetPreparedSurface(normalizedHeights);
        groundFloor = (float[,])normalizedGround.Clone();
    }

    /// <summary>Return an independent copy of the prepared map-ground height field for editor baking.</summary>
    /// <returns>Normalized ground elevations, or null before the site has prepared a mound.</returns>
    public float[,] GetPreparedGroundCopy() => groundFloor == null ? null : (float[,])groundFloor.Clone();

    /// <summary>Sample the mound's effective cutting floor using bilinear interpolation in world coordinates.</summary>
    /// <param name="worldPoint">Finite query position; horizontal coordinates outside the Terrain clamp to its edge.</param>
    /// <returns>World floor elevation including map ground and maximum cut depth; legacy soil uses FloorWorldY.</returns>
    /// <exception cref="ArgumentException">The query position contains a nonfinite coordinate.</exception>
    public float GetFloorWorldY(Vector3 worldPoint)
    {
        if (!Finite(worldPoint)) throw new ArgumentException("Floor query must be finite.", nameof(worldPoint));
        if (groundFloor == null) return FloorWorldY;
        Vector3 local = worldPoint - transform.position;
        int last = runtime.heightmapResolution - 1;
        float x = Mathf.Clamp01(local.x / runtime.size.x) * last;
        float z = Mathf.Clamp01(local.z / runtime.size.z) * last;
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        int x1 = Mathf.Min(last, x0 + 1), z1 = Mathf.Min(last, z0 + 1);
        return transform.position.y + Mathf.Lerp(
            Mathf.Lerp(VertexFloor(x0, z0), VertexFloor(x1, z0), x - x0),
            Mathf.Lerp(VertexFloor(x0, z1), VertexFloor(x1, z1), x - x0), z - z0);
    }

    /// <summary>Check whether the current Terrain collider supplies a solid surface at a horizontal world position.</summary>
    /// <param name="worldPoint">World position; height is ignored, but all coordinates must be finite.</param>
    /// <returns>False for inactive or disabled collision, missing data, out-of-bounds positions and holes.</returns>
    /// <remarks>This query does not initialize the runtime Terrain or modify its source data.</remarks>
    public bool HasSurfaceAt(Vector3 worldPoint)
    {
        if (!Finite(worldPoint) || !gameObject.activeInHierarchy) return false;
        TerrainCollider collider = SoilCollider;
        if (collider == null || !collider.enabled || collider.terrainData == null) return false;
        TerrainData data = collider.terrainData;
        Vector3 local = worldPoint - transform.position;
        Vector3 size = data.size;
        if (local.x < 0 || local.z < 0 || local.x > size.x || local.z > size.z ||
            size.x <= 0 || size.z <= 0) return false;
        int resolution = data.holesResolution;
        int x = Mathf.Min(resolution - 1, Mathf.FloorToInt(local.x / size.x * resolution));
        int z = Mathf.Min(resolution - 1, Mathf.FloorToInt(local.z / size.z * resolution));
        return !data.IsHole(x, z);
    }

    private bool VertexTouchesHole(int x, int z)
    {
        // Only interior vertices are edited. Each contributes volume to four adjacent cells;
        // keeping hole boundaries fixed preserves the full-cell integration used for transfers.
        return runtime.IsHole(x - 1, z - 1) || runtime.IsHole(x, z - 1) ||
            runtime.IsHole(x - 1, z) || runtime.IsHole(x, z);
    }

    private float VertexFloor(int x, int z)
    {
        float floor = Mathf.Max(floorHeight, initial[z, x] * runtime.size.y - maximumCutDepth);
        return groundFloor == null ? floor : Mathf.Max(floor, groundFloor[z, x] * runtime.size.y);
    }

    private static void ValidateHeightmap(float[,] heights, int resolution, string parameterName)
    {
        if (heights == null || heights.GetLength(0) != resolution || heights.GetLength(1) != resolution)
            throw new ArgumentException("Heightmap must match the terrain resolution.", parameterName);
        foreach (float height in heights)
            if (!float.IsFinite(height) || height < 0 || height > 1)
                throw new ArgumentException("Heightmap must contain finite normalized heights.", parameterName);
    }

    /// <summary>恢复运行副本的初始高度；有斗量时应使用BucketSoilCutter.ResetExcavation同步复位。</summary>
    public void ResetSoil()
    {
        if (runtime != null) runtime.SetHeights(0, 0, initial);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static bool ValidCorners(Vector3[] corners)
    {
        if (corners == null || corners.Length != 8) return false;
        for (int i = 0; i < corners.Length; i++)
            if (!Finite(corners[i])) return false;
        return true;
    }

    private static void AddBoxTriangles(Vector3[] corners, Vector3[,] triangles, ref int count)
    {
        AddQuad(corners[0], corners[2], corners[6], corners[4], triangles, ref count);
        AddQuad(corners[1], corners[5], corners[7], corners[3], triangles, ref count);
        AddQuad(corners[0], corners[4], corners[5], corners[1], triangles, ref count);
        AddQuad(corners[2], corners[3], corners[7], corners[6], triangles, ref count);
        AddQuad(corners[0], corners[1], corners[3], corners[2], triangles, ref count);
        AddQuad(corners[4], corners[6], corners[7], corners[5], triangles, ref count);
    }

    private static void AddSweepTriangles(Vector3[] previous, Vector3[] current, Vector3[,] triangles, ref int count)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            int bit = 1 << axis;
            for (int corner = 0; corner < 8; corner++)
            {
                if ((corner & bit) != 0) continue;
                int other = corner | bit;
                AddQuad(previous[corner], previous[other], current[other], current[corner], triangles, ref count);
            }
        }
    }

    private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3[,] triangles, ref int count)
    {
        triangles[count, 0] = a; triangles[count, 1] = b; triangles[count++, 2] = c;
        triangles[count, 0] = a; triangles[count, 1] = c; triangles[count++, 2] = d;
    }

    private static bool ProjectedHeight(Vector3 a, Vector3 b, Vector3 c, Vector2 point, out float height)
    {
        Vector2 ab = new Vector2(b.x - a.x, b.z - a.z);
        Vector2 ac = new Vector2(c.x - a.x, c.z - a.z);
        Vector2 ap = point - new Vector2(a.x, a.z);
        float determinant = ab.x * ac.y - ab.y * ac.x;
        if (Mathf.Abs(determinant) < 1e-8f)
        {
            height = 0;
            return false;
        }
        float u = (ap.x * ac.y - ap.y * ac.x) / determinant;
        float v = (ab.x * ap.y - ab.y * ap.x) / determinant;
        const float tolerance = 1e-5f;
        if (u < -tolerance || v < -tolerance || u + v > 1 + tolerance)
        {
            height = 0;
            return false;
        }
        height = a.y + u * (b.y - a.y) + v * (c.y - a.y);
        return true;
    }

    private void OnDestroy()
    {
        if (runtime == null) return;
        if (Terrain != null) Terrain.terrainData = source;
        if (SoilCollider != null) SoilCollider.terrainData = source;
        if (Application.isPlaying) Destroy(runtime); else DestroyImmediate(runtime);
    }
}

using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Zx200.Editor
{
    /// <summary>创建独立浅土层演示，保留用户场景与原始ZX200 prefab。</summary>
    public static class Zx200SoilDemoSetup
    {
        private const string PrefabPath = "Assets/Robotics/zx200_description/Prefabs/zx200.prefab";

        /// <summary>Create a separate cutting/unloading scene with a reachable receiving platform.</summary>
        [MenuItem("Tools/ZX200/Soil/Create Independent Unloading Demo Scene")]
        public static void CreateUnloadingDemoScene()
        {
            CreateDemoScene();
            Scene scene = SceneManager.GetActiveScene();
            var robot = Selection.activeGameObject;
            var cutter = robot.GetComponent<Zx200DigController>().bucketJoint.GetComponent<BucketSoilCutter>();
            string directory = System.IO.Path.GetDirectoryName(scene.path).Replace('\\', '/');
            var zone = new GameObject("Soil receiving platform").AddComponent<SoilReceivingZone>();
            zone.transform.position = new Vector3(5, .35f, 5);
            zone.size = new Vector2(3, 3);
            zone.capacityM3 = 2;
            zone.maximumFillHeight = .6f;
            zone.GetComponent<Renderer>().sharedMaterial = cutter.fillVisual.GetComponent<Renderer>().sharedMaterial;
            var platformMaterial = CreateMaterial(directory, "ReceivingPlatform", new Color(.22f, .43f, .46f));
            CreatePlatformPart(zone.transform, "Solid receiving floor", new Vector3(0, -.1f, 0), new Vector3(3.2f, .2f, 3.2f), platformMaterial);
            CreatePlatformPart(zone.transform, "Left wall", new Vector3(-1.55f, .25f, 0), new Vector3(.1f, .5f, 3.2f), platformMaterial);
            CreatePlatformPart(zone.transform, "Right wall", new Vector3(1.55f, .25f, 0), new Vector3(.1f, .5f, 3.2f), platformMaterial);
            CreatePlatformPart(zone.transform, "Back wall", new Vector3(0, .25f, 1.55f), new Vector3(3.2f, .5f, .1f), platformMaterial);
            var unloader = cutter.gameObject.AddComponent<BucketSoilUnloader>();
            unloader.receivers = new[] { zone };
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("Unloading demo saved: " + scene.path);
        }

        private static void CreatePlatformPart(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.GetComponent<Renderer>().sharedMaterial = material;
        }

        /// <summary>新建附加demo场景及独立资源并保存到唯一目录，供菜单或batch执行。</summary>
        [MenuItem("Tools/ZX200/Soil/Create Independent Demo Scene")]
        public static void CreateDemoScene()
        {
            RequireEditMode();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) throw new InvalidOperationException("找不到ZX200 prefab：" + PrefabPath);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            robot.name = "ZX200_Soil_Demo";
            // 仅独立演示固定底盘，原prefab和用户移动模式不改。
            var keyboard = robot.GetComponent<Zx200KeyboardController>();
            if (keyboard != null) keyboard.fixedBase = true;
            var dig = robot.GetComponent<Zx200DigController>();
            if (dig == null || dig.bucketJoint == null) throw new InvalidOperationException("ZX200缺少bucketJoint引用。");
            dig.workReadyPose = new Zx200JointPose(0f, -26f, 68f, 25f, 1f);
            Zx200PrefabSetup.ApplyEditorWorkReadyPose(dig);
            keyboard.boomSpeed = 10f;
            keyboard.armSpeed = 10f;
            keyboard.bucketSpeed = 15f;
            keyboard.swingSpeed = 15f;
            string directory = NewAssetDirectory();
            Vector3 center = robot.transform.position + robot.transform.forward * 7f;
            center.y = robot.transform.position.y + .6f;
            DeformableSoilTerrain soil = CreateSoilAt(center, new Vector2(6, 6), .6f, directory);
            AttachCutter(dig.bucketJoint.transform, soil, directory);
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Vehicle ground (solid)";
            ground.transform.position = new Vector3(0, -.15f, 0);
            ground.transform.localScale = new Vector3(40, .3f, 40);
            ground.GetComponent<Renderer>().sharedMaterial = CreateMaterial(directory, "Ground", new Color(.29f, .32f, .29f));
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 2;
            sun.transform.rotation = Quaternion.Euler(45, -35, 0);
            var camera = new GameObject("Demo Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = center + new Vector3(10, 8, -10);
            camera.transform.LookAt(Vector3.Lerp(robot.transform.position + Vector3.up * 2, center, .5f));
            camera.farClipPlane = 100;
            camera.gameObject.AddComponent<AudioListener>();
            var cameraController = robot.GetComponent<Zx200CameraController>();
            if (cameraController != null)
            {
                cameraController.targetCamera = camera;
                cameraController.orbitPivotLocalPosition = new Vector3(0f, 1.4f, 3f);
                cameraController.initialThirdPersonYaw = -35f;
                cameraController.initialDistance = 13f;
                cameraController.maxDistance = 18f;
            }
            PrefabUtility.RecordPrefabInstancePropertyModifications(keyboard);
            PrefabUtility.RecordPrefabInstancePropertyModifications(dig);
            if (cameraController != null) PrefabUtility.RecordPrefabInstancePropertyModifications(cameraController);
            EditorSceneManager.SaveScene(scene, directory + "/ZX200SoilDemo.unity");
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = robot;
            Debug.Log("独立切削场景已创建：" + scene.path + "。土区位于设备前方；原场景仍保持打开以保留未保存内容；运行前请单独打开新生成的场景。");
            LogBucketMarkers(robot);
        }

        /// <summary>为选中的ZX200实例在当前刃口前方建立土区；只标记场景dirty，不保存用户场景。</summary>
        [MenuItem("Tools/ZX200/Soil/Add Soil At Selected ZX200 Bucket")]
        public static void AddSoilToSelected()
        {
            RequireEditMode();
            var selected = Selection.activeGameObject;
            var dig = selected != null ? selected.GetComponentInParent<Zx200DigController>() : null;
            if (dig == null || dig.bucketJoint == null) throw new InvalidOperationException("请选中场景中的ZX200根物体或其子物体。");
            if (EditorUtility.IsPersistent(dig) || PrefabStageUtility.GetPrefabStage(dig.gameObject) != null ||
                EditorSceneManager.IsPreviewScene(dig.gameObject.scene))
                throw new InvalidOperationException("请选中普通场景中的ZX200实例，不要在Prefab模式或预览场景创建土区。");
            if (dig.bucketJoint.GetComponent<BucketSoilCutter>() != null)
                throw new InvalidOperationException("该铲斗已有土区，请移动已有土区，不要重复创建。");
            SceneManager.SetActiveScene(dig.gameObject.scene);
            string directory = NewAssetDirectory();
            Vector3 center = dig.bucketJoint.transform.TransformPoint(new Vector3(0, .82f, 1.25f));
            center.y = dig.transform.position.y + .6f;
            var soil = CreateSoilAt(center, new Vector2(6, 6), .6f, directory);
            AttachCutter(dig.bucketJoint.transform, soil, directory);
            EditorSceneManager.MarkSceneDirty(dig.gameObject.scene);
            AssetDatabase.SaveAssets();
            LogBucketMarkers(dig.gameObject);
        }

        /// <summary>按世界坐标创建浅土层及实体底板，供可达性试验选择位置。</summary>
        /// <param name="surfaceCenter">土区表面中心世界坐标，Y为表面高度。</param>
        /// <param name="size">土区水平宽度和长度，米。</param>
        /// <param name="depth">土层厚度，米；底部保留0.05米。</param>
        /// <param name="assetDirectory">已存在的Unity资源目录；资源用唯一名称保存。</param>
        /// <param name="heightmapResolution">Unity高度图分辨率；精细斗齿需要足够密的采样网格。</param>
        /// <returns>新建土区组件，尚未克隆运行数据。</returns>
        public static DeformableSoilTerrain CreateSoilAt(Vector3 surfaceCenter, Vector2 size, float depth, string assetDirectory, int heightmapResolution = 129)
        {
            if (size.x <= 0 || size.y <= 0 || depth <= .05f) throw new ArgumentOutOfRangeException(nameof(depth));
            // Reserve vertical heightmap range for deposited mounds as well as the initial diggable layer.
            var data = new TerrainData { heightmapResolution = heightmapResolution, size = new Vector3(size.x, depth + 2f, size.y) };
            int resolution = data.heightmapResolution;
            var heights = new float[resolution, resolution];
            for (int z = 0; z < resolution; z++)
                for (int x = 0; x < resolution; x++) heights[z, x] = depth / data.size.y;
            data.SetHeights(0, 0, heights);
            // No alpha channel: Terrain/Lit otherwise treats opaque alpha as a fully glossy soil surface.
            var texture = new Texture2D(128, 128, TextureFormat.RGB24, true) { name = "Brown soil", wrapMode = TextureWrapMode.Repeat };
            var colors = new Color[128 * 128];
            for (int z = 0; z < 128; z++)
                for (int x = 0; x < 128; x++)
                {
                    uint grain = unchecked((uint)(x * 374761393 + z * 668265263));
                    grain = (grain ^ (grain >> 13)) * 1274126177u;
                    float noise = (grain & 1023) / 1023f;
                    colors[z * 128 + x] = Color.Lerp(new Color(.22f, .12f, .055f),
                        new Color(.48f, .32f, .17f), noise);
                }
            texture.SetPixels(colors);
            texture.Apply();
            SaveUnique(texture, assetDirectory + "/SoilColor.asset");
            var layer = new TerrainLayer { diffuseTexture = texture, tileSize = new Vector2(.5f, .5f) };
            SaveUnique(layer, assetDirectory + "/Soil.terrainlayer");
            data.terrainLayers = new[] { layer };
            SaveUnique(data, assetDirectory + "/SoilTerrain.asset");
            GameObject ground = Terrain.CreateTerrainGameObject(data);
            ground.name = "Excavatable shallow soil";
            ground.transform.position = surfaceCenter - new Vector3(size.x / 2, depth, size.y / 2);
            var terrainShader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            if (terrainShader != null)
            {
                var material = new Material(terrainShader);
                SaveUnique(material, assetDirectory + "/Terrain.mat");
                ground.GetComponent<Terrain>().materialTemplate = material;
            }
            var soil = ground.AddComponent<DeformableSoilTerrain>();
            soil.maximumCutDepth = depth - .05f;
            GameObject plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = "Soil bottom plate (always solid)";
            plate.transform.position = new Vector3(surfaceCenter.x, surfaceCenter.y - depth - .05f, surfaceCenter.z);
            plate.transform.localScale = new Vector3(size.x, .2f, size.y);
            plate.transform.SetParent(soil.transform, true);
            plate.GetComponent<Renderer>().sharedMaterial = CreateMaterial(assetDirectory, "BottomPlate", new Color(.2f, .2f, .2f));
            return soil;
        }

        /// <summary>为真实bucket建立采样器和纯显示土体；不修改机械碰撞壳和关节参数。</summary>
        /// <param name="bucket">真实bucket_link Transform。</param>
        /// <param name="soil">该铲斗唯一土区。</param>
        /// <param name="assetDirectory">填充材质存放目录。</param>
        /// <returns>配置后的切削器。</returns>
        public static BucketSoilCutter AttachCutter(Transform bucket, DeformableSoilTerrain soil, string assetDirectory)
        {
            var cutter = bucket.GetComponent<BucketSoilCutter>();
            if (cutter != null) throw new InvalidOperationException("该铲斗已有土区；请使用已有切削器重新定位土区，避免丢失斗量。");
            cutter = bucket.gameObject.AddComponent<BucketSoilCutter>();
            cutter.soil = soil;
            foreach (Transform child in bucket.GetComponentsInChildren<Transform>())
                if (child.name == "bucket_end_link") { cutter.tipMarker = child; break; }
            GameObject fill = new GameObject("Bucket soil fill (visual only)");
            fill.name = "Bucket soil fill (visual only)";
            fill.transform.SetParent(bucket, false);
            var fillMesh = fill.AddComponent<BucketSoilFill>();
            var fillMaterial = CreateMaterial(assetDirectory, "BucketSoil", Color.white);
            var layers = soil.GetComponent<Terrain>().terrainData.terrainLayers;
            if (layers.Length > 0) fillMaterial.mainTexture = layers[0].diffuseTexture;
            if (fillMaterial.HasProperty("_Smoothness")) fillMaterial.SetFloat("_Smoothness", 0);
            fill.GetComponent<Renderer>().sharedMaterial = fillMaterial;
            cutter.fillVisual = fill.transform;
            cutter.capacityM3 = (float)fillMesh.CapacityM3;
            cutter.innerMouthHalfWidth = fillMesh.innerHalfWidth;
            fill.SetActive(false);
            return cutter;
        }

        /// <summary>打印刃口、斗中心与切削方向世界坐标，供完整设备试切定位。</summary>
        /// <param name="robot">包含Zx200DigController的实例。</param>
        public static void LogBucketMarkers(GameObject robot)
        {
            var dig = robot.GetComponent<Zx200DigController>();
            var cutter = dig.bucketJoint.GetComponent<BucketSoilCutter>();
            Debug.Log($"ZX200 bucket origin={dig.bucketJoint.transform.position:F3}; tip={cutter.TipWorldPosition:F3}; " +
                $"cut direction={cutter.transform.TransformDirection(cutter.cutDirectionLocal).normalized:F3}; " +
                $"cavity={cutter.transform.TransformPoint(new Vector3(0,.65f,.6f)):F3}; soil={cutter.soil.transform.position:F3}");
        }

        private static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请先退出Play模式，再创建或添加演示土区。");
        }

        private static string NewAssetDirectory()
        {
            const string parent = "Assets/Robotics/zx200_description/Demos";
            if (!AssetDatabase.IsValidFolder(parent)) AssetDatabase.CreateFolder("Assets/Robotics/zx200_description", "Demos");
            const string root = parent + "/SoilCutting";
            if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder(parent, "SoilCutting");
            string path = AssetDatabase.GenerateUniqueAssetPath(root + "/Demo");
            AssetDatabase.CreateFolder(root, path.Substring(root.Length + 1));
            return path;
        }

        private static Material CreateMaterial(string directory, string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { color = color };
            SaveUnique(material, directory + "/" + name + ".mat");
            return material;
        }

        private static void SaveUnique(UnityEngine.Object asset, string path) => AssetDatabase.CreateAsset(asset, AssetDatabase.GenerateUniqueAssetPath(path));
    }
}

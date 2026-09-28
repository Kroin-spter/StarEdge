using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Zx200.Editor
{
    /// <summary>Build the parked loading site around the georeferenced machine in the existing main scene.</summary>
    public static class Zx200ConstructionSiteSetup
    {
        private const string ScenePath = "Assets/Scenes/sence_test.unity";
        private const string Directory = "Assets/ConstructionSite";
        private const float TruckSoilClearanceMeters = 2f;
        private const float TruckParkingYawDegrees = 90f;
        public static readonly Vector3 SoilStripCenterLocal = new Vector3(0f, 0f, 6.7f);
        public static readonly Vector3 SoilStripSizeLocal = new Vector3(13f, .8f, 1.8f);

        /// <summary>车辆相对于挖掘机底盘停在低位右侧偏后方，单位为米。</summary>
        public static readonly Vector3 TruckParkingOffsetLocal = new Vector3(7.9f, 0f, -1.2f);

        /// <summary>Create a parked truck with a 3.6×4.5 metre receiving bed; returns its root without saving assets.</summary>
        /// <param name="groundPosition">World position beneath the bed centre at ground level.</param>
        /// <param name="bodyMaterial">Cab and bed material, or null for Unity's default.</param>
        /// <param name="darkMaterial">Chassis and tyre material, or null for default.</param>
        /// <param name="glassMaterial">Window material, or null for default.</param>
        /// <returns>New truck root with solid chassis, floor, four walls, and a matching receiver.</returns>
        public static GameObject CreateTruck(Vector3 groundPosition, Material bodyMaterial, Material darkMaterial, Material glassMaterial)
        {
            var truck = new GameObject("Parked dump truck");
            truck.transform.position = groundPosition;
            Part(truck.transform, "Chassis", new Vector3(0,.65f,.65f), new Vector3(2.6f,.3f,6.3f), darkMaterial);
            Part(truck.transform, "Bed floor", new Vector3(0,.9148f,0), new Vector3(4.24f,.2f,4.84f), bodyMaterial);
            Part(truck.transform, "Left bed wall", new Vector3(-2.06f,1.4668f,0), new Vector3(.12f,.9f,4.84f), bodyMaterial);
            Part(truck.transform, "Right bed wall", new Vector3(2.06f,1.4668f,0), new Vector3(.12f,.9f,4.84f), bodyMaterial);
            Part(truck.transform, "Front bed wall", new Vector3(0,1.4668f,2.36f), new Vector3(4.12f,.9f,.12f), bodyMaterial);
            Part(truck.transform, "Tailgate", new Vector3(0,1.4668f,-2.36f), new Vector3(4.12f,.9f,.12f), bodyMaterial);
            Part(truck.transform, "Cab", new Vector3(0,1.45f,3.18f), new Vector3(2.6f,1.8f,1.85f), bodyMaterial);
            Part(truck.transform, "Windshield", new Vector3(0,1.9f,4.12f), new Vector3(2.3f,.65f,.04f), glassMaterial);
            foreach (int side in new[] { -1, 1 })
            {
                Part(truck.transform, side < 0 ? "Left window" : "Right window", new Vector3(side*1.31f,1.9f,3.32f), new Vector3(.04f,.65f,1.3f), glassMaterial);
                foreach (float axle in new[] { -1.35f, .1f, 3.25f })
                {
                    var wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    wheel.name = "Wheel " + side + " " + axle;
                    wheel.transform.SetParent(truck.transform, false);
                    wheel.transform.localPosition = new Vector3(side*1.35f,.48f,axle);
                    wheel.transform.localRotation = Quaternion.Euler(0,0,90);
                    wheel.transform.localScale = new Vector3(.96f,.2f,.96f);
                    wheel.GetComponent<Renderer>().sharedMaterial = darkMaterial;
                }
            }
            Part(truck.transform, "Front bumper", new Vector3(0,.6f,4.18f), new Vector3(2.8f,.2f,.2f), darkMaterial);
            var receiver = new GameObject("Truck bed soil receiver").AddComponent<SoilReceivingZone>();
            receiver.transform.SetParent(truck.transform, false);
            receiver.transform.localPosition = new Vector3(0, 1.0168f, 0);
            receiver.size = new Vector2(3.6f,4.5f);
            receiver.maximumFillHeight = .8f;
            receiver.capacityM3 = 9.6f;
            return truck;
        }

        /// <summary>Save the configured main scene in this project, retaining a first-run backup beside generated assets.</summary>
        [MenuItem("Tools/ZX200/Soil/Prepare Main Construction Site")]
        public static void PrepareMainScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode first.");
            Scene existing = SceneManager.GetSceneByPath(ScenePath);
            if (existing.IsValid() && existing.isDirty) throw new InvalidOperationException("Main scene has unsaved changes; save them before preparing the site.");
            if (!AssetDatabase.IsValidFolder(Directory)) AssetDatabase.CreateFolder("Assets", "ConstructionSite");
            if (!File.Exists(Directory + "/sence_test.before-site.unity"))
                AssetDatabase.CopyAsset(ScenePath, Directory + "/sence_test.before-site.unity");
            Scene scene = existing.IsValid() && existing.isLoaded ? existing : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            if (scene.GetRootGameObjects().Any(root => root.name == "ZX200 construction site"))
                throw new InvalidOperationException("This scene already has a construction site; reuse it instead of duplicating assets.");
            var dig = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Zx200DigController>(true)).Single();
            var robot = dig.gameObject;
            robot.transform.position = new Vector3(330.01f,7.8f,398.27f);
            var keyboard = robot.GetComponent<Zx200KeyboardController>();
            // Keep manual track driving available; autonomous cycles lock the base while working.
            keyboard.fixedBase = false;
            keyboard.boomSpeed = 10; keyboard.armSpeed = 10; keyboard.bucketSpeed = 15; keyboard.swingSpeed = 15;
            dig.workReadyPose = new Zx200JointPose(0,-26,68,25,1);
            dig.dumpPose.boom = -40;
            dig.dumpPose.arm = 50;
            Zx200PrefabSetup.ApplyEditorWorkReadyPose(dig);
            foreach (var terrain in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Terrain>(true)))
                terrain.gameObject.SetActive(false);
            var site = new GameObject("ZX200 construction site");
            Material body = Material("Truck orange", new Color(.82f,.31f,.055f));
            Material dark = Material("Truck chassis", new Color(.055f,.065f,.07f));
            Material glass = Material("Truck windows", new Color(.12f,.3f,.37f));
            Part(site.transform,"Solid work pad", robot.transform.position + new Vector3(1,-.2f,3), new Vector3(24,.4f,24), Material("Work pad",new Color(.31f,.3f,.27f)));
            Vector3 center = robot.transform.position + robot.transform.forward * 7;
            center.y = robot.transform.position.y + .6f;
            var soil = Zx200SoilDemoSetup.CreateSoilAt(center, new Vector2(6,6), .6f, Directory, 513);
            soil.transform.SetParent(site.transform,true);
            var cutter = Zx200SoilDemoSetup.AttachCutter(dig.bucketJoint.transform,soil,Directory);
            dig.bucketJoint.gameObject.AddComponent<BucketSoilResistance>();
            var truck = CreateTruck(Vector3.zero,body,dark,glass);
            truck.GetComponentInChildren<SoilReceivingZone>().GetComponent<Renderer>().sharedMaterial =
                cutter.fillVisual.GetComponent<Renderer>().sharedMaterial;
            var prefab = PrefabUtility.SaveAsPrefabAsset(truck,Directory + "/ParkedDumpTruck.prefab");
            UnityEngine.Object.DestroyImmediate(truck);
            truck = (GameObject)PrefabUtility.InstantiatePrefab(prefab,scene);
            truck.transform.position = robot.transform.TransformPoint(TruckParkingOffsetLocal);
            truck.transform.rotation = robot.transform.rotation * Quaternion.Euler(0f, TruckParkingYawDegrees, 0f);
            truck.transform.SetParent(site.transform,true);
            ValidateTruckParkingPosition(robot.transform,
                new Bounds(SoilStripCenterLocal, SoilStripSizeLocal), truck);
            var receiver = truck.GetComponentInChildren<SoilReceivingZone>();
            receiver.GetComponent<Renderer>().sharedMaterial = cutter.fillVisual.GetComponent<Renderer>().sharedMaterial;
            var unloader = cutter.gameObject.AddComponent<BucketSoilUnloader>();
            unloader.receivers = new[] { receiver };
            Camera camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true)).First(c => c.CompareTag("MainCamera"));
            camera.transform.position = robot.transform.position + new Vector3(13,9,-9);
            camera.transform.LookAt(robot.transform.position + new Vector3(2,1.4f,4));
            var cameraController = robot.GetComponent<Zx200CameraController>();
            cameraController.targetCamera = camera;
            cameraController.orbitPivotLocalPosition = new Vector3(0,1.4f,3);
            cameraController.initialThirdPersonYaw = -35;
            cameraController.initialDistance = 16; cameraController.maxDistance = 24;
            ConfigureMap(scene, robot.transform.position + new Vector3(1,0,3));
            foreach (var component in new UnityEngine.Object[] { robot.transform,keyboard,dig,cameraController })
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            PrefabUtility.RecordPrefabInstancePropertyModifications(receiver.GetComponent<Renderer>());
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log("Construction site saved in " + ScenePath + "; truck bed at " + receiver.transform.position);
        }

        private static void ValidateTruckParkingPosition(Transform machineFrame, Bounds soilStripBounds, GameObject truck)
        {
            Physics.SyncTransforms();
            Bounds truckBounds = GetCombinedColliderBoundsInFrame(truck, machineFrame);
            if (!ExcavationSiteLayoutSafety.HasRequiredClearance(soilStripBounds, truckBounds, TruckSoilClearanceMeters))
                throw new InvalidOperationException("车辆停车位与土区净距不足，无法生成安全施工场。");
        }

        private static Bounds GetCombinedColliderBoundsInFrame(GameObject root, Transform frame)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0)
                throw new InvalidOperationException("施工场对象缺少实体碰撞体：" + root.name);
            bool hasBounds = false;
            Bounds combined = default;
            foreach (Collider collider in colliders)
            {
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point;
                    if (collider is BoxCollider box)
                    {
                        Vector3 half = box.size * .5f;
                        Vector3 localPoint = box.center + new Vector3(
                            (corner & 1) == 0 ? -half.x : half.x,
                            (corner & 2) == 0 ? -half.y : half.y,
                            (corner & 4) == 0 ? -half.z : half.z);
                        point = box.transform.TransformPoint(localPoint);
                    }
                    else
                    {
                        Bounds world = collider.bounds;
                        point = new Vector3(
                            (corner & 1) == 0 ? world.min.x : world.max.x,
                            (corner & 2) == 0 ? world.min.y : world.max.y,
                            (corner & 4) == 0 ? world.min.z : world.max.z);
                    }
                    Vector3 local = frame.InverseTransformPoint(point);
                    if (!hasBounds) { combined = new Bounds(local, Vector3.zero); hasBounds = true; }
                    else combined.Encapsulate(local);
                }
            }
            return combined;
        }

        private static void ConfigureMap(Scene scene, Vector3 center)
        {
            // Reflection keeps the robotics editor assembly independent of optional Cesium/Web delivery packages.
            Type tilesetType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CesiumForUnity.Cesium3DTileset")).First(t => t != null);
            Type moundType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("CesiumMoundSite")).First(t => t != null);
            foreach (var root in scene.GetRootGameObjects())
                foreach (Component tileset in root.GetComponentsInChildren(tilesetType,true))
                {
                    string url = (string)tilesetType.GetProperty("url").GetValue(tileset);
                    if (string.IsNullOrEmpty(url) || !url.Contains("osgb_small_005_004")) { tileset.gameObject.SetActive(false); continue; }
                    string local = Path.GetFullPath(Path.Combine(Application.dataPath,"../../tilesets/osgb_small_005_004/tileset.json"));
                    if (File.Exists(local)) tilesetType.GetProperty("url").SetValue(tileset,new Uri(local).AbsoluteUri);
                    var components = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).ToArray();
                    foreach (var item in components)
                        if (item.name == "Solid work pad" || item.name == "Soil bottom plate (always solid)") item.gameObject.SetActive(false);
                    var soil = components.Select(t => t.GetComponent<DeformableSoilTerrain>()).First(s => s != null);
                    var machine = components.Select(t => t.GetComponent<Zx200DigController>()).First(d => d != null);
                    machine.workReadyPose.boom = -29;
                    Zx200PrefabSetup.ApplyEditorWorkReadyPose(machine);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(machine);
                    EditorUtility.SetDirty(machine);
                    var truck = machine.GetComponentInChildren<BucketSoilUnloader>().receivers[0].transform.parent;
                    foreach (var component in soil.GetComponents<Component>())
                        if (component != null && (component.GetType().Name == "CesiumTerrainConformer" ||
                            component.GetType().Name == "ConstructionSiteSurfaceAppearance")) UnityEngine.Object.DestroyImmediate(component);
                    soil.GetComponent<Terrain>().enabled = false;
                    soil.SoilCollider.enabled = false;
                    Component mound = soil.transform.parent.GetComponent(moundType);
                    if (mound == null) mound = soil.transform.parent.gameObject.AddComponent(moundType);
                    moundType.GetField("tileset").SetValue(mound, tileset);
                    moundType.GetField("soil").SetValue(mound, soil);
                    moundType.GetField("machine").SetValue(mound, machine);
                    moundType.GetField("truck").SetValue(mound, truck);
                    moundType.GetField("replacementAreaSize").SetValue(mound, new Vector2(28f, 28f));
                    moundType.GetField("pitDepth").SetValue(mound, 1.8f);
                    moundType.GetField("benchRiseStartLocalZ").SetValue(mound, 8.4f);
                    moundType.GetField("benchRiseEndLocalZ").SetValue(mound, 9.4f);
                    moundType.GetField("soilStripHalfWidth").SetValue(mound, 6.5f);
                    moundType.GetField("soilStripCenterLocalZ").SetValue(mound, 6.7f);
                    moundType.GetField("soilStripHalfDepth").SetValue(mound, .9f);
                    moundType.GetField("soilStripHeight").SetValue(mound, .65f);
                    moundType.GetField("truckParkingOffsetLocal").SetValue(mound, TruckParkingOffsetLocal);
                    moundType.GetField("truckParkingYawDegrees").SetValue(mound, TruckParkingYawDegrees);
                }
        }

        /// <summary>Replace the existing platform site with a local mound on intact Cesium ground and save the scene.</summary>
        [MenuItem("Tools/ZX200/Soil/Use Cesium Ground With Mound")]
        public static void UseCesiumGroundWithMound()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play mode first.");
            Scene scene = SceneManager.GetSceneByPath(ScenePath);
            if (scene.IsValid() && scene.isDirty) throw new InvalidOperationException("Save pending scene edits first.");
            if (!scene.IsValid() || !scene.isLoaded) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            ConfigureMap(scene, Vector3.zero);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        private static GameObject Part(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name; part.transform.SetParent(parent,false);
            part.transform.localPosition = position; part.transform.localScale = scale;
            part.GetComponent<Renderer>().sharedMaterial = material;
            return part;
        }

        private static Material Material(string name, Color color)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name, color = color };
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness",.1f);
            AssetDatabase.CreateAsset(material,AssetDatabase.GenerateUniqueAssetPath(Directory + "/" + name + ".mat"));
            return material;
        }
    }
}

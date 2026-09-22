using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Zx200.Editor
{
    /// <summary>Fit the supplied textured FBX to the existing parked soil receiver without changing scene references.</summary>
    public static class SmallDumpTruckSetup
    {
        private const string Folder = "Assets/ConstructionSite/SmallDumpTruck";
        private const string ModelPath = Folder + "/SmallDumpTruck.fbx";
        private const string PrefabPath = "Assets/ConstructionSite/ParkedDumpTruck.prefab";

        /// <summary>Import materials and replace the parked prefab visual and collisions; preserve the root and receiver IDs.</summary>
        [MenuItem("Tools/ZX200/Soil/Use Textured Small Dump Truck")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play mode before replacing the truck.");
            AssetDatabase.Refresh();
            ConfigureModel();
            var truck = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                var receiver = truck.GetComponentInChildren<SoilReceivingZone>(true);
                if (receiver == null) throw new InvalidOperationException("The existing truck must have a soil receiver.");
                foreach (Transform child in truck.transform.Cast<Transform>().ToArray())
                    if (child != receiver.transform) UnityEngine.Object.DestroyImmediate(child.gameObject);
                var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath), truck.transform);
                model.name = "Small dump truck visual";
                foreach (var node in model.GetComponentsInChildren<Transform>(true))
                    if (node.name == "Shadow" || node.name.Contains("Light Flare")) node.gameObject.SetActive(false);
                var meshes = model.GetComponentsInChildren<MeshFilter>();
                float bottom = meshes.Where(m => m.name.Contains("Wheel")).Min(m => m.GetComponent<Renderer>().bounds.min.y);
                // Supplied bed interior spans Z [-4.16, .66] metres, with cab facing local +Z.
                model.transform.localPosition = new Vector3(0, -bottom, 1.75f);
                var bed = meshes.Single(m => m.name == "Dumper Box");
                var points = bed.sharedMesh.vertices.Select(v => truck.transform.InverseTransformPoint(bed.transform.TransformPoint(v))).ToArray();
                float underside = points.Min(p => p.y);
                // The next horizontal level is the interior floor, about 4 cm above its underside.
                float floor = points.Where(p => p.y > underside + .015f).Min(p => p.y);
                float rim = meshes.Single(m => m.name == "Dumper Door").GetComponent<Renderer>().bounds.max.y;
                receiver.transform.localPosition = new Vector3(0, floor, 0);
                receiver.transform.localRotation = Quaternion.identity;
                receiver.transform.localScale = Vector3.one;
                receiver.size = new Vector2(2f, 4.74f);
                receiver.maximumFillHeight = rim - floor - .08f;
                receiver.capacityM3 = receiver.size.x * receiver.size.y * receiver.maximumFillHeight;
                float wallHeight = rim - floor;
                // A 2 mm skin below the visual surface lets soil cross the receiver plane before hitting the floor.
                Collider(truck.transform, "Bed floor", new Vector3(0, floor - .027f, 0), new Vector3(2.16f, .05f, 4.92f));
                Collider(truck.transform, "Left bed wall", new Vector3(-1.045f, floor + wallHeight / 2, 0), new Vector3(.07f, wallHeight, 4.92f));
                Collider(truck.transform, "Right bed wall", new Vector3(1.045f, floor + wallHeight / 2, 0), new Vector3(.07f, wallHeight, 4.92f));
                Collider(truck.transform, "Front bed wall", new Vector3(0, floor + wallHeight / 2, 2.435f), new Vector3(2.16f, wallHeight, .05f));
                Collider(truck.transform, "Tailgate", new Vector3(0, floor + wallHeight / 2, -2.435f), new Vector3(2.16f, wallHeight, .05f));
                Collider(truck.transform, "Chassis", new Vector3(0, .65f, 1), new Vector3(2, .35f, 6.9f));
                Collider(truck.transform, "Cab", new Vector3(0, 1.55f, 3.48f), new Vector3(2.25f, 1.85f, 1.82f));
                foreach (var wheel in meshes.Where(m => m.name.StartsWith("Truck ") && m.name.EndsWith("Wheel")))
                {
                    Bounds bounds = wheel.GetComponent<Renderer>().bounds;
                    Collider(truck.transform, wheel.name + " collision", bounds.center, bounds.size);
                }
                PrefabUtility.SaveAsPrefabAsset(truck, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"Small truck ready: bed={receiver.size}, floor={floor:F4}, rim={rim:F4}, capacity={receiver.capacityM3:F3} m3");
            }
            finally { PrefabUtility.UnloadPrefabContents(truck); }
        }

        private static void ConfigureModel()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            // Unity interprets the source as centimetres. 100 restores wheel diameter to about 0.89 metres.
            foreach (var key in importer.GetExternalObjectMap().Keys.ToArray()) importer.RemoveRemap(key);
            importer.globalScale = 100;
            importer.importAnimation = false;
            importer.addCollider = false;
            importer.SaveAndReimport();
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (!AssetDatabase.IsValidFolder(Folder + "/Materials")) AssetDatabase.CreateFolder(Folder, "Materials");
            foreach (var source in asset.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct())
            {
                string name = source.name.Replace(' ', '_');
                string path = Folder + "/Materials/" + name + ".mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                    AssetDatabase.CreateAsset(material, path);
                }
                material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Smoothness", name == "chrome" ? .6f : .18f);
                material.SetFloat("_Metallic", name == "chrome" ? .8f : 0);
                // Source material names use lower-case 'front/rear wheel'; supplied PNGs use underscores.
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/Textures/" + name + ".png");
                if (texture != null) material.SetTexture("_BaseMap", texture);
                else material.SetColor("_BaseColor", name.Contains("mirror") ? new Color(.35f,.42f,.46f) : new Color(.22f,.24f,.25f));
                EditorUtility.SetDirty(material);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), source.name), material);
            }
            importer.SaveAndReimport();
        }

        private static void Collider(Transform parent, string name, Vector3 center, Vector3 size)
        {
            var part = new GameObject(name);
            part.transform.SetParent(parent, false);
            part.transform.localPosition = center;
            part.AddComponent<BoxCollider>().size = size;
        }

    }
}

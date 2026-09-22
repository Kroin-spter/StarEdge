using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Unity.Robotics.UrdfImporter;
using UnityEditor;
using UnityEngine;

namespace UrdfUrpImporter
{
    /// <summary>
    /// Imports a selected URDF and saves a prefab with persistent URP/Lit materials.
    /// </summary>
    public static class UrdfUrpImportTool
    {
        private const string MenuPath = "Tools/Robotics/Import Selected URDF as URP";
        private const string UrpLitShaderName = "Universal Render Pipeline/Lit";

        /// <summary>
        /// Imports the URDF selected in the Project window and saves its URP prefab.
        /// </summary>
        [MenuItem(MenuPath)]
        public static void ImportSelected()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            GameObject prefab = Import(path);
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
        }

        /// <summary>
        /// Reports whether exactly one URDF asset is selected for the import command.
        /// </summary>
        /// <returns>True when the current selection is a `.urdf` asset.</returns>
        [MenuItem(MenuPath, true)]
        public static bool ValidateSelected()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".urdf", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Imports one URDF asset, converts materials, validates it, and saves a prefab.
        /// </summary>
        /// <param name="urdfAssetPath">Unity asset path of the URDF.</param>
        /// <returns>The saved prefab asset.</returns>
        public static GameObject Import(string urdfAssetPath)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            UrdfAssetLayout layout = UrdfAssetResolver.Resolve(urdfAssetPath);
            string importerUrdfPath = CreateImporterUrdf(layout);
            string importerMaterialDirectory = layout.UriRootDirectory + "/Materials";
            bool importerMaterialDirectoryExisted = AssetDatabase.IsValidFolder(importerMaterialDirectory);

            ImportSettings settings = ImportSettings.DefaultSettings();
            settings.convexMethod = ImportSettings.convexDecomposer.unity;
            settings.OverwriteExistingPrefabs = true;

            GameObject robot = null;
            try
            {
                var operation = UrdfRobotExtensions.Create(
                    importerUrdfPath, settings, loadStatus: false, forceRuntimeMode: false);
                while (operation.MoveNext())
                {
                    if (operation.Current != null) robot = operation.Current;
                }
                if (robot == null)
                {
                    throw new InvalidOperationException("The URDF importer did not create a robot object.");
                }

                Dictionary<string, Texture> daeTextures = LoadDaeTextures(layout.VisualMeshAssetPaths);
                Texture uniformTexture = daeTextures.Values.Where(value => value != null).Distinct().Count() == 1
                    ? daeTextures.Values.First(value => value != null)
                    : null;
                int slots = UrdfUrpMaterialConverter.ConvertRenderers(
                    robot,
                    layout.MaterialDirectory,
                    renderer => FindRendererTexture(renderer, daeTextures) ?? uniformTexture);
                if (slots == 0)
                {
                    throw new InvalidOperationException("The imported robot has no renderer material slots.");
                }

                ValidateRobot(robot);
                RunPrefabPostprocessors(robot, layout);
                EnsureAssetFolder(layout.PrefabDirectory);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(robot, layout.PrefabAssetPath);
                if (prefab == null)
                {
                    throw new InvalidOperationException($"Failed to save prefab: {layout.PrefabAssetPath}");
                }
                AssetDatabase.SaveAssets();
                Debug.Log($"URDF URP import completed: {layout.PrefabAssetPath} ({slots} slots)");
                return prefab;
            }
            finally
            {
                if (robot != null) UnityEngine.Object.DestroyImmediate(robot);
                if (File.Exists(importerUrdfPath)) File.Delete(importerUrdfPath);
                if (!importerMaterialDirectoryExisted &&
                    !importerMaterialDirectory.Equals(layout.MaterialDirectory, StringComparison.Ordinal))
                {
                    AssetDatabase.DeleteAsset(importerMaterialDirectory);
                }
            }
        }

        private static string CreateImporterUrdf(UrdfAssetLayout layout)
        {
            var document = new XmlDocument();
            document.Load(ToFullPath(layout.UrdfAssetPath));
            foreach (XmlNode node in document.SelectNodes("//mesh") ??
                     throw new InvalidOperationException("Could not enumerate URDF meshes."))
            {
                XmlAttribute filename = node.Attributes?["filename"];
                if (filename == null) continue;
                const string prefix = "package://";
                string relativeUri = filename.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? filename.Value.Substring(prefix.Length)
                    : filename.Value;
                string resolvedAssetPath = NormalizeAssetPath(
                    layout.UriRootDirectory + "/" + relativeUri);
                if (!resolvedAssetPath.StartsWith(layout.PackageDirectory + "/", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Mesh is outside the resolved package: {resolvedAssetPath}");
                }

                filename.Value = prefix + resolvedAssetPath.Substring(layout.PackageDirectory.Length + 1);
            }

            string temporaryPath = ToFullPath(
                layout.PackageDirectory + "/_UnityImport_" + layout.RobotName + ".urdf");
            document.Save(temporaryPath);
            return temporaryPath;
        }

        private static Dictionary<string, Texture> LoadDaeTextures(IEnumerable<string> daePaths)
        {
            var result = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
            foreach (string daePath in daePaths.Where(path =>
                         path.EndsWith(".dae", StringComparison.OrdinalIgnoreCase)))
            {
                string texturePath = UrdfAssetResolver.FindDaeTexture(daePath);
                result[daePath] = string.IsNullOrEmpty(texturePath)
                    ? null
                    : AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            }
            return result;
        }

        private static void RunPrefabPostprocessors(GameObject robot, UrdfAssetLayout layout)
        {
            foreach (Type type in TypeCache.GetTypesDerivedFrom<IUrdfPrefabPostprocessor>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!(Activator.CreateInstance(type) is IUrdfPrefabPostprocessor processor)) continue;
                if (processor.CanProcess(layout)) processor.Process(robot, layout);
            }
        }

        private static Texture FindRendererTexture(
            Renderer renderer,
            IReadOnlyDictionary<string, Texture> daeTextures)
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            string meshPath = filter?.sharedMesh == null
                ? null
                : AssetDatabase.GetAssetPath(filter.sharedMesh);
            if (string.IsNullOrEmpty(meshPath)) return null;
            if (meshPath.EndsWith(".dae", StringComparison.OrdinalIgnoreCase) &&
                daeTextures.TryGetValue(meshPath, out Texture direct)) return direct;

            string fileName = Path.GetFileNameWithoutExtension(meshPath);
            int suffix = fileName.LastIndexOf('_');
            if (suffix > 0 && int.TryParse(fileName.Substring(suffix + 1), out _))
            {
                fileName = fileName.Substring(0, suffix);
            }
            foreach (KeyValuePair<string, Texture> pair in daeTextures)
            {
                if (Path.GetFileNameWithoutExtension(pair.Key)
                    .Equals(fileName, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }

        private static void ValidateRobot(GameObject robot)
        {
            foreach (Renderer renderer in robot.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null ||
                        material.shader.name != UrpLitShaderName)
                    {
                        throw new InvalidOperationException(
                            $"Renderer '{renderer.name}' has an unsupported material.");
                    }
                }
            }
        }

        private static string ToFullPath(string assetPath)
        {
            string project = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Could not resolve the Unity project directory.");
            return Path.GetFullPath(Path.Combine(project, assetPath));
        }

        private static string NormalizeAssetPath(string path)
        {
            var segments = new List<string>();
            foreach (string segment in path.Replace('\\', '/').Split('/'))
            {
                if (segment == "" || segment == ".") continue;
                if (segment == ".." && segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                else if (segment != "..") segments.Add(segment);
            }
            return string.Join("/", segments);
        }

        private static void EnsureAssetFolder(string assetFolder)
        {
            string[] segments = assetFolder.Split('/');
            string current = segments[0];
            for (int index = 1; index < segments.Length; index++)
            {
                string next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segments[index]);
                current = next;
            }
        }
    }
}

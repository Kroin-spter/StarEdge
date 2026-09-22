using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using UnityEngine;

namespace UrdfUrpImporter
{
    /// <summary>
    /// Describes resolved Unity asset paths for one URDF robot package.
    /// </summary>
    public sealed class UrdfAssetLayout
    {
        /// <summary>Gets the selected URDF asset path.</summary>
        public string UrdfAssetPath { get; }

        /// <summary>Gets the directory in which generated robot assets are stored.</summary>
        public string PackageDirectory { get; }

        /// <summary>Gets the directory prepended to package URIs by the Unity importer.</summary>
        public string UriRootDirectory { get; }

        /// <summary>Gets the robot name declared by the URDF.</summary>
        public string RobotName { get; }

        /// <summary>Gets resolved visual mesh asset paths.</summary>
        public IReadOnlyList<string> VisualMeshAssetPaths { get; }

        /// <summary>Gets the generated material directory.</summary>
        public string MaterialDirectory => PackageDirectory + "/Materials";

        /// <summary>Gets the generated prefab directory.</summary>
        public string PrefabDirectory => PackageDirectory + "/Prefabs";

        /// <summary>Gets the generated prefab asset path.</summary>
        public string PrefabAssetPath => PrefabDirectory + "/" + RobotName + ".prefab";

        internal UrdfAssetLayout(
            string urdfAssetPath,
            string packageDirectory,
            string uriRootDirectory,
            string robotName,
            IReadOnlyList<string> visualMeshAssetPaths)
        {
            UrdfAssetPath = urdfAssetPath;
            PackageDirectory = packageDirectory;
            UriRootDirectory = uriRootDirectory;
            RobotName = robotName;
            VisualMeshAssetPaths = visualMeshAssetPaths;
        }
    }

    /// <summary>
    /// Resolves ROS package URIs and Collada texture references to Unity asset paths.
    /// </summary>
    public static class UrdfAssetResolver
    {
        private const string PackagePrefix = "package://";

        /// <summary>
        /// Parses a URDF and resolves its package root, mesh paths, and output locations.
        /// </summary>
        /// <param name="urdfAssetPath">Unity asset path of the URDF to resolve.</param>
        /// <returns>A validated asset layout for the robot.</returns>
        /// <exception cref="ArgumentException">Thrown when the path is not a URDF asset.</exception>
        /// <exception cref="InvalidOperationException">Thrown when package paths cannot be resolved.</exception>
        public static UrdfAssetLayout Resolve(string urdfAssetPath)
        {
            string normalizedUrdfPath = NormalizeAssetPath(urdfAssetPath);
            if (!normalizedUrdfPath.EndsWith(".urdf", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The selected asset must be a .urdf file.", nameof(urdfAssetPath));
            }

            string fullUrdfPath = ToFullPath(normalizedUrdfPath);
            if (!File.Exists(fullUrdfPath))
            {
                throw new FileNotFoundException("The selected URDF does not exist.", fullUrdfPath);
            }

            var document = new XmlDocument();
            document.Load(fullUrdfPath);
            string robotName = document.DocumentElement?.Attributes?["name"]?.Value;
            if (string.IsNullOrWhiteSpace(robotName))
            {
                throw new InvalidOperationException("The URDF does not declare a robot name.");
            }

            List<string> allUris = SelectMeshUris(document, "//mesh");
            if (allUris.Count == 0)
            {
                throw new InvalidOperationException("The URDF does not contain any mesh references.");
            }

            string urdfDirectory = GetAssetDirectory(normalizedUrdfPath);
            foreach (string candidateRoot in EnumerateAncestors(urdfDirectory))
            {
                List<string> resolved = allUris
                    .Select(uri => ResolveMeshPath(candidateRoot, uri))
                    .ToList();
                if (resolved.All(path => File.Exists(ToFullPath(path))))
                {
                    List<string> visualUris = SelectMeshUris(document, "//visual/geometry/mesh");
                    List<string> visualPaths = visualUris
                        .Select(uri => ResolveMeshPath(candidateRoot, uri))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    string packageDirectory = InferPackageDirectory(candidateRoot, allUris[0]);
                    return new UrdfAssetLayout(
                        normalizedUrdfPath,
                        packageDirectory,
                        candidateRoot,
                        SanitizeFileName(robotName),
                        visualPaths);
                }
            }

            throw new InvalidOperationException(
                $"Could not resolve every mesh referenced by '{normalizedUrdfPath}'.");
        }

        /// <summary>
        /// Reads the first Collada library image and resolves it beside the DAE asset.
        /// </summary>
        /// <param name="daeAssetPath">Unity asset path of a Collada mesh.</param>
        /// <returns>The resolved texture asset path, or null when none exists.</returns>
        public static string FindDaeTexture(string daeAssetPath)
        {
            string normalizedPath = NormalizeAssetPath(daeAssetPath);
            if (!normalizedPath.EndsWith(".dae", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(ToFullPath(normalizedPath)))
            {
                return null;
            }

            var document = new XmlDocument();
            document.Load(ToFullPath(normalizedPath));
            XmlNode imageNode = document.SelectSingleNode(
                "//*[local-name()='library_images']/*[local-name()='image']/*[local-name()='init_from']");
            if (string.IsNullOrWhiteSpace(imageNode?.InnerText))
            {
                return null;
            }

            string imageReference = Uri.UnescapeDataString(imageNode.InnerText.Trim());
            string candidate = NormalizeAssetPath(
                GetAssetDirectory(normalizedPath) + "/" + imageReference);
            return File.Exists(ToFullPath(candidate)) ? candidate : null;
        }

        private static List<string> SelectMeshUris(XmlDocument document, string xpath)
        {
            var result = new List<string>();
            foreach (XmlNode node in document.SelectNodes(xpath) ?? throw new InvalidOperationException())
            {
                string filename = node.Attributes?["filename"]?.Value;
                if (!string.IsNullOrWhiteSpace(filename))
                {
                    result.Add(filename);
                }
            }

            return result;
        }

        private static IEnumerable<string> EnumerateAncestors(string directory)
        {
            string current = directory;
            while (current.StartsWith("Assets", StringComparison.Ordinal))
            {
                yield return current;
                if (current == "Assets")
                {
                    yield break;
                }

                current = GetAssetDirectory(current);
            }
        }

        private static string ResolveMeshPath(string root, string uri)
        {
            string relative = uri.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
                ? uri.Substring(PackagePrefix.Length)
                : uri;
            return NormalizeAssetPath(root + "/" + relative);
        }

        private static string InferPackageDirectory(string uriRoot, string uri)
        {
            string relative = uri.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
                ? uri.Substring(PackagePrefix.Length)
                : uri;
            string firstSegment = relative.Split('/')[0];
            return firstSegment.Equals("meshes", StringComparison.OrdinalIgnoreCase)
                ? uriRoot
                : NormalizeAssetPath(uriRoot + "/" + firstSegment);
        }

        private static string NormalizeAssetPath(string path)
        {
            string normalized = path.Replace('\\', '/');
            var segments = new List<string>();
            foreach (string segment in normalized.Split('/'))
            {
                if (segment == "" || segment == ".") continue;
                if (segment == ".." && segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                else if (segment != "..") segments.Add(segment);
            }
            return string.Join("/", segments);
        }

        private static string GetAssetDirectory(string assetPath)
        {
            int separator = assetPath.LastIndexOf('/');
            return separator >= 0 ? assetPath.Substring(0, separator) : string.Empty;
        }

        private static string ToFullPath(string assetPath)
        {
            string projectDirectory = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Could not resolve the Unity project directory.");
            return Path.GetFullPath(Path.Combine(projectDirectory, assetPath));
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }
            return name;
        }
    }
}

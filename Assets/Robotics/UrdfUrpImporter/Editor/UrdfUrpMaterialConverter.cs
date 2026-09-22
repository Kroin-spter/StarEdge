using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UrdfUrpImporter
{
    /// <summary>
    /// Creates persistent opaque URP/Lit materials for imported robot renderers.
    /// </summary>
    public static class UrdfUrpMaterialConverter
    {
        private const string UrpLitShaderName = "Universal Render Pipeline/Lit";

        /// <summary>
        /// Creates or updates a persistent URP material for a texture.
        /// </summary>
        /// <param name="texture">Base-map texture, or null for the gray fallback.</param>
        /// <param name="materialDirectory">Unity asset directory for generated materials.</param>
        /// <returns>The persistent converted material.</returns>
        public static Material Convert(Texture texture, string materialDirectory)
        {
            ValidateAssetDirectory(materialDirectory);
            Shader shader = Shader.Find(UrpLitShaderName);
            if (shader == null)
            {
                throw new InvalidOperationException($"Shader not found: {UrpLitShaderName}");
            }

            EnsureAssetFolder(materialDirectory);
            string name = texture == null ? "Default_URP" : SanitizeName(texture.name) + "_URP";
            string materialPath = materialDirectory + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else
            {
                material.shader = shader;
            }

            material.SetTexture("_BaseMap", texture);
            material.SetTexture("_MainTex", texture);
            Color color = texture == null ? new Color(0.33f, 0.33f, 0.33f, 1.0f) : Color.white;
            material.SetColor("_BaseColor", color);
            material.SetColor("_Color", color);
            material.SetFloat("_Surface", 0.0f);
            material.SetFloat("_AlphaClip", 0.0f);
            material.SetFloat("_SrcBlend", 1.0f);
            material.SetFloat("_DstBlend", 0.0f);
            material.SetFloat("_ZWrite", 1.0f);
            material.SetOverrideTag("RenderType", "Opaque");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = -1;
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Replaces all material slots below a robot with persistent URP materials.
        /// </summary>
        /// <param name="root">Imported robot hierarchy root.</param>
        /// <param name="materialDirectory">Unity asset directory for generated materials.</param>
        /// <param name="fallbackTexture">Optional resolver used when a source slot has no texture.</param>
        /// <returns>The number of converted material slots.</returns>
        public static int ConvertRenderers(
            GameObject root,
            string materialDirectory,
            Func<Renderer, Texture> fallbackTexture)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            int count = 0;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                for (int index = 0; index < materials.Length; index++)
                {
                    Texture texture = materials[index] != null ? materials[index].mainTexture : null;
                    texture = texture != null ? texture : fallbackTexture?.Invoke(renderer);
                    materials[index] = Convert(texture, materialDirectory);
                    count++;
                }
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
            }
            AssetDatabase.SaveAssets();
            return count;
        }

        private static void ValidateAssetDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException("Material directory must be below Assets.", nameof(path));
            }
        }

        private static string SanitizeName(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "Default" : name;
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

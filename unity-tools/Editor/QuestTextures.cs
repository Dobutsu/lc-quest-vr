using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LCQuest.Editor
{
    /// <summary>
    /// The game's textures on Android as ASTC 6x6 (3.56 bits a pixel). Left to Unity, ASTC means 4x4: 8 bits, as much
    /// as the ETC2 RGBA they were before, and the Quest sits at its memory limit. Textures the game keeps uncompressed
    /// (terrain splat maps) stay so.
    /// </summary>
    internal static class QuestTextures
    {
        private const TextureImporterFormat Format = TextureImporterFormat.ASTC_6x6;

        public static void Apply()
        {
            var changed = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Distinct())
                {
                    if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;
                    if (importer.textureCompression == TextureImporterCompression.Uncompressed) continue;
                    if (importer.textureType is TextureImporterType.Lightmap or TextureImporterType.DirectionalLightmap or TextureImporterType.SingleChannel)
                        continue;
                    var android = importer.GetPlatformTextureSettings("Android");
                    if (android.overridden && android.format == Format) continue;
                    android.overridden = true;
                    android.format = Format;
                    android.maxTextureSize = importer.maxTextureSize;
                    importer.SetPlatformTextureSettings(android);
                    importer.SaveAndReimport();
                    changed++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            Debug.Log($"[QuestTextures] {changed} textures set to {Format} on Android");
        }
    }
}

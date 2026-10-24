using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LCQuest.Editor
{
    /// <summary>
    /// Builds LCVR's asset bundles (lethalcompanyvr, lethalcompanyvr-levels) for Android from the assets
    /// scripts/import_lcvr_assets.py brought in (Assets/LCVR/bundles.json), into StreamingAssets/LCVR, where
    /// LCVR.Standalone's bootstrap extracts them from the APK. Built in this project, so the bundles get the URP
    /// materials, Vulkan shaders and ASTC textures the rest of the game has.
    /// </summary>
    public static class LcvrBundles
    {
        public const string OutputFolder = "Assets/StreamingAssets/LCVR";
        private const string ListFile = "Assets/LCVR/bundles.json";

        [System.Serializable]
        private class BundleList
        {
            public string[] lethalcompanyvr;
            public string[] lethalcompanyvr_levels;
        }

        /// <returns>The path of the bundles' manifest bundle, which the player build hands to its code stripping</returns>
        public static string Build()
        {
            FixForUrp();

            // "-" is not a field name; the JSON is ours, so rename the key before parsing
            var json = File.ReadAllText(ListFile).Replace("\"lethalcompanyvr-levels\"", "\"lethalcompanyvr_levels\"");
            var list = JsonUtility.FromJson<BundleList>(json);
            var builds = new[]
            {
                new AssetBundleBuild { assetBundleName = "lethalcompanyvr", assetNames = list.lethalcompanyvr },
                new AssetBundleBuild { assetBundleName = "lethalcompanyvr-levels", assetNames = list.lethalcompanyvr_levels },
            };
            foreach (var build in builds)
            foreach (var path in build.assetNames.Where(p => AssetDatabase.AssetPathToGUID(p) == ""))
                Debug.LogError($"[LcvrBundles] {build.assetBundleName}: no asset at {path}");

            // Unity names the manifest bundle after the folder; built outside StreamingAssets, then the two bundles
            // are copied in (the build's .manifest files and the manifest bundle stay out of the APK)
            var staging = Path.GetFullPath("Temp/LcvrBundles/LCVR");
            Directory.CreateDirectory(staging);
            var manifest = BuildPipeline.BuildAssetBundles(staging, builds,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.Android);
            if (manifest == null)
                throw new BuildFailedException("LCVR's asset bundles failed to build");

            Directory.CreateDirectory(OutputFolder);
            foreach (var build in builds)
                File.Copy(Path.Combine(staging, build.assetBundleName), Path.Combine(OutputFolder, build.assetBundleName), true);
            AssetDatabase.ImportAsset(OutputFolder, ImportAssetOptions.ImportRecursive);

            var sizes = builds.Select(b => $"{b.assetBundleName} {new FileInfo(Path.Combine(staging, b.assetBundleName)).Length / 1024} KB");
            Debug.Log($"[LcvrBundles] built {string.Join(", ", sizes)}");
            return Path.Combine(staging, "LCVR.manifest");
        }

        /// <summary>
        /// LCVR's menu backdrop for URP. Its spheres (Skybox/Cloak, OuterSphere: Cloak.mat, black, seen from inside)
        /// follow the head, so URP's back-to-front sort puts them last, over the menu's canvases at 5 m (only the
        /// stars, nearer than OuterSphere, let the menu through): they go to a queue before the other transparents.
        /// The Uncloak clip fades Cloak in through HDRP Unlit's colour, which is _BaseColor on URP's.
        /// </summary>
        private static void FixForUrp()
        {
            var cloak = AssetDatabase.LoadAssetAtPath<Material>("Assets/LCVR/Material/Cloak.mat");
            if (cloak != null && cloak.renderQueue != 2900)
            {
                cloak.renderQueue = 2900;
                EditorUtility.SetDirty(cloak);
            }

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/LCVR/AnimationClip/Uncloak.anim");
            if (clip == null) return;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!binding.propertyName.StartsWith("material._UnlitColor")) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                var renamed = binding;
                renamed.propertyName = binding.propertyName.Replace("_UnlitColor", "_BaseColor");
                AnimationUtility.SetEditorCurve(clip, binding, null);
                AnimationUtility.SetEditorCurve(clip, renamed, curve);
            }
            AssetDatabase.SaveAssets();
        }

        private class BuildFailedException : System.Exception
        {
            public BuildFailedException(string message) : base(message)
            {
            }
        }
    }
}

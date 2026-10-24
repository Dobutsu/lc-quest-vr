using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LCQuest.Editor
{
    /// <summary>
    /// URP in place of HDRP: a pipeline asset with a Universal Renderer, set as the project's and every quality
    /// level's render pipeline (the export points them at the game's HDRP asset, which the HDRP shim cannot render).
    /// </summary>
    internal static class UrpSetup
    {
        private const string Folder = "Assets/LCQuest/URP";
        private const string AssetPath = Folder + "/LCQuest-URP.asset";

        public static void Apply()
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(AssetPath);
            if (asset == null)
            {
                if (!AssetDatabase.IsValidFolder("Assets/LCQuest")) AssetDatabase.CreateFolder("Assets", "LCQuest");
                if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/LCQuest", "URP");
                // The menu's own path (Create > Rendering > URP Asset (with Universal Renderer)): the renderer gets
                // its shaders and post-processing data from the package
                var createRenderer = typeof(UniversalRenderPipelineAsset).GetMethod("CreateRendererAsset",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var renderer = (ScriptableRendererData)createRenderer!.Invoke(null,
                    new object[] { Folder + "/LCQuest-Renderer.asset", RendererType.UniversalRenderer, false, "" });
                asset = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(asset, AssetPath);
            }

            // Quest-sized defaults; the VR build revisits them (stage 5)
            asset.supportsHDR = false;
            asset.msaaSampleCount = 1;
            asset.renderScale = 1f;
            asset.supportsCameraDepthTexture = false;
            asset.supportsCameraOpaqueTexture = false;
            asset.shadowDistance = 30f;
            asset.shadowCascadeCount = 1;
            asset.maxAdditionalLightsCount = 8;
            SetPrivate(asset, "m_SoftShadowsSupported", false);
            SetPrivate(asset, "m_MainLightShadowsSupported", true);
            // Lamps sit inside their shades: without their shadows they light whole rooms through the fixture (the
            // facility's kitchen came out ten times too bright). Cost on the Quest: revisited in stage 5
            SetPrivate(asset, "m_AdditionalLightShadowsSupported", true);
            SetPrivate(asset, "m_AdditionalLightsShadowmapResolution", 2048);
            SetPrivate(asset, "m_AdditionalLightsRenderingMode", LightRenderingMode.PerPixel);
            // HDRP light layers live in the same renderingLayerMask bits of lights and renderers: URP honours them only
            // with light layers on (the player camera's own spotlight lit the ship's walls without)
            SetPrivate(asset, "m_SupportsLightLayers", true);
            // Stage 5 experiments (LCQUEST_URP="forward,shvertex,nolodfade,nocookies"): measured on the headset before
            // they become defaults
            var experiment = System.Environment.GetEnvironmentVariable("LCQUEST_URP") ?? "";
            if (experiment.Contains("shvertex")) SetPrivate(asset, "m_ShEvalMode", ShEvalMode.PerVertex);
            if (experiment.Contains("nolodfade")) SetPrivate(asset, "m_EnableLODCrossFade", false);
            if (experiment.Contains("nocookies")) SetPrivate(asset, "m_SupportsLightCookies", false);
            EditorUtility.SetDirty(asset);

            // Forward+: no per-object light limit. The ship's interior is one mesh in reach of some twenty lights; plain
            // Forward keeps the 8 "most important" per object, and the floodlights and the sun pushed out the ceiling lamps
            var rendererList = (ScriptableRendererData[])typeof(UniversalRenderPipelineAsset)
                .GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(asset);
            var mode = experiment.Contains("forward") && !experiment.Contains("forward+") ? RenderingMode.Forward : RenderingMode.ForwardPlus;
            if (rendererList[0] is UniversalRendererData universal && universal.renderingMode != mode)
            {
                universal.renderingMode = mode;
                EditorUtility.SetDirty(universal);
            }
            UberPostProcessData((UniversalRendererData)rendererList[0]);

            GraphicsSettings.defaultRenderPipeline = asset;
            var current = QualitySettings.GetQualityLevel();
            for (var i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
            }
            QualitySettings.SetQualityLevel(current, false);
            Debug.Log($"[UrpSetup] URP asset set for {QualitySettings.names.Length} quality levels");

            AddRendererFeature<LCQuest.CustomPassFeature>(asset);
            // The game's decals (blood, spray paint, footprints, slime): HdrpBridge mirrors them as URP decal projectors
            // (internal in URP 14: by name)
            AddRendererFeature(asset, typeof(UniversalRendererData).Assembly.GetType("UnityEngine.Rendering.Universal.DecalRendererFeature"));
            RuntimeResources();
        }

        /// <summary>The renderer's post-processing resources as URP's, but with LCQuest/UberPost as the final pass: it also
        /// draws the game's outlines (unity-tools/Runtime/CustomPassFeature.cs, #10)</summary>
        private static void UberPostProcessData(UniversalRendererData renderer)
        {
            const string path = Folder + "/LCQuest-PostProcessData.asset";
            var data = AssetDatabase.LoadAssetAtPath<PostProcessData>(path);
            if (data == null)
            {
                var source = renderer.postProcessData != null && AssetDatabase.GetAssetPath(renderer.postProcessData) != path
                    ? renderer.postProcessData
                    : AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                data = Object.Instantiate(source);
                AssetDatabase.CreateAsset(data, path);
            }
            data.shaders.uberPostPS = Shader.Find("Hidden/LCQuest/UberPost");
            EditorUtility.SetDirty(data);
            if (renderer.postProcessData != data)
            {
                renderer.postProcessData = data;
                EditorUtility.SetDirty(renderer);
            }
            Debug.Log($"[UrpSetup] post-processing data: uber {(data.shaders.uberPostPS != null ? data.shaders.uberPostPS.name : "MISSING")}");
        }

        /// <summary>A feature on the pipeline's (first) renderer, once</summary>
        private static void AddRendererFeature<T>(UniversalRenderPipelineAsset asset) where T : ScriptableRendererFeature =>
            AddRendererFeature(asset, typeof(T));

        private static void AddRendererFeature(UniversalRenderPipelineAsset asset, System.Type type)
        {
            var list = (ScriptableRendererData[])typeof(UniversalRenderPipelineAsset)
                .GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(asset);
            var renderer = list[0];
            if (renderer.rendererFeatures.Any(f => f != null && f.GetType() == type)) return;
            var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(type);
            feature.name = type.Name;
            AssetDatabase.AddObjectToAsset(feature, renderer);
            renderer.rendererFeatures.Add(feature);
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
            Debug.Log($"[UrpSetup] renderer feature {type.Name} added");
        }

        /// <summary>
        /// Assets the runtime (unity-tools/Runtime/HdrpBridge.cs) loads from Resources: HDRP's default volume profile,
        /// which HDRP applies under every volume (moved, so its GUID and references stay), and the skybox material
        /// HDRI skies are shown with
        /// </summary>
        private static void RuntimeResources()
        {
            const string resources = "Assets/LCQuest/Resources";
            if (!AssetDatabase.IsValidFolder(resources)) AssetDatabase.CreateFolder("Assets/LCQuest", "Resources");

            var defaultsPath = resources + "/HDRPDefaultVolumeProfile.asset";
            if (AssetDatabase.LoadAssetAtPath<VolumeProfile>(defaultsPath) == null)
            {
                var source = AssetDatabase.FindAssets("DefaultSettingsVolumeProfile t:VolumeProfile")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => System.IO.Path.GetFileNameWithoutExtension(p) == "DefaultSettingsVolumeProfile");
                var error = source != null ? AssetDatabase.MoveAsset(source, defaultsPath) : "not found";
                Debug.Log($"[UrpSetup] HDRP default volume profile -> Resources: {(string.IsNullOrEmpty(error) ? "ok" : error)}");
            }

            // Keeps the shader HdrpBridge unwraps skies with in the build (Shader.Find)
            var latLongPath = resources + "/LCQuestCubeToLatLong.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(latLongPath) == null)
                AssetDatabase.CreateAsset(new Material(Shader.Find("Hidden/LCQuest/CubeToLatLong")), latLongPath);

            var haloPath = resources + "/LCQuestHalo.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(haloPath) == null)
                AssetDatabase.CreateAsset(new Material(Shader.Find("LCQuest/Halo")) { enableInstancing = true }, haloPath);

            // Keeps the cheap tree shader in the build (QuestPerformance.CheapLeaves loads it from here)
            var leavesPath = resources + "/LCQuestLeaves.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(leavesPath) == null)
                AssetDatabase.CreateAsset(new Material(Shader.Find("LCQuest/Leaves")), leavesPath);

            var skyPath = resources + "/LCQuestSky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            var skyShader = Shader.Find("LCQuest/Sky");
            if (sky == null)
                AssetDatabase.CreateAsset(new Material(skyShader), skyPath);
            else if (sky.shader != skyShader)
            {
                sky.shader = skyShader;
                EditorUtility.SetDirty(sky);
            }
        }

        private static void SetPrivate(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, value);
    }
}

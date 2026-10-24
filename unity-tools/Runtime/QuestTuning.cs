using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace LCQuest
{
    /// <summary>
    /// Rendering switches for measuring on the headset what each part costs, through QuestDiagnostics' invoke
    /// (scripts/quest-diag.sh invoke "LCQuest.QuestTuning.Set addShadows 0"); VrApi's App= time in logcat shows the GPU
    /// frame time. Keys (value 0/1 for switches):
    ///   renderScale, eyeScale (XRSettings.eyeTextureResolutionScale), foveation (XRDisplaySubsystem level, 0..1), msaa (1/2/4), hdr, shadows (main light),
    ///   addShadows (additional lights), shadowRes (additional lights' atlas), mainShadowRes, shadowDistance, lights (per-object
    ///   limit), post (the player camera's post-processing), feature:&lt;name&gt; (a renderer feature, e.g.
    ///   feature:CustomPassFeature, feature:DecalRendererFeature), intermediate (1: the renderer always draws into an intermediate texture, 0: only when a pass needs one), depthPriming (0 disabled, 1 auto, 2 forced: a depth prepass, then opaques shaded once per pixel), opaqueMask / transparentMask (the renderer's layer
    ///   masks, as an int), mipLimit (mip levels dropped from every texture), terrainShadows, terrainFoliage (trees and grass),
    ///   treeDistance, detailDistance (metres), noShadows:&lt;text&gt; (1: no shadows for lights whose path has it), addLights (0: every enabled non-directional light off, 1: those back on), lightsOff:&lt;text&gt; (1: the lights whose path has it off, 0: back on), lightLayers (URP's light layers), cookies, specular / reflections (0: URP Lit's specular highlights / environment reflections off on every material in the scenes; 1 turns them on everywhere), lightRange (every light's range times this, from its original), maxLights (n: only the n lights nearest the camera stay on, 0: all back), static:&lt;Type.field&gt; (a public static float or bool, e.g. static:LCQuest.HdrpBridge.HaloStrength)
    /// </summary>
    public static class QuestTuning
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        public static void Set(string key, float value)
        {
            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null) { Debug.Log("[QuestTuning] no URP asset"); return; }
            var on = value > 0.5f;
            switch (key)
            {
                case "renderScale": asset.renderScale = value; break;
                case "eyeScale": XRSettings.eyeTextureResolutionScale = value; break;
                case "foveation":
                    var displays = new List<XRDisplaySubsystem>();
                    SubsystemManager.GetInstances(displays);
                    foreach (var display in displays)
                    {
                        display.foveatedRenderingLevel = value;
                        Debug.Log($"[QuestTuning] foveation {display.foveatedRenderingLevel} flags {display.foveatedRenderingFlags}");
                    }
                    break;
                case "msaa": asset.msaaSampleCount = (int)value; break;
                case "hdr": asset.supportsHDR = on; break;
                case "shadows": SetField(asset, "m_MainLightShadowsSupported", on); break;
                case "addShadows": SetField(asset, "m_AdditionalLightShadowsSupported", on); break;
                case "shadowRes": SetField(asset, "m_AdditionalLightsShadowmapResolution", (int)value); break;
                case "mainShadowRes": SetField(asset, "m_MainLightShadowmapResolution", (int)value); break;
                case "shadowDistance": asset.shadowDistance = value; break;
                case "lights": asset.maxAdditionalLightsCount = (int)value; break;
                case "post":
                    // HdrpBridge's rescan sets every game camera's post-processing from this
                    HdrpBridge.PostProcessing = on;
                    foreach (var camera in Camera.allCameras.Where(c => c.cameraType == CameraType.Game && c.targetTexture == null))
                        camera.GetUniversalAdditionalCameraData().renderPostProcessing = on;
                    break;
                case "mipLimit": QualitySettings.globalTextureMipmapLimit = (int)value; break;
                case "terrainShadows":
                    foreach (var terrain in Terrain.activeTerrains) terrain.shadowCastingMode = on ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    break;
                case "terrainFoliage":
                    foreach (var terrain in Terrain.activeTerrains) terrain.drawTreesAndFoliage = on;
                    break;
                case "treeDistance":
                    foreach (var terrain in Terrain.activeTerrains) terrain.treeDistance = value;
                    break;
                case "detailDensity":
                    foreach (var terrain in Terrain.activeTerrains) terrain.detailObjectDensity = value;
                    break;
                case "terrainInfo":
                    foreach (var terrain in Terrain.activeTerrains)
                        Debug.Log($"[QuestTuning] terrain {terrain.name}: details {terrain.detailObjectDistance} m density {terrain.detailObjectDensity}, " +
                                  $"trees {terrain.treeDistance} m billboards {terrain.treeBillboardDistance} m, shadows {terrain.shadowCastingMode}, " +
                                  $"detail prototypes {string.Join(", ", terrain.terrainData.detailPrototypes.Select(p => $"{(p.prototype != null ? p.prototype.name : p.prototypeTexture?.name)} {p.renderMode} instanced {p.useInstancing}"))}");
                    break;
                case "detailDistance":
                    foreach (var terrain in Terrain.activeTerrains) terrain.detailObjectDistance = value;
                    break;
                case "addLights": LightsOff("", on); break;
                case "lightLayers": SetField(asset, "m_SupportsLightLayers", on); break;
                case "cookies": SetField(asset, "m_SupportsLightCookies", on); break;
                case "specular": Keyword("_SPECULARHIGHLIGHTS_OFF", !on); break;
                case "reflections": Keyword("_ENVIRONMENTREFLECTIONS_OFF", !on); break;
                case "lightRange":
                    foreach (var light in Object.FindObjectsOfType<Light>(true).Where(l => l.type != LightType.Directional))
                    {
                        if (!Ranges.ContainsKey(light)) Ranges[light] = light.range;
                        light.range = Ranges[light] * value;
                    }
                    break;
                case "maxLights": MaxLights((int)value); break;
                case "intermediate":
                    var rendererData = (UniversalRendererData)((ScriptableRendererData[])typeof(UniversalRenderPipelineAsset)
                        .GetField("m_RendererDataList", Private)!.GetValue(asset))[0];
                    rendererData.intermediateTextureMode = on ? IntermediateTextureMode.Always : IntermediateTextureMode.Auto;
                    rendererData.SetDirty();  // the renderer reads the mode when it is built
                    break;
                case "depthPriming":
                    var primingData = (UniversalRendererData)((ScriptableRendererData[])typeof(UniversalRenderPipelineAsset)
                        .GetField("m_RendererDataList", Private)!.GetValue(asset))[0];
                    primingData.depthPrimingMode = (DepthPrimingMode)(int)value;
                    primingData.SetDirty();
                    break;
                case "opaqueMask":
                case "transparentMask":
                    var data = (UniversalRendererData)((ScriptableRendererData[])typeof(UniversalRenderPipelineAsset)
                        .GetField("m_RendererDataList", Private)!.GetValue(asset))[0];
                    if (key == "opaqueMask") data.opaqueLayerMask = (int)value; else data.transparentLayerMask = (int)value;
                    break;
                default:
                    if (key.StartsWith("noShadows:"))
                    {
                        var name = key.Substring("noShadows:".Length);
                        if (on) QuestPerformance.NoShadows.Add(name); else QuestPerformance.NoShadows.Remove(name);
                        break;
                    }
                    if (key.StartsWith("lightsOff:"))
                    {
                        LightsOff(key.Substring("lightsOff:".Length), !on);
                        break;
                    }
                    if (key.StartsWith("static:"))
                    {
                        var path = key.Substring("static:".Length);
                        var dot = path.LastIndexOf('.');
                        var field = System.AppDomain.CurrentDomain.GetAssemblies()
                            .Select(a => a.GetType(path.Substring(0, dot))).FirstOrDefault(t => t != null)
                            ?.GetField(path.Substring(dot + 1), BindingFlags.Public | BindingFlags.Static);
                        if (field == null) { Debug.Log($"[QuestTuning] no static field {path}"); return; }
                        field.SetValue(null, field.FieldType == typeof(bool) ? on : System.Convert.ChangeType(value, field.FieldType));
                        break;
                    }
                    if (key.StartsWith("feature:"))
                    {
                        var name = key.Substring("feature:".Length);
                        var renderers = (ScriptableRendererData[])typeof(UniversalRenderPipelineAsset)
                            .GetField("m_RendererDataList", Private)!.GetValue(asset);
                        var feature = renderers[0].rendererFeatures.FirstOrDefault(f => f != null && f.name == name);
                        if (feature == null) { Debug.Log($"[QuestTuning] no renderer feature {name}"); return; }
                        feature.SetActive(on);
                        break;
                    }
                    Debug.Log($"[QuestTuning] unknown key {key}");
                    return;
            }
            Debug.Log($"[QuestTuning] {key} = {value}");
        }

        /// <summary>The lights a switch turned off, so switching back restores only those (the scenes keep many off)</summary>
        private static readonly HashSet<Light> SwitchedOff = new HashSet<Light>();

        /// <summary>Turns off the enabled non-directional lights whose path contains the text, or back on</summary>
        private static void LightsOff(string text, bool on)
        {
            if (on)
            {
                foreach (var light in SwitchedOff.Where(l => l != null && PathOf(l.transform).Contains(text)).ToList())
                {
                    light.enabled = true;
                    SwitchedOff.Remove(light);
                }
                return;
            }
            foreach (var light in Object.FindObjectsOfType<Light>().Where(l => l.enabled && l.type != LightType.Directional && PathOf(l.transform).Contains(text)))
            {
                light.enabled = false;
                SwitchedOff.Add(light);
            }
        }

        private static readonly Dictionary<Light, float> Ranges = new Dictionary<Light, float>();
        private static readonly HashSet<Light> OverBudget = new HashSet<Light>();

        /// <summary>Only the n lights nearest the camera (distance less range: those that reach it first) stay on;
        /// 0 brings the rest back. Once, at the time of the switch</summary>
        private static void MaxLights(int n)
        {
            foreach (var light in OverBudget.Where(l => l != null)) light.enabled = true;
            OverBudget.Clear();
            var camera = Camera.main;
            if (n <= 0 || camera == null) return;
            var eye = camera.transform.position;
            foreach (var light in Object.FindObjectsOfType<Light>()
                         .Where(l => l.enabled && l.type != LightType.Directional)
                         .OrderBy(l => Vector3.Distance(l.transform.position, eye) - l.range).Skip(n))
            {
                light.enabled = false;
                OverBudget.Add(light);
            }
        }

        /// <summary>A shader keyword on every material of the active renderers that has the property it belongs to
        /// (URP Lit's specular highlights and environment reflections toggles)</summary>
        private static void Keyword(string keyword, bool enable)
        {
            var property = keyword == "_SPECULARHIGHLIGHTS_OFF" ? "_SpecularHighlights" : "_EnvironmentReflections";
            foreach (var material in Object.FindObjectsOfType<Renderer>().SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct())
            {
                if (!material.HasProperty(property)) continue;
                if (enable) material.EnableKeyword(keyword); else material.DisableKeyword(keyword);
                material.SetFloat(property, enable ? 0f : 1f);
            }
        }

        private static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

        private static void SetField(object target, string field, object value) =>
            target.GetType().GetField(field, Private)!.SetValue(target, value);
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using HD = UnityEngine.Rendering.HighDefinition;
using URP = UnityEngine.Rendering.Universal;

namespace LCQuest
{
    /// <summary>
    /// What HDRP did with the game's HDRP data, done on URP every frame. The HDRP shim keeps the components and volume
    /// overrides as data; this reads them:
    ///  - per camera, before it renders: the HDRP volume stack, blended for that camera exactly as HDRP would (its
    ///    volumeLayerMask, the same volumes and weights the game animates), mapped onto URP's fog, skybox and a global
    ///    URP volume of post-processing overrides (exposure, colour adjustments, bloom, vignette, lens distortion...)
    ///  - local volumetric fog (foggy weather, indoor fog) the camera is inside: added to the fog density
    ///  - HDAdditionalLightData.lightDimmer (the game fades the sun and indirect light with it) -> Light.intensity
    ///  - HDAdditionalLightData.fadeDistance (HDRP drops a light that far from the camera: the facility's lamps at
    ///    30 m in the factory, 43 in the manor, 50-60 in the mines) -> the light's range shrinks to nothing beyond it, so
    ///    URP culls it (the range, not enabled or intensity: the game animates those)
    ///  - the facility's own lamps light half as far (FacilityLightRange): every lamp reaching a pixel costs it ~0.6 ms,
    ///    and halls have some twenty; their halos and fog glow keep the full range
    ///  - HDAdditionalCameraData's clear mode and colour -> the Camera's
    ///  - HDRP DecalProjectors -> URP DecalProjectors on the same objects, kept in step (the game fades slime decals,
    ///    recolours spray paint and toggles footprints at runtime)
    /// The lights were rescaled for a fixed camera exposure (EV100 1.3, scripts/convert_scenes.py); other exposures
    /// become URP post exposure.
    /// </summary>
    public class HdrpBridge : MonoBehaviour
    {
        public const float BakedEV100 = 1.3f;
        // Calibration (scripts/compare-set.sh against the HDRP reference renders; ReferenceRender -tune sets them)
        public static float SunScatterFraction = 0.5f;
        // The sky's ambient light: none. The game's scenes set no static lighting sky (StaticLightingSky None), and with
        // skyAmbientMode Static HDRP lights neither surfaces nor fog with the visible sky; kept as a knob
        public static float SkyAmbientScale = 0f;
        public static float MaxLampScatter = 10f;
        public static float LampScatterOutdoors = 0.18f;
        // HDRP voxelizes local fog into froxels whose first slices are deep: next to the camera it thins far more than
        // its extinction says, which a uniform fog cannot do; its extinction is scaled instead
        public static float LocalFogScale = 1f;
        /// <summary>Reference renders have no player: 1 treats the camera as indoors, 0 as outdoors, -1 asks the game</summary>
        public static float IndoorsOverride = -1f;
        // Lamp halos (HDRP's volumetric glow around lights): strength per unit fog density, size per light range
        public static float HaloStrength = 0.6f;
        public static float HaloSize = 0.35f;
        /// <summary>Scales every camera's bloom (0: URP skips the pass); the ship's radar camera is the only one with
        /// bloom in normal play</summary>
        public static float BloomScale = 1f;
        /// <summary>The game cameras' URP post-processing (grading, bloom...): off only for measurements (#10)</summary>
        public static bool PostProcessing = true;
        /// <summary>Cameras that draw into a texture (the ship's radar, body cam and door camera) draw shadows too: each
        /// renders every shadow map again for a picture a few hundred pixels wide (#12)</summary>
        public static bool TextureCameraShadows;
        /// <summary>Lights beyond their HDRP fade distance from the camera are culled (LightFade); each lamp lighting a
        /// pixel costs ~0.6 ms in the facility's halls, where some 25 lamps within 30 m reach the pixels in view</summary>
        public static bool LightFade = true;
        private readonly Dictionary<Light, float> fadedRanges = new Dictionary<Light, float>();
        /// <summary>Share of their range the facility's fixed lamps keep (below y -80, the game's own test for inside;
        /// not the lights carried by players, items or enemies: night vision, flashlights)</summary>
        public static float FacilityLightRange = 0.5f;
        private float appliedFacilityRange = FacilityLightRange;
        /// <summary>The facility lamps' ranges as the game set them</summary>
        private readonly Dictionary<Light, float> baseRanges = new Dictionary<Light, float>();
        private readonly List<Light> fadeBack = new List<Light>();

        // HDRP's grading of the main camera with the game's default volume profiles (auto exposure at EV 1, post
        // exposure 0.2, contrast 24.5, custom tonemap), measured as one tone curve from reference renders with and
        // without post-processing (scripts/fit_post_curve.py). Deviations from these settings (the game's filters)
        // are applied on top by URP.
        private const float MeasuredPostExposure = 0.5f;  // (1.3 - EV 1) + 0.2
        private const float MeasuredContrast = 24.5f;
        private static readonly Vector2[] MeasuredPostCurve =
        {
            new Vector2(0.00098f, 0.00061f),
            new Vector2(0.00116f, 0.00067f),
            new Vector2(0.00164f, 0.00072f),
            new Vector2(0.00195f, 0.00082f),
            new Vector2(0.00232f, 0.00099f),
            new Vector2(0.00276f, 0.00123f),
            new Vector2(0.00328f, 0.00141f),
            new Vector2(0.00391f, 0.00167f),
            new Vector2(0.00465f, 0.00204f),
            new Vector2(0.00552f, 0.00257f),
            new Vector2(0.00657f, 0.00322f),
            new Vector2(0.00781f, 0.00422f),
            new Vector2(0.00929f, 0.00550f),
            new Vector2(0.01105f, 0.00681f),
            new Vector2(0.01314f, 0.00840f),
            new Vector2(0.01562f, 0.01057f),
            new Vector2(0.01858f, 0.01319f),
            new Vector2(0.02210f, 0.01659f),
            new Vector2(0.02628f, 0.02104f),
            new Vector2(0.03125f, 0.02636f),
            new Vector2(0.03716f, 0.03263f),
            new Vector2(0.04419f, 0.04034f),
            new Vector2(0.05256f, 0.05111f),
            new Vector2(0.06250f, 0.06511f),
            new Vector2(0.07433f, 0.08083f),
            new Vector2(0.08839f, 0.10213f),
            new Vector2(0.10511f, 0.13576f),
            new Vector2(0.12500f, 0.18000f),
            new Vector2(0.14865f, 0.23393f),
            new Vector2(0.17678f, 0.30245f),
            new Vector2(0.21022f, 0.38413f),
            new Vector2(0.25000f, 0.46370f),
            new Vector2(0.29730f, 0.54233f),
            new Vector2(0.35355f, 0.62263f),
            new Vector2(0.42045f, 0.70901f),
            new Vector2(0.50000f, 0.78443f),
            new Vector2(0.59460f, 0.84080f),
            new Vector2(0.70711f, 0.89189f),
            new Vector2(0.84090f, 0.91898f),
            new Vector2(1.00000f, 0.99000f),
        };

        private static readonly float BakedExposure = 1f / (1.2f * Mathf.Pow(2f, BakedEV100));
        private const int UiLayer = 5;

        private VolumeStack stack;
        private VolumeProfile postProfile;
        private URP.ColorAdjustments colorAdjustments;
        private URP.Bloom bloom;
        private URP.Vignette vignette;
        private URP.Tonemapping tonemapping;
        private URP.LensDistortion lensDistortion;
        private URP.PaniniProjection panini;
        private URP.ChromaticAberration chromatic;
        private URP.FilmGrain filmGrain;
        private URP.SplitToning splitToning;
        private URP.LiftGammaGain liftGammaGain;
        private URP.ColorLookup colorLookup;
        private readonly Dictionary<(float, float, float, float, float, float), Texture2D> customCurves =
            new Dictionary<(float, float, float, float, float, float), Texture2D>();

        private int defaultsLayer = -1;

        /// <summary>Calibration against HDRP reference images: exposure fixed at the baked EV, no grading or tonemapping</summary>
        public static bool Neutral;

        /// <summary>What was applied for each camera last (QuestDiagnostics reports it)</summary>
        public static readonly Dictionary<string, string> LastApplied = new Dictionary<string, string>();
        private Material skyMaterial;
        private Cubemap skyCubemap;
        private readonly Dictionary<(Cubemap, float, float), SphericalHarmonicsL2> skyAmbient =
            new Dictionary<(Cubemap, float, float), SphericalHarmonicsL2>();

        private readonly List<HD.LocalVolumetricFog> localFogs = new List<HD.LocalVolumetricFog>();
        private readonly List<Light> directionalLights = new List<Light>();
        private readonly List<HD.LocalVolumetricFog> insideFogs = new List<HD.LocalVolumetricFog>();
        private readonly List<(Light light, HD.HDAdditionalLightData data, float distance)> nearLamps =
            new List<(Light, HD.HDAdditionalLightData, float)>();
        private Vector3 cameraPosition;
        private string lastFogNote = "";
        private static readonly int FogParamsId = Shader.PropertyToID("_LCQuestFog");
        private static readonly Vector3[] AmbientDirections =
            { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
        private readonly Color[] ambientSamples = new Color[6];
        private readonly Dictionary<Light, (HD.HDAdditionalLightData data, float dimmer)> lights =
            new Dictionary<Light, (HD.HDAdditionalLightData, float)>();
        private readonly List<(Camera camera, HD.HDAdditionalCameraData data)> cameras = new List<(Camera, HD.HDAdditionalCameraData)>();
        private float nextScan, nextDecalScan;
        private readonly Dictionary<HD.DecalProjector, URP.DecalProjector> decals = new Dictionary<HD.DecalProjector, URP.DecalProjector>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("LCQuest HdrpBridge");
            DontDestroyOnLoad(go);
            go.AddComponent<HdrpBridge>();
        }

        private void Awake()
        {
            // The global volume our post-processing overrides live in; layer Default is in every URP camera's volume mask
            postProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            colorAdjustments = postProfile.Add<URP.ColorAdjustments>();
            bloom = postProfile.Add<URP.Bloom>();
            vignette = postProfile.Add<URP.Vignette>();
            tonemapping = postProfile.Add<URP.Tonemapping>();
            lensDistortion = postProfile.Add<URP.LensDistortion>();
            panini = postProfile.Add<URP.PaniniProjection>();
            chromatic = postProfile.Add<URP.ChromaticAberration>();
            filmGrain = postProfile.Add<URP.FilmGrain>();
            splitToning = postProfile.Add<URP.SplitToning>();
            liftGammaGain = postProfile.Add<URP.LiftGammaGain>();
            colorLookup = postProfile.Add<URP.ColorLookup>();
            foreach (var component in postProfile.components)
                component.SetAllOverridesTo(true);

            var volumeObject = new GameObject("LCQuest PostVolume") { layer = 0 };
            volumeObject.transform.SetParent(transform);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10000;
            volume.sharedProfile = postProfile;

            // HDRP's default volume profile (HDRP global settings) under every volume, as HDRP applies it; on a layer of
            // its own that each camera's mask gets, since the game's volumes keep their layers
            var defaults = Resources.Load<VolumeProfile>("HDRPDefaultVolumeProfile");
            for (var layer = 31; layer > 7 && defaults != null; layer--)
            {
                if (LayerMask.LayerToName(layer).Length > 0) continue;
                defaultsLayer = layer;
                var defaultsObject = new GameObject("LCQuest HDRP defaults") { layer = layer };
                defaultsObject.transform.SetParent(transform);
                var defaultsVolume = defaultsObject.AddComponent<Volume>();
                defaultsVolume.isGlobal = true;
                defaultsVolume.priority = -10000;
                defaultsVolume.sharedProfile = defaults;
                break;
            }
            Debug.Log($"[LCQuest] HDRP default volume profile: {(defaults != null ? $"layer {defaultsLayer}" : "missing")}");

            var template = Resources.Load<Material>("LCQuestSky");
            if (template != null) skyMaterial = new Material(template);

            SceneManager.sceneLoaded += (_, _) => nextScan = 0;
            RenderPipelineManager.beginCameraRendering += BeginCamera;
        }

        private void OnDestroy() => RenderPipelineManager.beginCameraRendering -= BeginCamera;

        // --- scene objects ------------------------------------------------------------------------------------------

        private void LateUpdate()
        {
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 2f;
                Scan();
            }

            if (Time.unscaledTime >= nextDecalScan)
            {
                nextDecalScan = Time.unscaledTime + 1f;
                foreach (var hd in FindObjectsOfType<HD.DecalProjector>(true))
                    if (!decals.ContainsKey(hd))
                        decals[hd] = hd.gameObject.AddComponent<URP.DecalProjector>();
            }
            SyncDecals();

            // lightDimmer: the baked intensity already holds the dimmer the light was saved with; apply changes since
            changedLights.Clear();
            foreach (var pair in lights)
            {
                var light = pair.Key;
                var (data, last) = pair.Value;
                if (light == null || data == null) { changedLights.Add((light, null, 0f)); continue; }
                var dimmer = data.lightDimmer;
                if (dimmer == last) continue;
                if (last > 0.0001f) light.intensity = light.intensity / last * dimmer;
                else if (dimmer > 0.0001f) light.intensity = dimmer;  // nothing to scale from: a dim fallback
                changedLights.Add((light, data, dimmer));
            }
            foreach (var (light, data, dimmer) in changedLights)
            {
                if (data == null) lights.Remove(light);
                else lights[light] = (data, dimmer);
            }

            var round = StartOfRound.Instance;
            var view = round != null && round.activeCamera != null ? round.activeCamera : Camera.main;
            if (view == null)
                foreach (var (camera, _) in cameras)
                    if (camera != null && camera.name == "MainCamera") { view = camera; break; }
            if (FacilityLightRange != appliedFacilityRange)
            {
                appliedFacilityRange = FacilityLightRange;
                foreach (var light in baseRanges.Keys.ToList())
                    if (light == null) baseRanges.Remove(light); else ScaleRange(light);
            }
            FadeLights(view);
            UpdateHalos(view);

            // URP shadows only its main light: make it the strongest shadow-casting sun (scenes name the shadowless
            // "Indirect" fill light as their sun, which left SunWithShadows unshadowed, lighting the facility's rooms)
            Light main = null;
            foreach (var light in directionalLights)
                if (light != null && light.isActiveAndEnabled && light.shadows != LightShadows.None && (main == null || light.intensity > main.intensity))
                    main = light;
            if (main != null && RenderSettings.sun != main) RenderSettings.sun = main;

            foreach (var (camera, data) in cameras)
            {
                if (camera == null || data == null) continue;
                switch ((int)data.clearColorMode)
                {
                    case 0:  // Sky
                        camera.clearFlags = CameraClearFlags.Skybox;
                        camera.backgroundColor = Color.black;
                        break;
                    case 1:  // Color
                        camera.clearFlags = CameraClearFlags.SolidColor;
                        var background = data.backgroundColorHDR.gamma;  // HDRP's is linear, the Camera's gamma
                        background.a = 1f;  // the headset's compositor blends the eye buffer by its alpha
                        camera.backgroundColor = background;
                        break;
                    default:  // None
                        camera.clearFlags = CameraClearFlags.Depth;
                        break;
                }
            }
        }

        private readonly List<HD.DecalProjector> deadDecals = new List<HD.DecalProjector>();
        private readonly List<(Light light, HD.HDAdditionalLightData data, float dimmer)> changedLights =
            new List<(Light, HD.HDAdditionalLightData, float)>();

        private void SyncDecals()
        {
            deadDecals.Clear();
            foreach (var pair in decals)
            {
                var (hd, urp) = (pair.Key, pair.Value);
                if (hd == null || urp == null) { deadDecals.Add(hd); continue; }
                if (urp.enabled != hd.enabled) urp.enabled = hd.enabled;
                if (!hd.isActiveAndEnabled) continue;
                // URP's setters mark the decal for an update: only what changed (the game fades and recolours a few)
                if (urp.material != hd.material) urp.material = hd.material;
                if (urp.fadeFactor != hd.fadeFactor) urp.fadeFactor = hd.fadeFactor;
                if (urp.size != hd.size) urp.size = hd.size;
                if (urp.pivot != hd.pivot) urp.pivot = hd.pivot;
                if (urp.uvScale != hd.uvScale) urp.uvScale = hd.uvScale;
                if (urp.uvBias != hd.uvBias) urp.uvBias = hd.uvBias;
                if (urp.drawDistance != hd.drawDistance) urp.drawDistance = hd.drawDistance;
                if (urp.fadeScale != hd.fadeScale) urp.fadeScale = hd.fadeScale;
                if (urp.startAngleFade != hd.startAngleFade) urp.startAngleFade = hd.startAngleFade;
                if (urp.endAngleFade != hd.endAngleFade) urp.endAngleFade = hd.endAngleFade;
                var scaleMode = (URP.DecalScaleMode)(int)hd.scaleMode;
                if (urp.scaleMode != scaleMode) urp.scaleMode = scaleMode;
            }
            foreach (var dead in deadDecals) decals.Remove(dead);
        }

        // --- lamp halos ---------------------------------------------------------------------------------------------

        private const int MaxHalos = 24;
        private readonly List<MeshRenderer> halos = new List<MeshRenderer>();
        private Mesh haloMesh;
        private Material haloMaterial;
        private MaterialPropertyBlock haloBlock;
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private readonly List<(Light light, float distance)> haloLights = new List<(Light, float)>();

        /// <summary>
        /// HDRP's volumetric fog glows around every lamp; URP's fog is one colour for the view. The nearest lamps get
        /// an additive camera-facing glow, as bright as the fog is dense, dimmed by the fog between it and the camera
        /// </summary>
        private void UpdateHalos(Camera camera)
        {
            if (haloMaterial == null)
            {
                var template = Resources.Load<Material>("LCQuestHalo");
                if (template == null) return;
                haloMaterial = new Material(template) { enableInstancing = true };
                haloBlock = new MaterialPropertyBlock();
                haloMesh = new Mesh { name = "LCQuest halo" };
                haloMesh.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) });
                haloMesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
                haloMesh.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
                haloMesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            }

            var density = RenderSettings.fogDensity;
            haloLights.Clear();
            if (camera != null && density > 0.0005f && HaloStrength > 0f)
            {
                var eye = camera.transform.position;
                var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
                foreach (var pair in lights)
                {
                    var light = pair.Key;
                    if (light == null || light.type == LightType.Directional || !light.isActiveAndEnabled || light.intensity <= 0f) continue;
                    if (player != null && light.transform.IsChildOf(player.transform)) continue;  // night vision, helmet light
                    var d = Vector3.Distance(light.transform.position, eye);
                    if (d < 1.5f || d > Mathf.Min(60f, FullRange(light) * 4f)) continue;
                    haloLights.Add((light, d));
                }
                haloLights.Sort((a, b) => a.distance.CompareTo(b.distance));
            }

            for (var i = 0; i < MaxHalos; i++)
            {
                if (i >= haloLights.Count)
                {
                    if (i < halos.Count && halos[i].enabled) halos[i].enabled = false;
                    continue;
                }
                if (i >= halos.Count)
                {
                    var go = new GameObject("LCQuest halo") { layer = 0 };
                    go.transform.SetParent(transform);
                    go.AddComponent<MeshFilter>().sharedMesh = haloMesh;
                    var renderer = go.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = haloMaterial;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    halos.Add(renderer);
                }
                var (light, distance) = haloLights[i];
                var data = lights.TryGetValue(light, out var entry) ? entry.data : null;
                var dimmer = data != null ? data.volumetricDimmer : 1f;
                var size = Mathf.Clamp(FullRange(light) * HaloSize, 0.5f, 8f);
                var halo = halos[i];
                halo.enabled = true;
                halo.transform.position = light.transform.position;
                halo.transform.rotation = Quaternion.LookRotation(halo.transform.position - camera.transform.position, camera.transform.up);
                halo.transform.localScale = new Vector3(size, size, size);
                var through = Mathf.Exp(-density * distance);
                haloBlock.SetColor(ColorId, LightColor(light) * (light.intensity * dimmer * density * HaloStrength * through));
                halo.SetPropertyBlock(haloBlock);
            }
        }

        private void Scan()
        {
            foreach (var camera in new List<Camera>(farPlanes.Keys))
                if (camera == null) farPlanes.Remove(camera);

            localFogs.Clear();
            localFogs.AddRange(FindObjectsOfType<HD.LocalVolumetricFog>());

            directionalLights.Clear();
            foreach (var data in FindObjectsOfType<HD.HDAdditionalLightData>(true))
            {
                var light = data.GetComponent<Light>();
                if (light != null && light.type == LightType.Directional) directionalLights.Add(light);
                if (light != null && !lights.ContainsKey(light))
                {
                    lights[light] = (data, data.lightDimmer);
                    if (light.type != LightType.Directional && light.transform.position.y < -80f && !Carried(light))
                    {
                        baseRanges[light] = light.range;
                        ScaleRange(light);
                    }
                }
            }

            cameras.Clear();
            foreach (var data in FindObjectsOfType<HD.HDAdditionalCameraData>(true))
            {
                var camera = data.GetComponent<Camera>();
                if (camera == null) continue;
                cameras.Add((camera, data));
                var urp = camera.GetUniversalAdditionalCameraData();
                urp.renderPostProcessing = PostProcessing;
                urp.renderShadows = TextureCameraShadows || camera.targetTexture == null;
                urp.volumeLayerMask |= 1;
            }
        }

        /// <summary>HDRP's distance fade as a cut: a light farther from the view than its fade distance gets a tiny
        /// range (out of every cluster), and its own range back once nearer</summary>
        private void FadeLights(Camera view)
        {
            fadeBack.Clear();
            foreach (var pair in fadedRanges)
                if (pair.Key == null || !LightFade || view == null ||
                    Vector3.Distance(pair.Key.transform.position, view.transform.position) <= FadeDistance(pair.Key))
                    fadeBack.Add(pair.Key);
            foreach (var light in fadeBack)
            {
                if (light != null) light.range = fadedRanges[light];
                fadedRanges.Remove(light);
            }
            if (!LightFade || view == null) return;
            var eye = view.transform.position;
            foreach (var pair in lights)
            {
                var light = pair.Key;
                if (light == null || pair.Value.data == null || light.type == LightType.Directional || fadedRanges.ContainsKey(light)) continue;
                var fade = pair.Value.data.fadeDistance;
                if (fade <= 0f || fade >= 10000f || Vector3.Distance(light.transform.position, eye) <= fade) continue;
                fadedRanges[light] = light.range;
                light.range = 0.001f;
            }
        }

        /// <summary>A facility lamp's range at FacilityLightRange (a faded one keeps its tiny range until it is back)</summary>
        private void ScaleRange(Light light)
        {
            var range = baseRanges[light] * Mathf.Max(0.01f, appliedFacilityRange);
            if (fadedRanges.ContainsKey(light)) fadedRanges[light] = range; else light.range = range;
        }

        /// <summary>The range the game set: halos and fog glow are sized by it, not by the shortened lighting range
        /// (a faded light's tiny range hides its halo, as HDRP's fade does)</summary>
        private float FullRange(Light light) =>
            !fadedRanges.ContainsKey(light) && baseRanges.TryGetValue(light, out var range) ? range : light.range;

        private static bool Carried(Light light) =>
            light.GetComponentInParent<GameNetcodeStuff.PlayerControllerB>() != null ||
            light.GetComponentInParent<GrabbableObject>() != null || light.GetComponentInParent<EnemyAI>() != null;

        private float FadeDistance(Light light) =>
            lights.TryGetValue(light, out var entry) && entry.data != null ? entry.data.fadeDistance : float.MaxValue;

        // --- per camera ---------------------------------------------------------------------------------------------

        private void BeginCamera(ScriptableRenderContext context, Camera camera)
        {
            if (camera.cameraType != CameraType.Game) return;
            // Thumbnail cameras (helmet camera 34x24, ship door camera 43x65) keep what the last camera set
            var target = camera.targetTexture;
            if (target != null && target.width * target.height < 20000) return;
            stack ??= VolumeManager.instance.CreateStack();

            var hdCamera = camera.GetComponent<HD.HDAdditionalCameraData>();
            int mask = hdCamera != null ? hdCamera.volumeLayerMask.value : 1;
            if (defaultsLayer >= 0) mask |= 1 << defaultsLayer;
            VolumeManager.instance.Update(stack, camera.transform, mask);

            // A camera that only draws UI shows other cameras' already exposed images: no exposure of its own
            var uiOnly = (camera.cullingMask & ~(1 << UiLayer)) == 0;
            ApplySky();
            ApplyFog(camera);
            ApplyPost(uiOnly, camera.stereoEnabled);
            var urp = camera.GetUniversalAdditionalCameraData();
            var volumeNames = new List<string>();
            foreach (var volume in VolumeManager.instance.GetVolumes(mask))
                if (volume.isActiveAndEnabled && volume.weight > 0f)
                    volumeNames.Add($"{volume.name}({(volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile)?.name}:{volume.weight:F2})");
            LastApplied[camera.name] = $"volumes [{string.Join(",", volumeNames)}] sky {(skyCubemap != null ? skyCubemap.name : "-")} x{(skyMaterial != null ? skyMaterial.GetFloat("_Exposure") : 0):F3} mask {mask:X} post {urp.renderPostProcessing} exposure {colorAdjustments.postExposure.value:F2} " +
                                       $"contrast {colorAdjustments.contrast.value:F0} tonemap {tonemapping.mode.value} lut {colorLookup.contribution.value} " +
                                       $"bloom {bloom.intensity.value:F2} vignette {vignette.intensity.value:F2} lens {lensDistortion.intensity.value:F2} ca {chromatic.intensity.value:F2} grain {filmGrain.intensity.value:F2} fog {RenderSettings.fogDensity:F4} " +
                                       $"sky {(RenderSettings.skybox != null ? "hdri" : "-")}; {lastFogNote}";
        }

        /// <summary>
        /// HDRP's fog on URP. Extinction is 1 / mean free path, thinning above the base height (HDRP's scale height is
        /// 0.144765 of the layer between base and maximum height); URP's distance fog gets the camera's, the sky shader
        /// the whole profile (_LCQuestFog). The colour: with volumetric fog on (all of the game's profiles), HDRP's haze
        /// is light scattered by the fog: albedo x (the sky's ambient light + the sun, isotropic phase), not the
        /// constant colour, which only shows without volumetrics.
        /// </summary>
        private void ApplyFog(Camera camera)
        {
            cameraPosition = camera.transform.position;
            var fog = stack.GetComponent<HD.Fog>();
            var extinction = 0f;
            var density = 0f;
            var color = Color.black;
            var fogParams = Vector4.zero;
            var scattering = Scattering();
            if (fog != null && fog.enabled.value)
            {
                extinction = 1f / Mathf.Max(1f, fog.meanFreePath.value);
                var scale = Mathf.Max(0.01f, fog.maximumHeight.value - fog.baseHeight.value) * 0.144765f;
                var height = camera.transform.position.y;
                density = extinction * Mathf.Exp(-Mathf.Max(0f, height - fog.baseHeight.value) / scale);
                color = fog.enableVolumetricFog.value
                    ? fog.albedo.value * fog.tint.value * scattering
                    : fog.color.value * fog.tint.value * BakedExposure;
                fogParams = new Vector4(extinction, fog.baseHeight.value, scale, fog.maxFogDistance.value);
            }

            // Local fog volumes around the camera (weather, indoor fog, the clear zone around the ship), in HDRP's order:
            // by priority, Additive adds extinction, Overwrite replaces it
            var position = camera.transform.position;
            var baseDensity = density;
            insideFogs.Clear();
            foreach (var local in localFogs)
                if (local != null && local.isActiveAndEnabled && LocalFogWeight(local, position) > 0f)
                    insideFogs.Add(local);
            insideFogs.Sort((a, b) => a.parameters.priority.CompareTo(b.parameters.priority));
            foreach (var local in insideFogs)
            {
                var p = local.parameters;
                var localExtinction = LocalFogScale * LocalFogWeight(local, position) / Mathf.Max(0.5f, p.meanFreePath);
                var localColor = p.albedo * scattering;
                if ((int)p.blendingMode == 0)  // Overwrite
                {
                    density = localExtinction;
                    color = localColor;
                }
                else
                {
                    color = Color.Lerp(color, localColor, localExtinction / Mathf.Max(1e-6f, density + localExtinction));
                    density += localExtinction;
                }
            }
            if (density != baseDensity)
            {
                fogParams.x = Mathf.Max(0f, fogParams.x + density - baseDensity);
                if (fogParams.w <= 0f) fogParams.w = 1000f;
                if (fogParams.z <= 0f) { fogParams.y = -10000f; fogParams.z = 1f; }
            }

            lastFogNote = $"fog global {baseDensity:F4} -> {density:F4} via [{string.Join(",", insideFogs.ConvertAll(f => f.name))}] color {color} scattering {scattering}";
            // Always on: the fog keyword of the objects' shaders follows RenderSettings.fog only at the start of a frame,
            // so toggling it per camera left the next camera without fog; no fog is density 0
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = density;
            // RenderSettings.fogColor is gamma-encoded (Unity linearizes it for the shaders); ours is linear light
            RenderSettings.fogColor = color.gamma;
            Shader.SetGlobalVector(FogParamsId, fogParams);
            CullAtFog(camera, density, fogParams);
        }

        // --- draw distance ------------------------------------------------------------------------------------------

        /// <summary>Optical depth at which the fog hides what is behind it: exp(-6) is 0.25%, under one 8-bit step even
        /// for lamps a few times brighter than the fog. 0 keeps the cameras' own far planes</summary>
        public static float FogCullDepth = 6f;
        /// <summary>How high above the camera the scenery past the far plane reaches (hills, trees on slopes): the sky
        /// drawn in its place must be as fogged as it was; below 0 the sky is not checked</summary>
        public static float FogCullRise = 30f;
        private readonly Dictionary<Camera, (float own, float set)> farPlanes = new Dictionary<Camera, (float, float)>();

        /// <summary>
        /// Nothing past the distance where the fog turns opaque is seen, and it is all drawn (Vow's forest in foggy
        /// weather): the camera's far plane is pulled in to it. Objects are fogged by the camera's density (URP's
        /// exponential fog on view depth, as the far plane is); the sky shown instead has HDRP's height fog, thinner
        /// upwards, so the plane stays out until the sky above scenery FogCullRise higher is as opaque too. Only for
        /// cameras that show our sky behind everything: a background colour (the facility) is not the fog's colour.
        /// XR takes the far plane before beginCameraRendering, so it applies from the next frame.
        /// </summary>
        private void CullAtFog(Camera camera, float density, Vector4 fogParams)
        {
            // First seen, or the game set a far plane of its own since
            if (!farPlanes.TryGetValue(camera, out var far) || camera.farClipPlane != far.set)
                far = (camera.farClipPlane, camera.farClipPlane);
            var distance = far.own;
            if (FogCullDepth > 0f && density > 0f && !camera.orthographic &&
                camera.clearFlags == CameraClearFlags.Skybox && skyMaterial != null && RenderSettings.skybox == skyMaterial)
            {
                distance = FogCullDepth / density;
                var height = camera.transform.position.y;
                while (FogCullRise >= 0f && distance < far.own &&
                       SkyOpticalDepth(fogParams, height, FogCullRise / Mathf.Sqrt(distance * distance + FogCullRise * FogCullRise)) < FogCullDepth)
                    distance *= 1.1f;
                distance = Mathf.Clamp(distance, camera.nearClipPlane + 1f, far.own);
            }
            if (camera.farClipPlane != distance) camera.farClipPlane = distance;
            farPlanes[camera] = (far.own, camera.farClipPlane);
            lastFogNote += $" far {camera.farClipPlane:F0}/{far.own:F0}";
        }

        /// <summary>LCQuest_Sky.shader's OpticalDepth: a ray from height y0 whose direction rises dy per metre</summary>
        private static float SkyOpticalDepth(Vector4 fog, float y0, float dy)
        {
            float k = fog.x, baseHeight = fog.y, scale = Mathf.Max(fog.z, 0.01f), length = fog.w;
            var tCross = Mathf.Abs(dy) > 1e-4f ? (baseHeight - y0) / dy : -1f;
            float below, t0, t1;
            if (y0 < baseHeight)
            {
                below = dy > 0f ? Mathf.Min(tCross, length) : length;
                t0 = below;
                t1 = length;
            }
            else
            {
                below = dy < 0f && tCross < length ? length - tCross : 0f;
                t0 = 0f;
                t1 = dy < 0f ? Mathf.Min(tCross, length) : length;
            }
            var above = 0f;
            if (t1 > t0)
            {
                float h0 = Mathf.Max(0f, y0 + t0 * dy - baseHeight), h1 = Mathf.Max(0f, y0 + t1 * dy - baseHeight);
                above = Mathf.Abs(dy) > 1e-4f
                    ? scale / Mathf.Abs(dy) * Mathf.Abs(Mathf.Exp(-h0 / scale) - Mathf.Exp(-h1 / scale))
                    : (t1 - t0) * Mathf.Exp(-h0 / scale);
            }
            return k * (below + above);
        }

        /// <summary>
        /// How much of a local fog volume's density is at a point: 0 outside its box, fading in over the blend distances
        /// at its faces (positive/negative fade, as fractions of its size), a texture mask's patchy density averaged out
        /// </summary>
        private static float LocalFogWeight(HD.LocalVolumetricFog local, Vector3 position)
        {
            var p = local.parameters;
            var q = local.transform.InverseTransformPoint(position);
            var half = p.size * 0.5f;
            var weight = 1f;
            for (var axis = 0; axis < 3; axis++)
            {
                if (half[axis] <= 0f || Mathf.Abs(q[axis]) > half[axis]) return 0f;
                // HDRP's blend distances run from each face across the box (as fractions of its size; Titan's top fade
                // covers 90% of its height): both faces of every axis count
                var size = 2f * half[axis];
                var fromPositive = (half[axis] - q[axis]) / size;
                var fromNegative = (half[axis] + q[axis]) / size;
                if (p.positiveFade[axis] > 0f) weight *= Mathf.Clamp01(fromPositive / p.positiveFade[axis]);
                if (p.negativeFade[axis] > 0f) weight *= Mathf.Clamp01(fromNegative / p.negativeFade[axis]);
            }
            if ((int)p.falloffMode == 1) weight = Mathf.Pow(weight, 2.2f);  // exponential falloff, as HDRP
            if (p.invertFade) weight = 1f - weight;
            if (p.volumeMask != null) weight *= 0.35f;  // noise masks: patches of fog in clear air
            return weight;
        }

        private Material latLong;

        /// <summary>
        /// The ambient probe of an HDRI sky: the cubemap unwrapped on the GPU (64x32), read back and projected into
        /// spherical harmonics, each texel a directional light of its solid angle. URP shades with albedo x SH(n), and
        /// a uniform sky of radiance L must give L (HDRP's diffuse albedo/pi x pi L), hence the 1/pi.
        /// (DynamicGI.UpdateEnvironment does not finish synchronously on the device: it handed back stale probes.)
        /// </summary>
        private SphericalHarmonicsL2 SkyProbe(Cubemap cubemap, float intensity, float rotation)
        {
            var probe = new SphericalHarmonicsL2();
            if (latLong == null)
            {
                var shader = Shader.Find("Hidden/LCQuest/CubeToLatLong");
                if (shader == null) return probe;
                latLong = new Material(shader);
            }
            const int width = 64, height = 32;
            latLong.SetTexture("_Tex", cubemap);
            latLong.SetFloat("_Rotation", rotation);
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            Graphics.Blit(null, rt, latLong);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var pixels = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var data = pixels.GetPixels();
            Destroy(pixels);
            for (var y = 0; y < height; y++)
            {
                var theta = (1f - (y + 0.5f) / height) * Mathf.PI;
                var solidAngle = 2f * Mathf.PI / width * Mathf.PI / height * Mathf.Sin(theta);
                for (var x = 0; x < width; x++)
                {
                    var phi = (x + 0.5f) / width * 2f * Mathf.PI;
                    var dir = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
                    var c = data[y * width + x];
                    probe.AddDirectionalLight(dir, new Color(c.r, c.g, c.b), intensity * SkyAmbientScale * solidAngle / Mathf.PI);
                }
            }
            return probe;
        }

        /// <summary>A light's linear colour as URP shades with it: its colour, times its colour temperature's when used
        /// (the game's suns and lamps are warm through their temperature, not their colour)</summary>
        private static Color LightColor(Light light)
        {
            var color = light.color.linear;
            if (light.useColorTemperature && GraphicsSettings.lightsUseColorTemperature)
                color *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
            return color;
        }

        /// <summary>Light a white fog particle scatters towards the camera: the ambient probe's average plus the sun</summary>
        private Color Scattering()
        {
            RenderSettings.ambientProbe.Evaluate(AmbientDirections, ambientSamples);
            var ambient = Color.black;
            foreach (var sample in ambientSamples) ambient += sample / ambientSamples.Length;
            // Directional lights: HDRP's volumetric dimmer, none from below the horizon, and a fraction of the rest:
            // HDRP's volumetric shadows keep most of the fog out of the sun (terrain, cliffs, buildings), which a single
            // fog colour cannot see; 0.5 matches the reference renders of the fogged moons (scripts/compare-set.sh)
            var sunLight = Color.black;
            foreach (var light in directionalLights)
            {
                if (light == null || !light.isActiveAndEnabled) continue;
                var data = light.GetComponent<HD.HDAdditionalLightData>();
                var dimmer = data != null ? data.volumetricDimmer : 1f;
                var visible = Mathf.Clamp01(-light.transform.forward.y * 4f);
                // Light.intensity is HDRP's lux x exposure / pi (scripts/convert_scenes.py); isotropic phase 1/(4 pi)
                sunLight += LightColor(light) * (light.intensity * 0.25f * dimmer * visible * SunScatterFraction);
            }
            // Indoors (the facility, the ship's room) the sun does not reach the fog and the lamps light it instead:
            // HDRP's volumetric shadows and local light scattering. The game knows where the player is; without a
            // player (reference renders) the camera is outdoors. (A dark sky does not mean indoors: Rend and the
            // Company's moon have near-black skies, and their fog must not glow with the ship's floodlights)
            var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            var indoors = IndoorsOverride >= 0f ? IndoorsOverride > 0.5f : player != null && (player.isInsideFactory || player.isInHangarShipRoom);
            var outdoors = indoors ? 0f : 1f;
            sunLight *= outdoors;

            // Lamps near the camera light the fog around them (HDRP's volumetric fog glows around every light): the
            // fog's equilibrium radiance next to a point light of intensity I (URP units, I x exposure / pi) at distance
            // r is albedo x I / (4 r^2), taken at the camera, from the nearest few lamps in range
            var lampLight = Color.black;
            var position = cameraPosition;
            nearLamps.Clear();
            foreach (var pair in lights)
            {
                var light = pair.Key;
                if (light == null || light.type == LightType.Directional || !light.isActiveAndEnabled) continue;
                var distance = Vector3.Distance(light.transform.position, position);
                if (distance > FullRange(light)) continue;
                nearLamps.Add((light, pair.Value.data, distance));
            }
            nearLamps.Sort((a, b) => a.distance.CompareTo(b.distance));
            for (var i = 0; i < nearLamps.Count && i < 8; i++)
            {
                var (light, data, distance) = nearLamps[i];
                var dimmer = data != null ? data.volumetricDimmer : 1f;
                var r = Mathf.Max(distance, 1.5f);
                lampLight += LightColor(light) * (light.intensity * dimmer / (4f * r * r));
            }
            // Only where the sky does not light the fog (indoors, the facility, the ship in orbit): one colour for the
            // whole view cannot hold a lamp's local glow, and under an open sky the ship's floodlights would fill the
            // air with it. Capped for the same reason
            lampLight *= Mathf.Lerp(1f, LampScatterOutdoors, outdoors);
            var peak = Mathf.Max(lampLight.r, Mathf.Max(lampLight.g, lampLight.b));
            if (peak > MaxLampScatter) lampLight *= MaxLampScatter / peak;
            return ambient + sunLight + lampLight;
        }

        private void ApplySky()
        {
            var environment = stack.GetComponent<HD.VisualEnvironment>();
            var hdri = stack.GetComponent<HD.HDRISky>();
            // VisualEnvironment.skyType: HDRP's sky unique ids; HDRI sky is 1. No sky: black, no ambient light
            if (skyMaterial == null || environment == null || environment.skyType.value != 1 || hdri == null || hdri.hdriSky.value == null)
            {
                RenderSettings.skybox = null;
                RenderSettings.ambientMode = AmbientMode.Custom;
                RenderSettings.ambientProbe = new SphericalHarmonicsL2();
                return;
            }
            var cubemap = hdri.hdriSky.value as Cubemap;
            if (cubemap == null)
            {
                RenderSettings.skybox = null;
                RenderSettings.ambientMode = AmbientMode.Custom;
                RenderSettings.ambientProbe = new SphericalHarmonicsL2();
                return;
            }
            if (cubemap != skyCubemap)
            {
                skyCubemap = cubemap;
                skyMaterial.SetTexture("_Tex", cubemap);
            }
            // HDRP's sky intensity modes: 0 Exposure (2^exposure x multiplier), 1 Lux (desired lux over the HDRI's upper
            // hemisphere lux; none measured: no sky light), 2 Multiplier
            var mode = (int)hdri.skyIntensityMode.value;
            var skyScale = mode switch
            {
                1 => hdri.upperHemisphereLuxValue.value > 1e-4f ? hdri.desiredLuxValue.value / hdri.upperHemisphereLuxValue.value : 0f,
                2 => hdri.multiplier.value,
                _ => Mathf.Pow(2f, hdri.exposure.value) * hdri.multiplier.value,
            };
            var intensity = skyScale * BakedExposure;
            skyMaterial.SetFloat("_Exposure", intensity);
            skyMaterial.SetFloat("_Rotation", -hdri.rotation.value);
            RenderSettings.skybox = skyMaterial;

            // HDRP lights everything with the sky (ambient probe); the same sky's probe, computed once per sky setup
            var key = (cubemap, intensity, hdri.rotation.value);
            if (!skyAmbient.TryGetValue(key, out var probe))
            {
                probe = SkyProbe(cubemap, intensity, -hdri.rotation.value);
                Debug.Log($"[LCQuest] sky ambient from {cubemap.name} x{intensity:F3}: {probe[0, 0]:F3} {probe[1, 0]:F3} {probe[2, 0]:F3}");
                skyAmbient[key] = probe;
            }
            RenderSettings.ambientMode = AmbientMode.Custom;
            RenderSettings.ambientProbe = probe;
        }

        /// <summary>
        /// HDRP's custom tonemapping curve (RP Core's HableCurve, as HDRP evaluates it) as a URP user LUT: URP looks it
        /// up in sRGB, in the pipeline's LUT size (strip of size x size slices)
        /// </summary>
        private Texture2D CustomCurve(HD.Tonemapping t)
        {
            var key = (t.toeStrength.value, t.toeLength.value, t.shoulderStrength.value, t.shoulderLength.value, t.shoulderAngle.value, t.gamma.value);
            if (customCurves.TryGetValue(key, out var lut) && lut != null) return lut;

            var curve = new HableCurve();
            curve.Init(key.Item1, key.Item2, key.Item3, key.Item4, key.Item5, key.Item6);
            var size = UniversalRenderPipeline.asset != null ? UniversalRenderPipeline.asset.colorGradingLutSize : 32;
            var table = new float[size];
            for (var i = 0; i < size; i++)
            {
                var linear = Mathf.GammaToLinearSpace(i / (size - 1f));
                table[i] = Mathf.LinearToGammaSpace(Mathf.Clamp01(curve.Eval(linear)));
            }
            lut = new Texture2D(size * size, size, TextureFormat.RGBAHalf, false, true)
            {
                name = "LCQuest HDRP custom tonemap", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color[size * size * size];
            for (var g = 0; g < size; g++)
            for (var b = 0; b < size; b++)
            for (var r = 0; r < size; r++)
                pixels[g * size * size + b * size + r] = new Color(table[r], table[g], table[b], 1f);
            lut.SetPixels(pixels);
            lut.Apply(false, true);
            customCurves[key] = lut;
            return lut;
        }

        private Texture2D measuredLut;

        /// <summary>MeasuredPostCurve as a URP user LUT (looked up in sRGB), interpolated in log space</summary>
        private Texture2D MeasuredCurve()
        {
            if (measuredLut != null) return measuredLut;
            var size = UniversalRenderPipeline.asset != null ? UniversalRenderPipeline.asset.colorGradingLutSize : 32;
            var table = new float[size];
            for (var i = 0; i < size; i++)
            {
                var x = Mathf.GammaToLinearSpace(i / (size - 1f));
                table[i] = Mathf.LinearToGammaSpace(Mathf.Clamp01(EvaluateMeasured(x)));
            }
            measuredLut = BuildLut(table, size, "LCQuest HDRP grading (measured)");
            return measuredLut;
        }

        private static float EvaluateMeasured(float x)
        {
            var c = MeasuredPostCurve;
            if (x <= c[0].x) return x * c[0].y / c[0].x;
            for (var i = 1; i < c.Length; i++)
            {
                if (x > c[i].x) continue;
                var t = Mathf.InverseLerp(Mathf.Log(c[i - 1].x), Mathf.Log(c[i].x), Mathf.Log(x));
                return Mathf.Exp(Mathf.Lerp(Mathf.Log(c[i - 1].y), Mathf.Log(c[i].y), t));
            }
            return c[c.Length - 1].y;
        }

        private static Texture2D BuildLut(float[] table, int size, string name)
        {
            var lut = new Texture2D(size * size, size, TextureFormat.RGBAHalf, false, true)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color[size * size * size];
            for (var g = 0; g < size; g++)
            for (var b = 0; b < size; b++)
            for (var r = 0; r < size; r++)
                pixels[g * size * size + b * size + r] = new Color(table[r], table[g], table[b], 1f);
            lut.SetPixels(pixels);
            lut.Apply(false, true);
            return lut;
        }

        private void ApplyPost(bool uiOnly, bool headset)
        {
            var exposure = stack.GetComponent<HD.Exposure>();
            var hdColor = stack.GetComponent<HD.ColorAdjustments>();
            var ev = BakedEV100;
            if (exposure != null)
            {
                switch ((int)exposure.mode.value)
                {
                    case 0:  // Fixed
                        ev = exposure.fixedExposure.value;
                        break;
                    case 1: case 2: case 4:  // Automatic, CurveMapping, AutomaticHistogram: inside the limits
                        // HLSL clamp(ev, min, max) = min(max(ev, min), max): the game's limits (min 1.62 > max 1) pin it at 1
                        ev = Mathf.Min(Mathf.Max(BakedEV100, exposure.limitMin.value), exposure.limitMax.value);
                        break;
                }
                ev -= exposure.compensation.value;
            }
            colorAdjustments.postExposure.value = (uiOnly ? 0f : BakedEV100 - ev) + (hdColor != null ? hdColor.postExposure.value : 0f);
            // The HUD camera shows the player camera's finished picture; HDRP leaves it as it is (measured: main camera
            // render texture -> screen is 1:1 in the mid tones), so no second grading here
            if (Neutral || uiOnly)
            {
                foreach (var component in postProfile.components) component.active = false;
                return;
            }
            foreach (var component in postProfile.components) component.active = true;
            if (hdColor != null)
            {
                colorAdjustments.contrast.value = hdColor.contrast.value;
                colorAdjustments.colorFilter.value = hdColor.colorFilter.value;
                colorAdjustments.hueShift.value = hdColor.hueShift.value;
                colorAdjustments.saturation.value = hdColor.saturation.value;
            }

            var hdBloom = stack.GetComponent<HD.Bloom>();
            if (hdBloom != null)
            {
                bloom.intensity.value = hdBloom.intensity.value * 2f * BloomScale;  // HDRP's 0..1 against URP's 0..
                bloom.threshold.value = hdBloom.threshold.value;
                bloom.scatter.value = hdBloom.scatter.value;
                bloom.tint.value = hdBloom.tint.value;
            }

            var hdVignette = stack.GetComponent<HD.Vignette>();
            if (hdVignette != null)
            {
                vignette.color.value = hdVignette.color.value;
                vignette.intensity.value = hdVignette.intensity.value;
                vignette.smoothness.value = hdVignette.smoothness.value;
                vignette.rounded.value = hdVignette.rounded.value;
            }

            // HDRP tonemapping: Neutral and ACES are URP's too (applied in LDR as well); HDRP's Custom curve (the
            // game's default profile: toe/shoulder, gamma 0.6) becomes a lookup table of the same curve
            var hdTonemapping = stack.GetComponent<HD.Tonemapping>();
            var hdMode = hdTonemapping != null ? (int)hdTonemapping.mode.value : 0;
            tonemapping.mode.value = hdMode switch { 1 => TonemappingMode.Neutral, 2 => TonemappingMode.ACES, _ => TonemappingMode.None };
            if (hdMode == 3 && !uiOnly)
            {
                colorLookup.texture.value = MeasuredCurve();
                colorLookup.contribution.value = 1f;
                colorAdjustments.postExposure.value -= MeasuredPostExposure;
                colorAdjustments.contrast.value -= MeasuredContrast;
            }
            else if (hdMode == 3)
            {
                colorLookup.texture.value = CustomCurve(hdTonemapping);
                colorLookup.contribution.value = 1f;
            }
            else
                colorLookup.contribution.value = 0f;

            var hdLens = stack.GetComponent<HD.LensDistortion>();
            if (hdLens != null)
            {
                lensDistortion.intensity.value = hdLens.intensity.value;
                lensDistortion.xMultiplier.value = hdLens.xMultiplier.value;
                lensDistortion.yMultiplier.value = hdLens.yMultiplier.value;
                lensDistortion.scale.value = hdLens.scale.value;
            }

            var hdPanini = stack.GetComponent<HD.PaniniProjection>();
            if (hdPanini != null)
            {
                panini.distance.value = hdPanini.distance.value;
                panini.cropToFit.value = hdPanini.cropToFit.value;
            }

            var hdChromatic = stack.GetComponent<HD.ChromaticAberration>();
            if (hdChromatic != null) chromatic.intensity.value = hdChromatic.intensity.value;

            var hdGrain = stack.GetComponent<HD.FilmGrain>();
            if (hdGrain != null)
            {
                filmGrain.intensity.value = hdGrain.intensity.value;
                filmGrain.response.value = hdGrain.response.value;
                filmGrain.type.value = (FilmGrainLookup)Mathf.Clamp((int)hdGrain.type.value, 0, 10);
            }

            // In the headset: no lens distortion, Panini (a full-screen pass of its own), chromatic aberration or film
            // grain, screen-space warps and noise on top of the lenses' own (insanity, drunkenness, poison, underwater,
            // the scan pulse; LCVR's DisableLensDistortion, always on). The radar and security monitors keep theirs. The
            // vignette stays: LCVR's damage, death and muffle feedback is one, and URP centres it on each eye.
            // The profile is shared by all cameras, so these are reset rather than skipped
            if (headset)
            {
                lensDistortion.intensity.value = 0f;
                panini.distance.value = 0f;
                chromatic.intensity.value = 0f;
                filmGrain.intensity.value = 0f;
            }

            var hdSplit = stack.GetComponent<HD.SplitToning>();
            if (hdSplit != null)
            {
                splitToning.shadows.value = hdSplit.shadows.value;
                splitToning.highlights.value = hdSplit.highlights.value;
                splitToning.balance.value = hdSplit.balance.value;
            }

            var hdLgg = stack.GetComponent<HD.LiftGammaGain>();
            if (hdLgg != null)
            {
                liftGammaGain.lift.value = hdLgg.lift.value;
                liftGammaGain.gamma.value = hdLgg.gamma.value;
                liftGammaGain.gain.value = hdLgg.gain.value;
            }
        }
    }
}

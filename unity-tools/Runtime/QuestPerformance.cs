using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LCQuest
{
    /// <summary>
    /// Memory and GPU savings that change what is drawn (measured on the Quest 3: dumpsys meminfo, the memory section of
    /// quest-diag.sh's report, scripts/quest-trace.sh):
    ///   - every texture without its largest mip (TextureMipLimit): 1,021 textures stay loaded (items, enemies), many
    ///     4096 or 2048 wide, ~430 MB at full size; at ~2000 pixels per eye their top mip is rarely seen
    ///   - the helmet visor's condensation plane (GlassCondensation: alpha 0, only the lamps' glints on drops) is a
    ///     full-screen transparent lit quad 6 cm from the eyes, ~12 ms a frame; HDRP needed it to show volumetric fog
    ///     in front of the camera, our fog is in the shaders. Switched off
    ///   - no terrain grass (DrawGrass): the ship's lamps drew Vow's grass into their shadow maps, ~15 ms
    ///   - shadow budget: of the lights the scenes cast shadows from, only those that light most around the camera
    ///     keep them, within MaxShadowCost. A spot light's cost is the geometry in its reach, (range / 10 m)^2 and at
    ///     least 1, a point light's PointShadowCost (beyond the budget): a lamp in a room costs 1, the ship's exterior floodlights
    ///     (44 m, the forest of Vow in their maps: ~10 ms there) ~19, more than the budget. The rest light unshadowed.
    ///     A lamp that needs its shadow to stay inside its shade is the nearest one, so it keeps it
    ///   - LOD0 only within half its reach (Lod0Reach): the forests' cost is the full trees near the camera, their
    ///     alpha-clipped leaves; the next level has the same material and fewer leaves. Outside the ship: March
    ///     27.8 -> 25.4 ms, Vow 29.9 -> 28.8, rooms unchanged. LCVR's lodBias 2 stays: a lower one (1: the same
    ///     ~1.2 ms on Vow) also brings the flat far trees (another material, orange in the light) twice as near and
    ///     culls the forest at ~60 m instead of ~120
    ///   - no rain drop wider on screen than RainMaxSize: the rainy and stormy weathers' drops are streaks ~12 m long
    ///     in a lit transparent material, 750 and 1,200 of them around the player, and the game lets one grow to half
    ///     the view as it passes the eyes, a smear in the headset. Not for speed: on Vow even no rain at all was within
    ///     the noise (~29 ms either way), so RainDensity keeps every drop
    ///   - the ship's body cam only while its picture shows (IdleBodyCamOff, #12)
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public class QuestPerformance : MonoBehaviour
    {
        public static int TextureMipLimit = 1;
        public static float MaxShadowCost = 8f;
        /// <summary>Six maps, each drawing whole meshes in reach: the ship's interior is one mesh, so a monitor's 4 m
        /// point light drew all of it six times (shadows 7 -> 18 ms on the ship). Above the budget</summary>
        public static float PointShadowCost = 24f;
        public static bool HideVisorPlane = true;
        /// <summary>Terrain grass (detail meshes) off: every shadow-casting lamp drew it into its shadow map, Vow's ship
        /// 17 -> 2.6 ms in shadows, and it cost the opaque pass outside too</summary>
        public static bool DrawGrass;
        /// <summary>QualitySettings.lodBias over LCVR's (its config, 2); 0 keeps LCVR's</summary>
        public static float LodBias;
        /// <summary>Share of its reach every LODGroup's first level keeps: LOD0 gives way this much nearer, the later
        /// levels and the distance it is culled at stay</summary>
        public static float Lod0Reach = 0.5f;
        /// <summary>Share of the rain's drops kept (emission rate and the most alive at once)</summary>
        public static float RainDensity = 1f;
        /// <summary>Widest a rain drop gets, a share of the view (the game's 0.5): only drops within a few metres of the
        /// eyes are narrowed, a 20 cm drop within ~3 m</summary>
        public static float RainMaxSize = 0.03f;
        /// <summary>The alpha-clipped materials of LODGroup trees drawn with LCQuest/Leaves (sun diffuse per pixel, the
        /// rest per vertex) instead of Lit (#11)</summary>
        public static bool CheapLeaves = true;
        /// <summary>LCQuest/Leaves lights the trees with the local lights per pixel instead of per vertex (still diffuse
        /// only): the ship's floodlights on near trunks, ~0.7 ms on Vow by the ship (per vertex they fall between a
        /// trunk's few vertices and it stays dark)</summary>
        public static bool LeavesPixelLights = true;
        /// <summary>The ship's body cam (ManualCameraRenderer.headMountedCam) renders only while the monitor shows it: the
        /// game switches it on with the radar at 22 fps and hides only its picture when the radar follows the local
        /// player (alone, always), so it drew the scene for nothing (#12)</summary>
        public static bool IdleBodyCamOff = true;
        /// <summary>Lights whose path contains one of these never get shadows (QuestTuning noShadows:&lt;text&gt;)</summary>
        public static readonly HashSet<string> NoShadows = new HashSet<string>();
        private readonly Dictionary<Light, string> paths = new Dictionary<Light, string>();

        private readonly Dictionary<Light, LightShadows> wanted = new Dictionary<Light, LightShadows>();
        private readonly Dictionary<Light, LightShadows> applied = new Dictionary<Light, LightShadows>();
        private readonly List<(Light light, float score)> candidates = new List<(Light, float)>();
        private readonly List<Light> dead = new List<Light>();
        private readonly Dictionary<LODGroup, float> lod0 = new Dictionary<LODGroup, float>();
        private float appliedReach = 1f;
        private readonly Dictionary<ParticleSystem, (float rate, int max, float size)> rain =
            new Dictionary<ParticleSystem, (float, int, float)>();
        private float appliedDensity = RainDensity, appliedRainSize = RainMaxSize;
        private readonly Dictionary<Material, Shader> leaves = new Dictionary<Material, Shader>();
        private bool appliedLeaves = CheapLeaves;
        private Shader leavesShader;
        private static readonly int LeavesPixelLightsId = Shader.PropertyToID("_LCQuestLeavesPixelLights");
        private LODGroup localBody;
        private Renderer visorPlane;
        private readonly List<ManualCameraRenderer> monitors = new List<ManualCameraRenderer>();
        private float nextScan, nextBudget;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LimitTextures() => QualitySettings.globalTextureMipmapLimit = TextureMipLimit;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("LCQuest Performance");
            DontDestroyOnLoad(go);
            go.AddComponent<QuestPerformance>();
        }

        private void Update()
        {
            var now = Time.realtimeSinceStartup;
            if (now >= nextScan)
            {
                nextScan = now + 2f;
                Scan();
            }
            // LCVR sets its own at startup and when its config changes
            if (LodBias > 0f && QualitySettings.lodBias != LodBias) QualitySettings.lodBias = LodBias;
            if (Lod0Reach != appliedReach)
            {
                appliedReach = Lod0Reach;
                foreach (var group in lod0.Keys.ToList())
                    if (group == null) lod0.Remove(group); else ApplyReach(group);
            }
            if (RainDensity != appliedDensity || RainMaxSize != appliedRainSize)
            {
                appliedDensity = RainDensity;
                appliedRainSize = RainMaxSize;
                foreach (var drops in rain.Keys.ToList())
                    if (drops == null) rain.Remove(drops); else ApplyRain(drops);
            }
            if (CheapLeaves != appliedLeaves)
            {
                appliedLeaves = CheapLeaves;
                foreach (var material in leaves.Keys.ToList())
                    if (material == null) leaves.Remove(material); else ApplyLeaves(material);
            }
            Shader.SetGlobalFloat(LeavesPixelLightsId, LeavesPixelLights ? 1f : 0f);
            KeepLocalBody();
            if (visorPlane != null && visorPlane.enabled == HideVisorPlane)
                visorPlane.enabled = !HideVisorPlane;
            if (now >= nextBudget)
            {
                nextBudget = now + 0.25f;
                Budget();
            }
        }

        /// <summary>After the game's LateUpdate (DefaultExecutionOrder), which decides whether the body cam's picture shows</summary>
        private void LateUpdate()
        {
            if (!IdleBodyCamOff) return;
            foreach (var monitor in monitors)
                if (monitor != null && monitor.headMountedCam.enabled && !monitor.headMountedCamUI.enabled)
                    monitor.headMountedCam.enabled = false;
        }

        /// <summary>FindObjectsOfType walks every object: every 2 s</summary>
        private void Scan()
        {
            foreach (var light in FindObjectsOfType<Light>())
                if (light.type != LightType.Directional && !wanted.ContainsKey(light))
                {
                    wanted[light] = light.shadows;
                    applied[light] = light.shadows;
                }
            if (!DrawGrass)
                foreach (var terrain in Terrain.activeTerrains)
                    if (terrain.detailObjectDistance > 0f) terrain.detailObjectDistance = 0f;
            foreach (var group in FindObjectsOfType<LODGroup>())
                if (group.lodCount > 1 && !lod0.ContainsKey(group))
                {
                    lod0[group] = group.GetLODs()[0].screenRelativeTransitionHeight;
                    ApplyReach(group);
                    foreach (var lod in group.GetLODs())
                        foreach (var renderer in lod.renderers)
                            if (renderer != null)
                                foreach (var material in renderer.sharedMaterials)
                                    if (material != null && !leaves.ContainsKey(material) && material.shader.name == "Universal Render Pipeline/Lit" &&
                                        material.HasFloat("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
                                    {
                                        leaves[material] = material.shader;
                                        ApplyLeaves(material);
                                    }
                }
            var time = TimeOfDay.Instance;
            if (time != null && time.effects != null)
                foreach (var effect in time.effects)
                    if (effect.effectObject != null)
                        // The drops; their splashes are sub-emitters, as many as the drops that land
                        foreach (var drops in effect.effectObject.GetComponentsInChildren<ParticleSystem>(true))
                            if (!rain.ContainsKey(drops) && drops.TryGetComponent<ParticleSystemRenderer>(out var renderer) &&
                                renderer.renderMode == ParticleSystemRenderMode.Stretch)
                            {
                                rain[drops] = (drops.emission.rateOverTimeMultiplier, drops.main.maxParticles,
                                    renderer.maxParticleSize);
                                ApplyRain(drops);
                            }
            monitors.Clear();
            monitors.AddRange(FindObjectsOfType<ManualCameraRenderer>().Where(m => m.headMountedCam != null && m.headMountedCamUI != null));
            if (visorPlane == null)
                foreach (var renderer in FindObjectsOfType<MeshRenderer>())
                    if (renderer.name == "Plane" && renderer.transform.parent != null &&
                        renderer.transform.parent.name == "PlayerHUDHelmetModel")
                        visorPlane = renderer;
        }

        /// <summary>The height LOD0 needs on screen is inversely its reach</summary>
        private void ApplyReach(LODGroup group)
        {
            var lods = group.GetLODs();
            lods[0].screenRelativeTransitionHeight = Mathf.Min(1f, lod0[group] / Mathf.Max(0.01f, appliedReach));
            group.SetLODs(lods);
        }

        private void ApplyLeaves(Material material)
        {
            if (leavesShader == null) leavesShader = Resources.Load<Material>("LCQuestLeaves")?.shader;
            if (leavesShader == null) return;
            var queue = material.renderQueue;
            material.shader = appliedLeaves ? leavesShader : leaves[material];
            material.renderQueue = queue;
        }

        private void ApplyRain(ParticleSystem drops)
        {
            var (rate, max, size) = rain[drops];
            var emission = drops.emission;
            emission.rateOverTimeMultiplier = rate * appliedDensity;
            var main = drops.main;
            main.maxParticles = Mathf.Max(1, Mathf.CeilToInt(max * appliedDensity));
            drops.GetComponent<ParticleSystemRenderer>().maxParticleSize = Mathf.Min(size, appliedRainSize);
        }

        /// <summary>The local player's body: its first level is the game's shadow-only model, the later ones are drawn
        /// (the flat game's camera never gets them, a headset's wider view at a lower bias does: a black box at the
        /// feet). Held at the first</summary>
        private void KeepLocalBody()
        {
            var round = StartOfRound.Instance;
            var player = round != null ? round.localPlayerController : null;
            var body = player != null && player.thisPlayerModel != null ? player.thisPlayerModel.GetComponentInParent<LODGroup>() : null;
            if (body == localBody) return;
            if (localBody != null) localBody.ForceLOD(-1);
            if (body != null) body.ForceLOD(0);
            localBody = body;
        }

        private static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

        private void Budget()
        {
            var round = StartOfRound.Instance;
            var camera = round != null && round.activeCamera != null ? round.activeCamera : Camera.main;
            if (camera == null) return;
            var eye = camera.transform.position;

            dead.Clear();
            candidates.Clear();
            foreach (var pair in wanted)
            {
                var light = pair.Key;
                if (light == null) { dead.Add(light); continue; }
                // The game switching a light's shadows itself (flashlights) is what it wants from then on
                if (light.shadows != applied[light]) wanted[light] = light.shadows;
                if (wanted[light] == LightShadows.None || !light.isActiveAndEnabled || light.intensity <= 0f) continue;
                if (NoShadows.Count > 0)
                {
                    if (!paths.TryGetValue(light, out var path)) paths[light] = path = PathOf(light.transform);
                    if (NoShadows.Any(path.Contains)) continue;
                }
                // How much it lights around the camera: nothing beyond its range
                var reach = 1f - Vector3.Distance(light.transform.position, eye) / (light.range + 2f);
                if (reach > 0f) candidates.Add((light, light.intensity * reach * reach));
            }
            foreach (var light in dead) { wanted.Remove(light); applied.Remove(light); }
            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            var spent = 0f;
            var keep = new HashSet<Light>();
            foreach (var (light, _) in candidates)
            {
                var cost = light.type == LightType.Point ? PointShadowCost : Mathf.Max(1f, light.range * light.range / 100f);
                if (spent + cost > MaxShadowCost) continue;
                spent += cost;
                keep.Add(light);
            }
            foreach (var pair in wanted)
            {
                var light = pair.Key;
                var shadows = keep.Contains(light) ? pair.Value : LightShadows.None;
                if (light.shadows != shadows) light.shadows = shadows;
                applied[light] = shadows;
            }
        }
    }
}

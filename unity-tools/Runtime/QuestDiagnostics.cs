using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LCQuest
{
    /// <summary>
    /// On-device snapshot and remote control for testing on the Quest, where adb screencap sees nothing. Idle until
    /// asked through files in persistentDataPath (/sdcard/Android/data/io.lcvr.quest/files; scripts/quest-diag.sh):
    ///   diag-request  -> diag/&lt;time&gt;/screen.png, rt-*.png (cameras' render textures), report.txt (scenes, cameras,
    ///                    the clickable buttons with their labels)
    ///   ui-click      -> clicks the active button whose name, path or label contains the file's text
    ///   invoke        -> "Type.Method": calls the parameterless method on the first live object of that type
    ///                    (e.g. StartMatchLever.StartGame: land on the routed moon without pulling the lever);
    ///                    "Type.?" logs the fields of that object instead
    ///   tp            -> "x y z [yaw]" teleports the local player ("ship" / "outside" / "entrance" for landmarks;
    ///                    "enter" / "exit" go through the facility's main door as a player does; "node 0.5": an AI
    ///                    node inside the facility, by fraction of the list or index; the yaw turns the VR view too;
    ///                    "look &lt;yaw&gt;" only turns the view)
    ///   hide          -> switches off (or back on) the renderers whose path contains the file's text
    /// </summary>
    public class QuestDiagnostics : MonoBehaviour
    {
        private string requestPath, clickPath, invokePath, tpPath, hidePath, outputRoot;
        private string pendingClick;
        /// <summary>Renders per camera since the last report: the ship's monitors switch theirs on only on the frames they
        /// draw, so Camera.allCameras rarely lists them</summary>
        private readonly Dictionary<Camera, int> renders = new Dictionary<Camera, int>();
        private float rendersSince;
        private float pendingSince;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject("LCQuest Diagnostics");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<QuestDiagnostics>();
        }

        private void Awake()
        {
            requestPath = Path.Combine(Application.persistentDataPath, "diag-request");
            clickPath = Path.Combine(Application.persistentDataPath, "ui-click");
            invokePath = Path.Combine(Application.persistentDataPath, "invoke");
            tpPath = Path.Combine(Application.persistentDataPath, "tp");
            hidePath = Path.Combine(Application.persistentDataPath, "hide");
            outputRoot = Path.Combine(Application.persistentDataPath, "diag");
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += (_, camera) =>
                renders[camera] = renders.TryGetValue(camera, out var count) ? count + 1 : 1;
            StartCoroutine(Poll());
        }

        private IEnumerator Poll()
        {
            var wait = new WaitForSecondsRealtime(0.5f);
            while (true)
            {
                yield return wait;
                // A click waits up to 30 s for its button (scenes load slowly); the file goes once it is done
                if (File.Exists(clickPath))
                {
                    var text = File.ReadAllText(clickPath).Trim();
                    if (text != pendingClick) { pendingClick = text; pendingSince = Time.realtimeSinceStartup; }
                    if (Click(text, Time.realtimeSinceStartup - pendingSince > 30f))
                    {
                        File.Delete(clickPath);
                        pendingClick = null;
                    }
                }
                if (File.Exists(invokePath))
                {
                    var text = File.ReadAllText(invokePath).Trim();
                    File.Delete(invokePath);
                    Invoke(text);
                }
                if (File.Exists(tpPath))
                {
                    var text = File.ReadAllText(tpPath).Trim();
                    File.Delete(tpPath);
                    Teleport(text);
                }
                if (File.Exists(hidePath))
                {
                    var text = File.ReadAllText(hidePath).Trim();
                    File.Delete(hidePath);
                    foreach (var r in FindObjectsOfType<Renderer>().Where(r => PathOf(r.transform).Contains(text)))
                    {
                        r.enabled = !r.enabled;
                        Debug.Log($"[QuestDiagnostics] {PathOf(r.transform)} {(r.enabled ? "shown" : "hidden")}");
                    }
                }
                if (File.Exists(requestPath))
                {
                    File.Delete(requestPath);
                    yield return Snapshot();
                }
            }
        }

        private static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

        private static string Label(Component c)
        {
            var tmp = c.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null) return tmp.text;
            var legacy = c.GetComponentInChildren<Text>(true);
            return legacy != null ? legacy.text : "";
        }

        /// <summary>True when done: clicked, or given up (<paramref name="last"/>)</summary>
        private static bool Click(string what, bool last)
        {
            var buttons = FindObjectsOfType<Button>().Where(b => b.isActiveAndEnabled && b.interactable).ToList();
            var match = buttons.FirstOrDefault(b => b.name.Equals(what, StringComparison.OrdinalIgnoreCase))
                        ?? buttons.FirstOrDefault(b => PathOf(b.transform).IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? buttons.FirstOrDefault(b => Label(b).IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match == null)
            {
                if (last) Debug.Log($"[QuestDiagnostics] no active button matches '{what}'");
                return last;
            }
            Debug.Log($"[QuestDiagnostics] click {PathOf(match.transform)} ('{Label(match).Trim()}')");
            match.onClick.Invoke();
            return true;
        }

        /// <summary>"Type.Method [args]": the first live object of that type (or a static method), arguments parsed to
        /// the method's parameter types (int, float, bool, string), matched by name and argument count</summary>
        private static void Invoke(string what)
        {
            var words = what.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var head = words.Length > 0 ? words[0] : "";
            var args = words.Skip(1).ToArray();
            var dot = head.LastIndexOf('.');
            var typeName = dot > 0 ? head.Substring(0, dot) : head;
            var methodName = dot > 0 ? head.Substring(dot + 1) : "";
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);
            var info = type?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == args.Length);
            var target = type != null && typeof(UnityEngine.Object).IsAssignableFrom(type) ? FindObjectOfType(type) : null;
            if (methodName == "?" && type != null)
            {
                Dump(type, target);
                return;
            }
            if (info == null || (!info.IsStatic && target == null))
            {
                Debug.Log($"[QuestDiagnostics] cannot invoke '{what}' (type {(type != null ? "found" : "missing")}, object {(target != null ? "found" : "missing")})");
                return;
            }
            var values = info.GetParameters().Select((p, i) => Convert.ChangeType(args[i], p.ParameterType, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Debug.Log($"[QuestDiagnostics] invoke {type.FullName}.{methodName}({string.Join(", ", args)})");
            try { info.Invoke(info.IsStatic ? null : target, values); }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>"Type.?": the fields of the first live object of that type (Unity objects: destroyed ones show null)</summary>
        private static void Dump(Type type, object target)
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
            var lines = new List<string>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
            foreach (var field in t.GetFields(flags | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (!field.IsStatic && target == null) continue;
                object value;
                try { value = field.GetValue(field.IsStatic ? null : target); }
                catch (Exception e) { value = e.GetType().Name; }
                var shown = value is UnityEngine.Object unityObject ? (unityObject != null ? unityObject.name : "null (Unity)") : value ?? "null";
                lines.Add($"  {field.Name} = {shown}");
            }
            Debug.Log($"[QuestDiagnostics] {type.FullName} ({(target != null ? "object found" : "statics only")}):\n{string.Join("\n", lines)}");
        }

        /// <summary>"QuestDiagnostics.Spawn &lt;enemy&gt; [metres]" through invoke: an enemy whose name contains the text
        /// ("_" for spaces: Hoarding_bug) in front of the local player, who can no longer die (the camera would go to
        /// spectating)</summary>
        public static void Spawn(string name, float distance)
        {
            var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            var type = Resources.FindObjectsOfTypeAll<EnemyType>()
                .FirstOrDefault(t => t.enemyName != null && t.enemyName.IndexOf(name.Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0);
            if (player == null || type == null || RoundManager.Instance == null)
            {
                Debug.Log($"[QuestDiagnostics] cannot spawn '{name}' (enemy types: {string.Join(", ", Resources.FindObjectsOfTypeAll<EnemyType>().Select(t => t.enemyName))})");
                return;
            }
            StartOfRound.Instance.allowLocalPlayerDeath = false;
            var forward = Vector3.ProjectOnPlane(player.gameplayCamera.transform.forward, Vector3.up).normalized;
            var position = player.transform.position + forward * distance;
            if (UnityEngine.AI.NavMesh.SamplePosition(position, out var hit, 5f, UnityEngine.AI.NavMesh.AllAreas)) position = hit.position;
            RoundManager.Instance.SpawnEnemyGameObject(position, player.transform.eulerAngles.y + 180f, -1, type);
            Debug.Log($"[QuestDiagnostics] spawned {type.enemyName} at {position}");
        }

        public static void Spawn(string name) => Spawn(name, 4f);

        /// <summary>"QuestDiagnostics.FixSeed &lt;n&gt;" before landing: the same facility layout every run, so measurements
        /// at its AI nodes compare</summary>
        public static void FixSeed(int seed)
        {
            StartOfRound.Instance.overrideRandomSeed = true;
            StartOfRound.Instance.overrideSeedNumber = seed;
            Debug.Log($"[QuestDiagnostics] map seed fixed at {seed}");
        }

        /// <summary>"QuestDiagnostics.Interior &lt;n&gt;" before landing: the routed moon generates that interior
        /// (RoundManager.dungeonFlowTypes: 0 factory, 1 manor, 4 mineshaft); logs the flows to pick from</summary>
        public static void Interior(int id)
        {
            var flows = RoundManager.Instance.dungeonFlowTypes;
            Debug.Log("[QuestDiagnostics] interiors: " + string.Join(", ", flows.Select((f, i) => $"{i} {f.dungeonFlow?.name}")));
            var level = StartOfRound.Instance.currentLevel;
            level.dungeonFlowTypes = new[] { new IntWithRarity(id, 1, null) };
            Debug.Log($"[QuestDiagnostics] interior on {level.PlanetName}: {flows[id].dungeonFlow?.name}");
        }

        /// <summary>"QuestDiagnostics.Weather &lt;n&gt;" before landing: the routed moon's weather (LevelWeatherType: -1 none,
        /// 0 dust clouds, 1 rainy, 2 stormy, 3 foggy, 4 flooded, 5 eclipsed)</summary>
        public static void Weather(int weather)
        {
            StartOfRound.Instance.currentLevel.currentWeather = (LevelWeatherType)weather;
            Debug.Log($"[QuestDiagnostics] weather on {StartOfRound.Instance.currentLevel.PlanetName}: {(LevelWeatherType)weather}");
        }

        private static readonly List<Renderer> keptOffRenderers = new List<Renderer>();
        private static readonly List<Light> keptOffLights = new List<Light>();

        /// <summary>"QuestDiagnostics.KeepTiles &lt;n&gt;": draws only the facility tile the camera is in and the tiles
        /// up to n doorways from it, their lights too (a ceiling for culling rooms through doorways, #9); -1 draws all
        /// again</summary>
        public static void KeepTiles(int depth)
        {
            foreach (var renderer in keptOffRenderers) if (renderer != null) renderer.forceRenderingOff = false;
            foreach (var light in keptOffLights) if (light != null) light.enabled = true;
            keptOffRenderers.Clear();
            keptOffLights.Clear();
            if (depth < 0) { Debug.Log("[QuestDiagnostics] every tile drawn"); return; }

            var tiles = FindObjectsOfType<DunGen.Tile>();
            var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (tiles.Length == 0 || player == null) { Debug.Log("[QuestDiagnostics] no facility tiles"); return; }
            var eye = player.gameplayCamera.transform.position;
            var start = tiles.FirstOrDefault(t => t.Bounds.Contains(eye))
                        ?? tiles.OrderBy(t => t.Bounds.SqrDistance(eye)).First();
            var kept = new HashSet<DunGen.Tile> { start };
            var frontier = new List<DunGen.Tile> { start };
            for (var step = 0; step < depth; step++)
                frontier = frontier.SelectMany(t => t.UsedDoorways)
                    .Select(d => d.ConnectedDoorway != null ? d.ConnectedDoorway.Tile : null)
                    .Where(t => t != null && kept.Add(t)).ToList();
            foreach (var tile in tiles.Where(t => !kept.Contains(t)))
            {
                foreach (var renderer in tile.GetComponentsInChildren<Renderer>())
                    if (!renderer.forceRenderingOff) { renderer.forceRenderingOff = true; keptOffRenderers.Add(renderer); }
                foreach (var light in tile.GetComponentsInChildren<Light>())
                    if (light.enabled) { light.enabled = false; keptOffLights.Add(light); }
            }
            Debug.Log($"[QuestDiagnostics] kept {kept.Count} of {tiles.Length} tiles around {start.name} ({depth} doorways): " +
                      $"{keptOffRenderers.Count} renderers, {keptOffLights.Count} lights off");
        }

        private static void Teleport(string what)
        {
            var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (player == null) { Debug.Log("[QuestDiagnostics] no local player"); return; }
            var parts = what.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Vector3 position;
            var yaw = player.transform.eulerAngles.y;
            var round = StartOfRound.Instance;
            switch (parts.Length > 0 ? parts[0] : "")
            {
                case "ship": position = round.playerSpawnPositions[0].position; break;
                case "outside": position = round.outsideShipSpawnPosition.position; break;
                case "enter":
                case "exit":
                    // Through the main door as a player goes: the game sets "inside the facility" and its effects
                    var door = FindObjectsOfType<EntranceTeleport>()
                        .FirstOrDefault(e => e.entranceId == 0 && e.isEntranceToBuilding == (parts[0] == "enter"));
                    if (door == null) { Debug.Log("[QuestDiagnostics] no main entrance"); return; }
                    door.TeleportPlayer();
                    Debug.Log($"[QuestDiagnostics] went {parts[0]} through the main entrance");
                    return;
                case "node":
                    // The facility's AI nodes, by index or by fraction of the list ("node 0.5"): rooms to measure in
                    var nodes = RoundManager.Instance != null ? RoundManager.Instance.insideAINodes : null;
                    if (nodes == null || nodes.Length == 0 || parts.Length < 2) { Debug.Log("[QuestDiagnostics] no inside nodes"); return; }
                    var at = parts[1].Contains(".")
                        ? Mathf.Clamp((int)(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * nodes.Length), 0, nodes.Length - 1)
                        : int.Parse(parts[1]) % nodes.Length;
                    position = nodes[at].transform.position;
                    parts = parts.Length > 2 ? new[] { "node", parts[2] } : new[] { "node" };
                    break;
                case "look":
                    // Only the view: turns where the headset looks (yaw in degrees, world)
                    if (parts.Length < 2) { Debug.Log("[QuestDiagnostics] look <yaw>?"); return; }
                    TurnView(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                    return;
                case "entrance":
                    var entrance = FindObjectsOfType<EntranceTeleport>().FirstOrDefault(e => !e.isEntranceToBuilding) ??
                                   FindObjectsOfType<EntranceTeleport>().FirstOrDefault();
                    if (entrance == null) { Debug.Log("[QuestDiagnostics] no entrance"); return; }
                    position = entrance.transform.position;
                    break;
                default:
                    if (parts.Length < 3) { Debug.Log($"[QuestDiagnostics] tp: '{what}'?"); return; }
                    position = new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
                    break;
            }
            var turn = parts.Length == 2 || parts.Length == 4;
            if (turn) yaw = float.Parse(parts[parts.Length - 1], System.Globalization.CultureInfo.InvariantCulture);
            player.TeleportPlayer(position, true, yaw);
            if (turn) TurnView(yaw);
            Debug.Log($"[QuestDiagnostics] teleported to {position} yaw {yaw}");
        }

        /// <summary>In VR the player faces where the headset looks: LCVR's turning offset is set so that the headset's
        /// current direction becomes this world yaw (as LCVR's own EntranceTeleport patch does), which turns a headset
        /// nobody wears towards a view to measure. Pitch stays the headset's</summary>
        private static void TurnView(float yaw)
        {
            var session = Type.GetType("LCVR.Managers.VRSession, LCVR")?.GetProperty("Instance")?.GetValue(null);
            var player = session?.GetType().GetProperty("LocalPlayer")?.GetValue(session);
            var turning = player?.GetType().GetProperty("TurningProvider")?.GetValue(player);
            var camera = session?.GetType().GetProperty("MainCamera")?.GetValue(session) as Camera;
            if (turning == null || camera == null) { Debug.Log("[QuestDiagnostics] look: no VR player"); return; }
            var head = camera.transform.parent;
            var origin = head.parent;
            var parentYaw = origin.parent != null ? origin.parent.eulerAngles.y : 0f;
            turning.GetType().GetMethod("SetOffset")?.Invoke(turning, new object[] { yaw - parentYaw - head.localEulerAngles.y });
            Debug.Log($"[QuestDiagnostics] view turned to yaw {yaw}");
        }

        private static void RenderFlat(Camera camera, string path)
        {
            var rt = RenderTexture.GetTemporary(1024, 1024, 24, RenderTextureFormat.ARGB32);
            var target = camera.stereoTargetEye;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.targetTexture = rt;
            camera.Render();
            camera.targetTexture = null;
            camera.stereoTargetEye = target;
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var flat = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            flat.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(path, flat.EncodeToPNG());
            Destroy(flat);
        }

        private IEnumerator Snapshot()
        {
            var dir = Path.Combine(outputRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            yield return new WaitForEndOfFrame();
            var screen = ScreenCapture.CaptureScreenshotAsTexture();
            // The camera clears to alpha 0; the display ignores alpha, a PNG viewer does not
            var pixels = screen.GetPixels32();
            for (var i = 0; i < pixels.Length; i++) pixels[i].a = 255;
            screen.SetPixels32(pixels);
            File.WriteAllBytes(Path.Combine(dir, "screen.png"), screen.EncodeToPNG());
            // In VR the screen is black: the headset's camera drawn once more, flat, into a texture
            var vr = Camera.allCameras.FirstOrDefault(c => c.targetTexture == null && c.stereoEnabled);
            if (vr != null)
            {
                RenderFlat(vr, Path.Combine(dir, $"flat-{vr.name}.png"));
                // Over grey: tells a picture that blends with what is behind from one that is cut away
                var (flags, background) = (vr.clearFlags, vr.backgroundColor);
                vr.clearFlags = CameraClearFlags.SolidColor;
                vr.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
                RenderFlat(vr, Path.Combine(dir, $"flat-{vr.name}-grey.png"));
                (vr.clearFlags, vr.backgroundColor) = (flags, background);
            }
            Destroy(screen);

            var report = new StringBuilder();
            FrameTimingManager.CaptureFrameTimings();
            var timings = new FrameTiming[30];
            var count = (int)FrameTimingManager.GetLatestTimings((uint)timings.Length, timings);
            if (count > 0)
            {
                double cpu = 0, mainThread = 0, render = 0, gpu = 0;
                for (var i = 0; i < count; i++)
                {
                    cpu += timings[i].cpuFrameTime; mainThread += timings[i].cpuMainThreadFrameTime;
                    render += timings[i].cpuRenderThreadFrameTime; gpu += timings[i].gpuFrameTime;
                }
                report.AppendLine($"frame timings ({count}): cpu {cpu / count:F1} ms (main {mainThread / count:F1}, render {render / count:F1}), gpu {gpu / count:F1} ms");
            }
            report.AppendLine($"time {Time.realtimeSinceStartup:F1}s, fps {1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):F0}, screen {Screen.width}x{Screen.height}");
            var player = GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;
            if (player != null)
                report.AppendLine($"player {player.transform.position} yaw {player.transform.eulerAngles.y:F0} inside {player.isInsideFactory}");
            var round = StartOfRound.Instance;
            if (round != null && round.levels != null)
                report.AppendLine($"level {round.currentLevelID} of [{string.Join(", ", round.levels.Select((l, i) => $"{i}:{l.PlanetName}"))}]");
            report.AppendLine($"fog {RenderSettings.fog} density {RenderSettings.fogDensity:F4} color {RenderSettings.fogColor} sky {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "-")}");
            var main = Camera.allCameras.FirstOrDefault(c => c.name == "MainCamera");
            if (main != null)
            {
                var on = FindObjectsOfType<Light>().Where(l => l.isActiveAndEnabled && l.type != LightType.Directional).ToList();
                report.AppendLine($"lights on {on.Count}, reaching the camera {on.Count(l => Vector3.Distance(l.transform.position, main.transform.position) < l.range)}, " +
                                  $"within 30 m {on.Count(l => Vector3.Distance(l.transform.position, main.transform.position) < 30f)}");
            }
            if (main != null)
                foreach (var light in FindObjectsOfType<Light>().Where(l => l.isActiveAndEnabled && l.type != LightType.Directional)
                             .OrderBy(l => Vector3.Distance(l.transform.position, main.transform.position)).Take(16))
                    report.AppendLine($"light {PathOf(light.transform)} {light.type} I {light.intensity:F3} range {light.range:F1} " +
                                      $"dist {Vector3.Distance(light.transform.position, main.transform.position):F1} shadows {light.shadows} layers {light.renderingLayerMask}");
            foreach (var applied in HdrpBridge.LastApplied)
                report.AppendLine($"bridge {applied.Key}: {applied.Value}");
            foreach (var passes in CustomPassFeature.LastPasses)
                report.AppendLine($"passes {passes.Key}: {passes.Value}");
            var stack = UnityEngine.Rendering.VolumeManager.instance.stack;
            var urpColor = stack?.GetComponent<UnityEngine.Rendering.Universal.ColorAdjustments>();
            if (urpColor != null) report.AppendLine($"urp stack: exposure {urpColor.postExposure.value:F2} active {urpColor.active}");
            for (var i = 0; i < SceneManager.sceneCount; i++)
                report.AppendLine($"scene {SceneManager.GetSceneAt(i).name}");
            var n = 0;
            foreach (var camera in Camera.allCameras)
            {
                var rt = camera.targetTexture;
                var e = camera.transform.eulerAngles;
                report.AppendLine($"pose {camera.name} | {camera.transform.position.x:F3} {camera.transform.position.y:F3} {camera.transform.position.z:F3} | {e.x:F1} {e.y:F1}");
                report.AppendLine($"camera {PathOf(camera.transform)} depth {camera.depth} mask {camera.cullingMask:X} " +
                                  (rt != null ? $"-> {rt.name} {rt.width}x{rt.height} {rt.graphicsFormat}" : "-> screen") +
                                  $" clear {camera.clearFlags} {camera.backgroundColor}");
                if (rt == null) continue;
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                RenderTexture.active = previous;
                File.WriteAllBytes(Path.Combine(dir, $"rt-{n++}-{camera.name}.png"), tex.EncodeToPNG());
                Destroy(tex);
            }
            var seconds = Time.realtimeSinceStartup - rendersSince;
            foreach (var pair in renders.Where(p => p.Key != null).OrderByDescending(p => p.Value))
            {
                var rt = pair.Key.targetTexture;
                report.AppendLine($"renders {PathOf(pair.Key.transform)} {pair.Value / seconds:F1}/s over {seconds:F0} s " +
                                  (rt != null ? $"-> {rt.name} {rt.width}x{rt.height}" : "-> screen") + $" mask {pair.Key.cullingMask:X}");
            }
            renders.Clear();
            rendersSince = Time.realtimeSinceStartup;
            foreach (var g in FindObjectsOfType<Graphic>().Where(g => g.isActiveAndEnabled && !(g is TMP_Text) && !(g is Text)))
            {
                var size = g.rectTransform.rect.size;
                report.AppendLine($"graphic {PathOf(g.transform)} {g.GetType().Name} color {g.color} " +
                                  $"tex {(g.mainTexture != null ? g.mainTexture.name : "-")} mat {g.materialForRendering?.shader?.name} {size.x:F0}x{size.y:F0}");
            }
            // Texts within 5 m of the player's view (world-space HUD in VR), with the shader they draw with
            var eye = round != null && round.activeCamera != null ? round.activeCamera.transform.position : Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            foreach (var c in FindObjectsOfType<Canvas>().Where(c => c.isActiveAndEnabled && c.isRootCanvas))
                report.AppendLine($"canvas {PathOf(c.transform)} {c.renderMode} camera {(c.worldCamera != null ? c.worldCamera.name : "-")} " +
                                  $"at {c.transform.position} scale {c.transform.lossyScale.x:F4} layer {c.gameObject.layer} " +
                                  $"order {c.sortingLayerName}/{c.sortingOrder} {Vector3.Distance(c.transform.position, eye):F2} m");
            foreach (var t in FindObjectsOfType<TMP_Text>().Where(t => t.isActiveAndEnabled && Vector3.Distance(t.transform.position, eye) < 5f))
            {
                var text = t.text ?? "";
                report.AppendLine($"text {PathOf(t.transform)} '{text.Substring(0, Math.Min(40, text.Length)).Replace('\n', ' ')}' " +
                                  $"shader {t.fontMaterial?.shader?.name} ({(t.fontMaterial != null && t.fontMaterial.shader != null && t.fontMaterial.shader.isSupported ? "ok" : "unsupported")}) " +
                                  $"font {t.font?.name} {Vector3.Distance(t.transform.position, eye):F2} m");
            }
            // Renderers around the view (few in the menus): what draws over what
            var near = FindObjectsOfType<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleSystemRenderer) &&
                                                                Vector3.Distance(r.bounds.center, eye) < 10f).Take(60);
            foreach (var r in near)
                report.AppendLine($"renderer {PathOf(r.transform)} {string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m => $"{m.name}:{m.shader.name}@{m.renderQueue}"))} " +
                                  $"bounds {r.bounds.center} {r.bounds.extents.magnitude:F1} {Vector3.Distance(r.bounds.center, eye):F2} m");
            // Memory by asset type, and the largest assets (development builds measure them)
            foreach (var type in new[] { typeof(Texture2D), typeof(Cubemap), typeof(Texture2DArray), typeof(RenderTexture), typeof(Mesh), typeof(AudioClip), typeof(AnimationClip), typeof(Shader), typeof(Font), typeof(TerrainData), typeof(Material) })
            {
                var assets = Resources.FindObjectsOfTypeAll(type);
                var sizes = assets.Select(a => (asset: a, size: UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(a))).OrderByDescending(x => x.size).ToList();
                report.AppendLine($"memory {type.Name}: {sizes.Count} assets, {sizes.Sum(x => x.size) / 1048576f:F0} MB");
                foreach (var (asset, size) in sizes.Take(12))
                    report.AppendLine($"  {size / 1048576f:F1} MB {asset.name}" + (asset is RenderTexture r ? $" {r.width}x{r.height} {r.graphicsFormat} {r.dimension} slices {r.volumeDepth} depth {r.depthStencilFormat} msaa {r.antiAliasing} vr {r.vrUsage} created {r.IsCreated()}" : asset is Texture t ? $" {t.width}x{t.height} {t.graphicsFormat} mips {t.mipmapCount}" : asset is Mesh m ? $" {m.vertexCount} verts readable {m.isReadable}" : asset is AudioClip c ? $" {c.length:F0} s {c.loadType}" : ""));
            }
            foreach (var b in FindObjectsOfType<Button>().Where(b => b.isActiveAndEnabled))
                report.AppendLine($"button {PathOf(b.transform)} '{Label(b).Trim()}' {(b.interactable ? "" : "(disabled)")}");
            File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
            Debug.Log($"[QuestDiagnostics] snapshot {dir}");
        }
    }
}

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LCQuest.Editor
{
    /// <summary>
    /// Reference images from the HDRP project (scripts/make-reference.sh): what the PC game renders at a given spot,
    /// to compare with the Quest build's snapshot of the same spot (scripts/quest-diag.sh).
    ///   -executeMethod LCQuest.Editor.ReferenceRender.Run -views &lt;file&gt; -out &lt;dir&gt;
    /// Views file, one per line: name | scenes (comma-separated, the first is the ship scene) | x y z | pitch yaw
    ///   [| extras]: semicolon-separated "prefab=Assets/...prefab@x y z yaw" (a dungeon room where the game builds the
    ///   facility) and "volume=GameObjectName:weight" (volumes the game drives from code, e.g. BlackSkyVolume indoors)
    /// The game's own player camera (SampleSceneRelay's MainCamera, with its HDRP settings and volume layer mask) is
    /// moved there and rendered at the game's 860x520 for 60 frames, so auto exposure and volumetric fog settle.
    /// The same script renders the URP project (scripts/urp-render.sh) for A/B images: there it also brings up
    /// LCQuest.HdrpBridge, which in the game starts itself in play mode.
    /// </summary>
    public static class ReferenceRender
    {
        public static void Run()
        {
            var views = File.ReadAllLines(Argument("-views")).Where(l => l.Trim().Length > 0 && !l.StartsWith("#"));
            var output = Argument("-out");
            // Shader variants compile asynchronously in the editor: the first frames of a view would use stand-ins
            ShaderUtil.allowAsyncCompilation = false;
            Directory.CreateDirectory(output);
            // Twice: the first pass of a view in a session renders differently (shader variants, fog keywords, first-time
            // resources), only the second is kept
            foreach (var line in views.ToList())
            {
                try { Render(line, null); Render(line, output); }
                catch (Exception e) { Debug.LogError($"[ReferenceRender] {line}: {e}"); }
            }
            EditorApplication.Exit(0);
        }

        private static void Render(string line, string output)
        {
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var name = parts[0];
            var scenes = parts[1].Split(',').Select(s => s.Trim()).ToArray();
            var autoPosition = parts[2] == "auto";
            var position = autoPosition ? Vector3.zero : Vector(parts[2]);
            GameObject placed = null;
            var angles = Vector(parts[3]);

            for (var i = 0; i < scenes.Length; i++)
                EditorSceneManager.OpenScene($"Assets/Scenes/{scenes[i]}.unity", i == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive);
            if (scenes.Length > 1)
                SceneManager.SetActiveScene(SceneManager.GetSceneByName(scenes[scenes.Length - 1]));

            if (parts.Length > 4)
                foreach (var extra in parts[4].Split(';').Select(e => e.Trim()).Where(e => e.Length > 0))
                {
                    var kv = extra.Split(new[] { '=' }, 2);
                    if (kv[0] == "prefab")
                    {
                        var at = kv[1].Split('@');
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(at[0]);
                        var pose = at.Length > 1 ? at[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray() : new float[4];
                        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());
                        placed = instance;
                        instance.transform.SetPositionAndRotation(new Vector3(pose[0], pose[1], pose[2]),
                            Quaternion.Euler(0, pose.Length > 3 ? pose[3] : 0, 0));
                    }
                    else if (kv[0] == "off")
                    {
                        foreach (var go in UnityEngine.Object.FindObjectsOfType<Transform>(true).Where(t => t.name == kv[1]))
                            go.gameObject.SetActive(false);
                    }
                    else if (kv[0] == "set")
                    {
                        // set=GameObject.Component.member:value (experiments on HDRP light data in the reference)
                        var spec = kv[1].Split(':');
                        var path = spec[0].Split('.');
                        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true).Where(t => t.name == path[0]))
                        {
                            var component = t.GetComponent(path[1]);
                            if (component == null) continue;
                            var property = component.GetType().GetProperty(path[2]);
                            var value = Convert.ChangeType(spec[1], property.PropertyType, CultureInfo.InvariantCulture);
                            property.SetValue(component, value);
                            Debug.Log($"[ReferenceRender] {path[0]}.{path[1]}.{path[2]} = {value}");
                        }
                    }
                    else if (kv[0] == "probe")
                    {
                        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true).Where(t => t.name == kv[1]))
                        {
                            var behaviours = string.Join(",", t.GetComponents<Behaviour>().Select(b => $"{b.GetType().Name}:{b.enabled}"));
                            var chain = string.Join("/", Enumerable.Repeat(t, 1).Concat(Ancestors(t)).Select(a => $"{a.name}{(a.gameObject.activeSelf ? "" : "(off)")}"));
                            Debug.Log($"[ReferenceRender] probe {kv[1]}: inHierarchy {t.gameObject.activeInHierarchy} pos {t.position} rot {t.eulerAngles} scale {t.lossyScale} [{behaviours}] {chain}");
                        }
                    }
                    else if (kv[0] == "fogmfp")
                    {
                        // fogmfp=Name:value: a local volumetric fog's mean free path (struct field, set back as a whole)
                        var nv = kv[1].Split(':');
                        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true).Where(t => t.name == nv[0]))
                        {
                            var fog = t.GetComponents<MonoBehaviour>().FirstOrDefault(b => b.GetType().Name == "LocalVolumetricFog");
                            var field = fog?.GetType().GetField("parameters");
                            if (field == null) continue;
                            var parameters = field.GetValue(fog);
                            parameters.GetType().GetField("meanFreePath").SetValue(parameters, float.Parse(nv[1], CultureInfo.InvariantCulture));
                            field.SetValue(fog, parameters);
                            Debug.Log($"[ReferenceRender] {nv[0]} meanFreePath = {nv[1]}");
                        }
                    }
                    else if (kv[0] == "fogvolumetric")
                    {
                        // fogvolumetric=0|1: override HDRP's volumetric fog switch (top-priority volume)
                        var fogType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Rendering.HighDefinition.Fog")).FirstOrDefault(t => t != null);
                        var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
                        var fogComponent = profile.Add(fogType, false);
                        var parameter = (UnityEngine.Rendering.BoolParameter)fogType.GetField("enableVolumetricFog").GetValue(fogComponent);
                        parameter.overrideState = true;
                        parameter.value = kv[1] == "1";
                        var go = new GameObject("Fog override");
                        var volume = go.AddComponent<UnityEngine.Rendering.Volume>();
                        volume.isGlobal = true;
                        volume.priority = 100001;
                        volume.sharedProfile = profile;
                    }
                    else if (kv[0] == "custompass")
                    {
                        // custompass=Name: switch on a custom pass the game enables at runtime (by its m_Name)
                        foreach (var volume in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true).Where(b => b.GetType().Name == "CustomPassVolume"))
                            if (volume.GetType().GetField("customPasses")?.GetValue(volume) is System.Collections.IList passes)
                                foreach (var pass in passes)
                                {
                                    var nameField = pass.GetType().GetField("m_Name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                                                    ?? pass.GetType().BaseType?.GetField("m_Name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                    if (nameField != null && (string)nameField.GetValue(pass) == kv[1])
                                        pass.GetType().GetField("enabled")?.SetValue(pass, true);
                                }
                    }
                    else if (kv[0] == "matfloat")
                    {
                        // matfloat=Material.property:value
                        var spec = kv[1].Split(':');
                        var dot = spec[0].LastIndexOf('.');
                        foreach (var m in Resources.FindObjectsOfTypeAll<Material>().Where(m => m.name == spec[0].Substring(0, dot)))
                            m.SetFloat(spec[0].Substring(dot + 1), float.Parse(spec[1], CultureInfo.InvariantCulture));
                    }
                    else if (kv[0] == "volume")
                    {
                        var nw = kv[1].Split(':');
                        foreach (var volume in UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.Volume>(true).Where(v => v.name == nw[0]))
                            volume.weight = float.Parse(nw[1], CultureInfo.InvariantCulture);
                    }
                }

            // "auto": inside the placed room, at eye height above its floor, in the middle of its renderers
            if (autoPosition && placed != null)
            {
                var renderers = placed.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToArray();
                if (renderers.Length > 0)
                {
                    var bounds = renderers[0].bounds;
                    foreach (var r in renderers) bounds.Encapsulate(r.bounds);
                    position = new Vector3(bounds.center.x, bounds.min.y + 1.6f, bounds.center.z);
                }
            }

            var camera = UnityEngine.Object.FindObjectsOfType<Camera>(true).FirstOrDefault(c => c.name == "MainCamera");
            if (camera == null) throw new InvalidOperationException("no MainCamera (is SampleSceneRelay the first scene?)");
            camera.gameObject.SetActive(true);
            camera.enabled = true;
            for (var t = camera.transform.parent; t != null; t = t.parent) t.gameObject.SetActive(true);
            camera.transform.SetPositionAndRotation(position, Quaternion.Euler(angles.x, angles.y, 0));

            // pick=x,y (viewport): what the camera sees there, for tracking down a wrong material
            if (parts.Length > 4)
                foreach (var extra in parts[4].Split(';').Select(e => e.Trim()).Where(e => e.StartsWith("pick=")))
                {
                    var xy = extra.Substring(5).Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    var ray = camera.ViewportPointToRay(new Vector3(xy[0], xy[1], 0));
                    Physics.SyncTransforms();
                    var hit = UnityEngine.Object.FindObjectsOfType<Renderer>()
                        .Where(r => r.enabled && r.gameObject.activeInHierarchy && r.bounds.IntersectRay(ray, out _))
                        .Select(r => { r.bounds.IntersectRay(ray, out var d); return (r, d); })
                        .OrderBy(x => x.d).Take(4);
                    foreach (var (r, d) in hit)
                        Debug.Log($"[ReferenceRender] pick {xy[0]},{xy[1]}: {r.name} (layer {r.gameObject.layer}) at {d:F1} m, " +
                                  $"materials {string.Join(",", r.sharedMaterials.Select(m => m != null ? $"{m.name}[{m.shader.name}]" : "null"))}");
                }

            var bridge = BridgeForEditMode();
            if (Environment.GetCommandLineArgs().Contains("-neutral")) Neutralize(bridge);
            // -tune "Field=value;...": HdrpBridge's calibration constants, for sweeps without rebuilding
            var tune = Environment.GetCommandLineArgs().SkipWhile(a => a != "-tune").Skip(1).FirstOrDefault();
            if (bridge != null && tune != null)
                foreach (var pair in tune.Split(';').Where(p => p.Contains('=')))
                {
                    var kv2 = pair.Split('=');
                    bridge.GetType().GetField(kv2[0].Trim())?.SetValue(null, float.Parse(kv2[1], CultureInfo.InvariantCulture));
                }

            // -composite: what the player sees, the HUD camera's picture of the player camera's render texture (both
            // cameras' post-processing); otherwise the player camera alone
            var composite = Environment.GetCommandLineArgs().Contains("-composite");
            var ui = composite ? UnityEngine.Object.FindObjectsOfType<Camera>(true).FirstOrDefault(c => c.name == "UICamera") : null;
            if (ui != null) { ui.gameObject.SetActive(true); ui.enabled = true; }
            var rt = ui != null
                ? new RenderTexture(1280, 800, 24, RenderTextureFormat.ARGB32) { name = "Reference" }
                : new RenderTexture(860, 520, 24, RenderTextureFormat.ARGB32) { name = "Reference" };
            var shot = ui != null ? ui : camera;
            var previous = shot.targetTexture;
            if (ui == null) camera.targetTexture = rt;
            for (var frame = 0; frame < 60; frame++)
            {
                if (bridge != null) bridge.SendMessage("LateUpdate");
                // Components that prepare their GPU data in Update (HDRP's local volumetric fog) get no Update in a
                // batch-mode editor: call it, or the reference has none of the game's local fog
                foreach (var behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                    if (behaviour.GetType().Name == "LocalVolumetricFog")
                        behaviour.SendMessage("Update", SendMessageOptions.DontRequireReceiver);
                camera.Render();
                if (ui != null)
                {
                    ui.targetTexture = rt;
                    ui.Render();
                }
            }
            camera = shot;
            RenderTexture.active = rt;
            var image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = null;
            camera.targetTexture = previous;
            if (output == null) return;
            File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
            Debug.Log($"[ReferenceRender] {name}: {string.Join(",", scenes)} at {position} {angles}");
            if (bridge != null && bridge.GetType().GetField("LastApplied")?.GetValue(null) is System.Collections.IDictionary applied)
                foreach (var key in new[] { "MainCamera", "UICamera" })
                    if (applied.Contains(key))
                        Debug.Log($"[ReferenceRender] {name} bridge {key}: {applied[key]}");
        }

        private static System.Collections.Generic.IEnumerable<Transform> Ancestors(Transform t)
        {
            for (var p = t.parent; p != null; p = p.parent) yield return p;
        }

        private static GameObject bridgeObject;

        /// <summary>
        /// -neutral: the lighting alone, for calibration. HDRP: a top-priority global volume with fixed exposure EV100 1.3
        /// (the URP build's baked exposure) and no tonemapping, grading, bloom or vignette. URP: HdrpBridge.Neutral.
        /// </summary>
        private static void Neutralize(Component bridge)
        {
            if (bridge != null)
            {
                bridge.GetType().GetField("Neutral")?.SetValue(null, true);
                return;
            }
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            var exposure = profile.Add<UnityEngine.Rendering.HighDefinition.Exposure>(true);
            exposure.mode.value = UnityEngine.Rendering.HighDefinition.ExposureMode.Fixed;
            exposure.fixedExposure.value = 1.3f;
            exposure.compensation.value = 0f;
            profile.Add<UnityEngine.Rendering.HighDefinition.Tonemapping>(true).mode.value = UnityEngine.Rendering.HighDefinition.TonemappingMode.None;
            var color = profile.Add<UnityEngine.Rendering.HighDefinition.ColorAdjustments>(true);
            color.postExposure.value = 0f; color.contrast.value = 0f; color.saturation.value = 0f;
            color.colorFilter.value = Color.white; color.hueShift.value = 0f;
            profile.Add<UnityEngine.Rendering.HighDefinition.Bloom>(true).intensity.value = 0f;
            profile.Add<UnityEngine.Rendering.HighDefinition.Vignette>(true).intensity.value = 0f;
            var go = new GameObject("Neutral volume");
            var volume = go.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.priority = 100000;
            volume.sharedProfile = profile;
            go.layer = 0;
        }

        /// <summary>The URP project's HdrpBridge (absent in the HDRP project), started by hand: no play mode here</summary>
        private static Component BridgeForEditMode()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("LCQuest.HdrpBridge")).FirstOrDefault(t => t != null);
            if (type == null) return null;
            if (bridgeObject == null)
            {
                bridgeObject = new GameObject("HdrpBridge (edit mode)");
                var created = bridgeObject.AddComponent(type);
                created.SendMessage("Awake");
            }
            var bridge = bridgeObject.GetComponent(type);
            bridge.GetType().GetField("nextScan", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(bridge, 0f);
            return bridge;
        }

        private static Vector3 Vector(string text)
        {
            var v = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            return new Vector3(v[0], v.Length > 1 ? v[1] : 0, v.Length > 2 ? v[2] : 0);
        }

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"missing {name}");
        }
    }
}

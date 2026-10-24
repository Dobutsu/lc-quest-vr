using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.Universal;

namespace LCQuest
{
    /// <summary>
    /// HDRP's full-screen custom passes on URP. The game's CustomPassVolumes (HDRP shim components, so the game still
    /// toggles them: HUDManager.mainCustomPass) hold FullScreenCustomPass entries with a material; for each enabled one
    /// in an active volume, this blits the camera colour through that material at the volume's injection point
    /// (the ship's "LethalSponge" outline pass runs before transparents). The materials' shaders are URP rewrites under
    /// the game's shader names (unity-tools/Shaders). Cameras that only draw UI are skipped.
    /// The outline pass itself is folded into the final post pass when the camera has one (FoldSponge): the
    /// LCQuest/UberPost shader runs it on the colour it reads anyway, which saves the colour copies and a full-screen
    /// pass (#10); this only hands it the material's values per camera.
    /// </summary>
    public class CustomPassFeature : ScriptableRendererFeature
    {
        private CustomPassRenderPass beforeTransparent, beforePostProcess, afterPostProcess;
        private ProbePass probe;
        private SpongeSetupPass spongeSetup;
        /// <summary>The outline pass runs inside LCQuest/UberPost instead of on its own (0: as a separate pass)</summary>
        public static bool FoldSponge = true;
        /// <summary>LCQuest/UberPost shows its inputs instead of the picture (1: outlines on, depth); for checks</summary>
        public static float SpongeDebug;
        /// <summary>Neighbours each outline edge reads, on the depth and on the colour: 4 (the diagonals; ~0.5-0.9 ms less
        /// than 8 in the factory's rooms, 0.1% of pixels differ), 8 (the game's Sobel) or 0 (no outlines, ~2.3 ms less; to
        /// measure what they cost)</summary>
        public static int SpongeTaps = 4;
        /// <summary>Per camera: the renderer's pass queue and colour target as last executed (QuestDiagnostics' report)</summary>
        public static readonly Dictionary<string, string> LastPasses = new Dictionary<string, string>();
        /// <summary>Asks URP for its opaque colour copy too (the passes blit their own copy, so it is a second one)</summary>
        public static bool RequestColor = true;

        public override void Create()
        {
            beforeTransparent = new CustomPassRenderPass(CustomPassInjectionPoint.BeforeTransparent, RenderPassEvent.BeforeRenderingTransparents);
            beforePostProcess = new CustomPassRenderPass(CustomPassInjectionPoint.BeforePostProcess, RenderPassEvent.BeforeRenderingPostProcessing);
            afterPostProcess = new CustomPassRenderPass(CustomPassInjectionPoint.AfterPostProcess, RenderPassEvent.AfterRenderingPostProcessing);
            probe = new ProbePass();
            spongeSetup = new SpongeSetupPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            if (renderingData.cameraData.cameraType != CameraType.Game) return;
            if (probe.Due(camera)) renderer.EnqueuePass(probe);
            // Every camera with a post pass sets the outline values, if only to switch them off
            // (the values are global: a camera that left them out would draw the last camera's outlines)
            var post = renderingData.cameraData.postProcessEnabled;
            var fold = FoldSponge && post;
            spongeSetup.Material = null;
            if (post) renderer.EnqueuePass(spongeSetup);
            if ((camera.cullingMask & ~(1 << 5)) == 0) return;
            foreach (var pass in new[] { beforeTransparent, beforePostProcess, afterPostProcess })
            {
                if (!pass.Collect(camera, fold ? spongeSetup : null)) continue;
                pass.ConfigureInput(RequestColor ? ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color : ScriptableRenderPassInput.Depth);
                renderer.EnqueuePass(pass);
            }
        }

        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            foreach (var pass in new[] { beforeTransparent, beforePostProcess, afterPostProcess })
                pass.Target = renderer.cameraColorTargetHandle;
        }

        protected override void Dispose(bool disposing)
        {
            beforeTransparent?.Dispose();
            beforePostProcess?.Dispose();
            afterPostProcess?.Dispose();
        }

        /// <summary>Records, every 2 s per camera, which passes the renderer runs and whether it draws into an
        /// intermediate texture (the eye buffer directly is what foveated rendering needs, #10)</summary>
        private sealed class ProbePass : ScriptableRenderPass
        {
            private static readonly FieldInfo Queue = typeof(ScriptableRenderer).GetField("m_ActiveRenderPassQueue", BindingFlags.NonPublic | BindingFlags.Instance);
            private readonly Dictionary<string, float> times = new Dictionary<string, float>();

            public ProbePass() => renderPassEvent = RenderPassEvent.AfterRendering + 100;

            public bool Due(Camera camera)
            {
                if (times.TryGetValue(camera.name, out var time) && Time.realtimeSinceStartup - time < 2f && Time.realtimeSinceStartup >= time) return false;
                times[camera.name] = Time.realtimeSinceStartup;
                return true;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cameraData = renderingData.cameraData;
                var renderer = cameraData.renderer;
                var passes = Queue?.GetValue(renderer) as List<ScriptableRenderPass>;
                var target = renderer.cameraColorTargetHandle;
                LastPasses[cameraData.camera.name] =
                    $"target {(target?.rt != null ? $"{target.rt.name} {target.rt.width}x{target.rt.height}" : target?.name ?? "-")} xr {cameraData.xr.enabled} " +
                    $"post {cameraData.postProcessEnabled} depthTex {cameraData.requiresDepthTexture} opaqueTex {cameraData.requiresOpaqueTexture} " +
                    $"hdr {cameraData.isHdrEnabled} msaa {cameraData.cameraTargetDescriptor.msaaSamples} stack {cameraData.renderType}: " +
                    string.Join(", ", passes?.Where(p => p != this).Select(p => $"{p.GetType().Name}@{p.renderPassEvent}") ?? Enumerable.Empty<string>());
            }
        }

        /// <summary>Hands LCQuest/UberPost the outline material's values for this camera (none: off), and asks for the
        /// depth texture its edges read</summary>
        private sealed class SpongeSetupPass : ScriptableRenderPass
        {
            public const string Shader = "FullScreen/SpongePosterizeNew";
            private static readonly int On = UnityEngine.Shader.PropertyToID("_LCQuestSponge"),
                FogMode = UnityEngine.Shader.PropertyToID("_LCQuestFogMode"),
                DepthParams = UnityEngine.Shader.PropertyToID("_LCQuestSpongeDepth"),
                ColorParams = UnityEngine.Shader.PropertyToID("_LCQuestSpongeColor"),
                Debug = UnityEngine.Shader.PropertyToID("_LCQuestSpongeDebug"),
                Taps = UnityEngine.Shader.PropertyToID("_LCQuestSpongeTaps");
            private Material material;

            public SpongeSetupPass() => renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

            public Material Material
            {
                set
                {
                    material = value;
                    ConfigureInput(value != null ? ScriptableRenderPassInput.Depth : ScriptableRenderPassInput.None);
                }
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cmd = CommandBufferPool.Get();
                cmd.SetGlobalFloat(On, material != null && SpongeTaps > 0 ? 1f : 0f);
                cmd.SetGlobalFloat(Taps, SpongeTaps);
                cmd.SetGlobalFloat(Debug, SpongeDebug);
                if (material != null)
                {
                    cmd.SetGlobalFloat(FogMode, RenderSettings.fog ? (int)RenderSettings.fogMode : 0);
                    cmd.SetGlobalVector(DepthParams, new Vector4(material.GetFloat("_OutlineThickness"), material.GetFloat("_DepthThreshold"),
                        material.GetFloat("_DepthCurve"), material.GetFloat("_DepthStrength")));
                    cmd.SetGlobalVector(ColorParams, new Vector4(material.GetFloat("_ColorThreshold"), material.GetFloat("_ColorCurve"),
                        material.GetFloat("_ColorStrength"), 0f));
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }

        private sealed class CustomPassRenderPass : ScriptableRenderPass
        {
            private static readonly List<CustomPassVolume> Volumes = new List<CustomPassVolume>();
            private static float volumesTime = -10f;

            private readonly CustomPassInjectionPoint injectionPoint;
            private readonly List<(Material material, int pass)> passes = new List<(Material, int)>();
            private RTHandle copy;
            public RTHandle Target;

            public CustomPassRenderPass(CustomPassInjectionPoint injectionPoint, RenderPassEvent renderPassEvent)
            {
                this.injectionPoint = injectionPoint;
                this.renderPassEvent = renderPassEvent;
                profilingSampler = new ProfilingSampler("LCQuest CustomPass " + injectionPoint);
            }

            /// <summary>The enabled full-screen passes of the active volumes at this injection point</summary>
            public bool Collect(Camera camera, SpongeSetupPass fold)
            {
                // FindObjectsOfType walks every object in the scenes: every 2 s, not every frame (it cost milliseconds)
                if (Time.realtimeSinceStartup - volumesTime > 2f || Time.realtimeSinceStartup < volumesTime)
                {
                    volumesTime = Time.realtimeSinceStartup;
                    Volumes.Clear();
                    Volumes.AddRange(Object.FindObjectsOfType<CustomPassVolume>());
                }
                passes.Clear();
                foreach (var volume in Volumes)
                {
                    if (volume == null || !volume.isActiveAndEnabled || volume.injectionPoint != injectionPoint) continue;
                    if (volume.targetCamera != null && volume.targetCamera != camera) continue;
                    if (!volume.isGlobal && !Inside(volume, camera.transform.position)) continue;
                    foreach (var pass in volume.customPasses)
                        if (pass is FullScreenCustomPass fullScreen && fullScreen.enabled && fullScreen.fullscreenPassMaterial != null)
                        {
                            if (fold != null && fullScreen.fullscreenPassMaterial.shader.name == SpongeSetupPass.Shader)
                            {
                                fold.Material = fullScreen.fullscreenPassMaterial;
                                continue;
                            }
                            passes.Add((fullScreen.fullscreenPassMaterial, Mathf.Max(0, fullScreen.fullscreenPassMaterial.FindPass(fullScreen.materialPassName))));
                        }
                }
                return passes.Count > 0;
            }

            private static bool Inside(Component volume, Vector3 position)
            {
                foreach (var collider in volume.GetComponents<Collider>())
                    if (collider.enabled && collider.ClosestPoint(position) == position)
                        return true;
                return false;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (Target == null) return;
                var descriptor = renderingData.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;
                RenderingUtils.ReAllocateIfNeeded(ref copy, descriptor, FilterMode.Point, TextureWrapMode.Clamp, name: "_LCQuestCustomPassCopy");

                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, profilingSampler))
                {
                    foreach (var (material, pass) in passes)
                    {
                        Blitter.BlitCameraTexture(cmd, Target, copy);
                        // Load: passes that blend (water on the visor) draw over the picture
                        Blitter.BlitCameraTexture(cmd, copy, Target, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                            material, Mathf.Min(pass, material.passCount - 1));
                    }
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public void Dispose() => copy?.Release();
        }
    }
}

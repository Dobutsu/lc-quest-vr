using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LCQuest.Editor
{
    /// <summary>
    /// The game's HDRP materials on URP shaders. AssetRipper's placeholder shaders keep the original property blocks,
    /// so each material's HDRP values still read by name; they are copied onto the URP equivalents and URP's own
    /// ShaderGUI then sets keywords, blend states and render queue as its inspector would.
    ///   HDRP/Lit, HDRP/LayeredLit (layer 0), the game's Shader Graphs, ProBuilder -> Universal Render Pipeline/Lit
    ///   HDRP/Unlit -> Universal Render Pipeline/Unlit;  HDRP/TerrainLit -> Universal Render Pipeline/Terrain/Lit
    ///   Custom/Additive (Soft, Tintable), Custom/LightningBoltShaderMesh -> Universal Render Pipeline/Particles/Unlit
    ///   HDRP/Decal -> Shader Graphs/Decal (URP's), with the colour and opacity baked into a copy of the texture:
    ///   URP's decal graph has no colour (spray paint is one texture in four colours)
    /// The full-screen effects (outline, water on lens) have their own URP passes.
    /// </summary>
    internal static class HdrpMaterialConverter
    {
        // HDRP renders emission in nits under the camera exposure; same scale as the lights (scripts/convert_lights.py)
        private const float ExposureEV100 = 1.3f;
        private static readonly float Exposure = 1f / (1.2f * Mathf.Pow(2f, ExposureEV100));

        private static readonly string[] LitSources = { "HDRP/Lit", "ProBuilder/Standard Vertex Color" };
        private static readonly string[] LitGraphs = { "Shader Graphs/WavingGrass", "Shader Graphs/BlobShader", "Shader Graphs/FurShader" };
        private static readonly string[] ParticleSources = { "Custom/Additive (Soft, Tintable)", "Custom/LightningBoltShaderMesh" };

        public static void Apply()
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            var terrain = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            var particles = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            var decal = Shader.Find("Shader Graphs/Decal");
            if (lit == null || unlit == null || terrain == null || particles == null)
                throw new InvalidOperationException("URP shaders not found; is the URP package in the project?");

            var counts = new Dictionary<string, int>();
            var decals = new List<Material>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null || material.shader == null) continue;
                    var source = material.shader.name;
                    bool converted;
                    if (LitSources.Contains(source)) converted = ToLit(material, lit, "");
                    else if (source == "HDRP/LayeredLit") converted = ToLayered(material);
                    else if (LitGraphs.Contains(source)) converted = ToLit(material, lit, "");
                    else if (source == "Shader Graphs/WaterShaderHDRP") converted = ToWater(material, lit);
                    else if (source == "Shader Graphs/HologramShader") converted = ToHologram(material, unlit);
                    else if (source == "HDRP/Unlit") converted = ToUnlit(material, unlit);
                    else if (source == "HDRP/TerrainLit") { material.shader = terrain; converted = true; }
                    else if (ParticleSources.Contains(source)) converted = ToAdditiveParticles(material, particles);
                    else if (source == "HDRP/Decal" && decal != null) { decals.Add(material); continue; }
                    else continue;
                    if (!converted) continue;
                    EditorUtility.SetDirty(material);
                    counts[source] = counts.TryGetValue(source, out var n) ? n + 1 : 1;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            // Outside the batch: the tinted textures must import before the materials can take them
            foreach (var material in decals)
            {
                ToDecal(material, decal);
                EditorUtility.SetDirty(material);
                counts["HDRP/Decal"] = counts.TryGetValue("HDRP/Decal", out var n) ? n + 1 : 1;
            }
            Debug.Log("[HdrpMaterialConverter] " + string.Join(", ", counts.OrderByDescending(c => c.Value).Select(c => $"{c.Key}: {c.Value}")));
        }

        /// <summary>
        /// Stage 5 measurement (LCQUEST_LIT=simple): URP Lit materials drawn with Simple Lit (Blinn-Phong, no
        /// reflections), a third cheaper on the Quest's opaque pass. Only the shader, keywords and _SpecColor change, so
        /// a later run without the variable puts them back on Lit with their own values (materials keep the properties
        /// of a shader they no longer use)
        /// </summary>
        public static void LitVariant(bool simple)
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var simpleLit = Shader.Find("Universal Render Pipeline/Simple Lit");
            var count = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.StartsWith("Assets/LCQuest/")) continue;
                    var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (m == null) continue;
                    if (m.shader == lit && !simple)
                    {
                        // Reflection probes off: in Forward+ each pixel walks every probe of its cluster, 5-7 ms of the
                        // Quest's frame, and the game's look barely shows them (metals keep URP's ambient reflection
                        // colour). Specular highlights stay: the lamps' light pools on the ship's walls are theirs.
                        // LCQUEST_LIT=env, nospec: measuring
                        var mode = Environment.GetEnvironmentVariable("LCQUEST_LIT") ?? "";
                        m.SetFloat("_EnvironmentReflections", mode.Contains("env") && !mode.Contains("noenv") ? 1f : 0f);
                        m.SetFloat("_SpecularHighlights", mode.Contains("nospec") ? 0f : 1f);
                        Validate(m, "UnityEditor.Rendering.Universal.ShaderGUI.LitShader");
                        EditorUtility.SetDirty(m);
                        continue;
                    }
                    if (m.shader != (simple ? lit : simpleLit)) continue;
                    if (simple)
                    {
                        var metallic = Float(m, "_Metallic", 0f);
                        var smoothness = Float(m, "_Smoothness", 0.5f);
                        var grey = Mathf.Lerp(0.04f, 0.5f, metallic) * smoothness;
                        m.shader = simpleLit;
                        m.SetColor("_SpecColor", new Color(grey, grey, grey, smoothness));
                        m.SetFloat("_SpecularHighlights", 1f);
                        m.SetFloat("_SmoothnessSource", 0f);
                        Validate(m, "UnityEditor.Rendering.Universal.ShaderGUI.SimpleLitShader");
                    }
                    else
                    {
                        m.shader = lit;
                        Validate(m, "UnityEditor.Rendering.Universal.ShaderGUI.LitShader");
                    }
                    EditorUtility.SetDirty(m);
                    count++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            Debug.Log($"[HdrpMaterialConverter] {count} materials -> {(simple ? "Simple Lit" : "Lit")}");
        }

        private struct TexSlot
        {
            public Texture Texture;
            public Vector2 Scale, Offset;
        }

        private static TexSlot Tex(Material m, params string[] names)
        {
            foreach (var name in names)
                if (m.HasProperty(name) && m.GetTexture(name) != null)
                    return new TexSlot { Texture = m.GetTexture(name), Scale = m.GetTextureScale(name), Offset = m.GetTextureOffset(name) };
            return new TexSlot { Scale = Vector2.one };
        }

        private static float Float(Material m, string name, float fallback) => m.HasProperty(name) ? m.GetFloat(name) : fallback;

        /// <summary>URP's cull mode from HDRP's: none when double-sided, else the forward pass's (front for surfaces
        /// seen from inside, as LCVR's menu backdrop and the game's build-mode ghost outlines are)</summary>
        private static float Cull(Material m) => Float(m, "_DoubleSidedEnable", 0f) > 0.5f ? 0f
            : Float(m, "_CullModeForward", Float(m, "_CullMode", 2f));

        private static Color Col(Material m, Color fallback, params string[] names)
        {
            foreach (var name in names)
                if (m.HasProperty(name)) return m.GetColor(name);
            return fallback;
        }

        private static void SetTex(Material m, string name, TexSlot slot)
        {
            m.SetTexture(name, slot.Texture);
            m.SetTextureScale(name, slot.Scale);
            m.SetTextureOffset(name, slot.Offset);
        }

        private static bool ToLit(Material m, Shader lit, string layer)
        {
            var baseMap = Tex(m, "_BaseColorMap" + layer, "_BaseColorMap", "_MainTex", "_BaseMap", "_Albedo", "_Texture", "_MainTexture", "_Diffuse");
            var baseColor = Col(m, Color.white, "_BaseColor" + layer, "_BaseColor", "_Color", "_Tint");
            var normal = Tex(m, "_NormalMap" + layer, "_NormalMap", "_BumpMap", "_Normal", "_NormalMapTex");
            var normalScale = Float(m, "_NormalScale" + layer, Float(m, "_BumpScale", 1f));
            var mask = Tex(m, "_MaskMap" + layer, "_MaskMap");
            var metallic = Float(m, "_Metallic" + layer, 0f);
            var smoothness = mask.Texture != null
                ? Float(m, "_SmoothnessRemapMax" + layer, 1f)
                : Float(m, "_Smoothness" + layer, 0.5f);
            var aoMin = Float(m, "_AORemapMin" + layer, 0f);
            var emissionMap = Tex(m, "_EmissiveColorMap");
            var emission = Col(m, Color.black, "_EmissiveColor") * Exposure;
            var surface = Float(m, "_SurfaceType", 0f);
            var blendMode = Float(m, "_BlendMode", 0f);
            var alphaClip = Float(m, "_AlphaCutoffEnable", 0f);
            var cutoff = Float(m, "_AlphaCutoff", 0.5f);
            var cull = Cull(m);

            m.shader = lit;
            SetTex(m, "_BaseMap", baseMap);
            SetTex(m, "_MainTex", baseMap);
            m.SetColor("_BaseColor", baseColor);
            m.SetColor("_Color", baseColor);
            SetTex(m, "_BumpMap", normal);
            m.SetFloat("_BumpScale", normalScale);
            // HDRP mask map: R metallic, G occlusion, B detail mask, A smoothness. URP's metallic map reads R and A,
            // its occlusion map G: the same texture serves both
            SetTex(m, "_MetallicGlossMap", mask);
            SetTex(m, "_OcclusionMap", mask);
            m.SetFloat("_OcclusionStrength", mask.Texture != null ? 1f - aoMin : 1f);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_SmoothnessTextureChannel", 0f);
            SetTex(m, "_EmissionMap", emissionMap);
            m.SetColor("_EmissionColor", emission);
            var emissive = emission.maxColorComponent > 0.0001f;
            m.globalIlluminationFlags = emissive ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            if (emissive) m.EnableKeyword("_EMISSION"); else m.DisableKeyword("_EMISSION");

            m.SetFloat("_Surface", surface);
            // HDRP blend modes: 0 alpha, 1 additive, 4 premultiplied; URP: 0 alpha, 1 premultiply, 2 additive
            m.SetFloat("_Blend", blendMode switch { 1f => 2f, 4f => 1f, _ => 0f });
            m.SetFloat("_AlphaClip", alphaClip);
            m.SetFloat("_Cutoff", cutoff);
            m.SetFloat("_Cull", cull);
            m.renderQueue = -1;
            Validate(m, "UnityEditor.Rendering.Universal.ShaderGUI.LitShader");
            return true;
        }

        private static bool ToUnlit(Material m, Shader unlit)
        {
            var baseMap = Tex(m, "_UnlitColorMap", "_MainTex", "_BaseColorMap");
            var color = Col(m, Color.white, "_UnlitColor", "_Color", "_BaseColor");
            var emission = Col(m, Color.black, "_EmissiveColor") * Exposure;
            var surface = Float(m, "_SurfaceType", 0f);
            var blendMode = Float(m, "_BlendMode", 0f);
            var alphaClip = Float(m, "_AlphaCutoffEnable", 0f);
            var cutoff = Float(m, "_AlphaCutoff", 0.5f);
            var cull = Cull(m);

            m.shader = unlit;
            SetTex(m, "_BaseMap", baseMap);
            SetTex(m, "_MainTex", baseMap);
            // URP Unlit has no emission: an unlit surface's colour is what it emits
            m.SetColor("_BaseColor", baseMap.Texture == null && emission.maxColorComponent > color.maxColorComponent
                ? new Color(emission.r, emission.g, emission.b, color.a) : color);
            m.SetFloat("_Surface", surface);
            m.SetFloat("_Blend", blendMode switch { 1f => 2f, 4f => 1f, _ => 0f });
            m.SetFloat("_AlphaClip", alphaClip);
            m.SetFloat("_Cutoff", cutoff);
            m.SetFloat("_Cull", cull);
            m.renderQueue = -1;
            Validate(m, "UnityEditor.Rendering.Universal.ShaderGUI.UnlitShader");
            return true;
        }

        /// <summary>
        /// HDRP/LayeredLit (the splat-mapped terrain meshes): our LCQuest/LayeredLit takes HDRP's property names as
        /// they are; only the mask-map flags are ours
        /// </summary>
        private static bool ToLayered(Material m)
        {
            var layered = Shader.Find("LCQuest/LayeredLit");
            if (layered == null) return false;
            var hasMask = new float[4];
            for (var i = 0; i < 4; i++)
                hasMask[i] = m.HasProperty($"_MaskMap{i}") && m.GetTexture($"_MaskMap{i}") != null ? 1f : 0f;
            m.shader = layered;
            for (var i = 0; i < 4; i++) m.SetFloat($"_HasMask{i}", hasMask[i]);
            m.renderQueue = -1;
            return true;
        }

        /// <summary>
        /// The game's water graph is procedural (no textures or colours on its materials): dark, glossy, see-through
        /// </summary>
        private static bool ToWater(Material m, Shader lit)
        {
            m.shader = lit;
            m.SetTexture("_BaseMap", null);
            m.SetColor("_BaseColor", new Color(0.05f, 0.09f, 0.10f, 0.8f));
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", 0.95f);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_Cull", 2f);
            m.renderQueue = -1;
            Validate(m, "UnityEditor.Rendering.Universal.ShaderGUI.LitShader");
            return true;
        }

        /// <summary>The hologram graph: additive fresnel colour with scanlines; here the scanlines in that colour</summary>
        private static bool ToHologram(Material m, Shader unlit)
        {
            var lines = Tex(m, "_HologramScanlines");
            var color = Col(m, Color.red, "_FresnelColor");
            var tiling = m.HasProperty("_HologramTiling") ? (Vector2)m.GetVector("_HologramTiling") : Vector2.one;
            m.shader = unlit;
            m.SetTexture("_BaseMap", lines.Texture);
            m.SetTextureScale("_BaseMap", tiling);
            m.SetColor("_BaseColor", new Color(color.r, color.g, color.b, 0.6f));
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 2f);  // additive
            m.SetFloat("_Cull", 0f);
            m.renderQueue = -1;
            Validate(m, "UnityEditor.Rendering.Universal.ShaderGUI.UnlitShader");
            return true;
        }

        private static bool ToAdditiveParticles(Material m, Shader particles)
        {
            var baseMap = Tex(m, "_MainTex", "_BaseMap");
            var color = Col(m, Color.white, "_TintColor", "_Color", "_BaseColor");
            m.shader = particles;
            SetTex(m, "_BaseMap", baseMap);
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 2f);
            m.renderQueue = -1;
            Validate(m, "UnityEditor.Rendering.Universal.ShaderGUI.ParticlesUnlitShader");
            return true;
        }

        private const string DecalFolder = "Assets/LCQuest/Decals";

        private static bool ToDecal(Material m, Shader decal)
        {
            var baseMap = Tex(m, "_BaseColorMap");
            var normal = Tex(m, "_NormalMap");
            var color = Col(m, Color.white, "_BaseColor");
            var opacity = Float(m, "_DecalBlend", 1f) * color.a;
            var tinted = TintedCopy(m.name, baseMap.Texture, new Color(color.r, color.g, color.b, opacity));
            m.shader = decal;
            m.SetTexture("Base_Map", tinted);
            m.SetTextureScale("Base_Map", baseMap.Scale);
            m.SetTextureOffset("Base_Map", baseMap.Offset);
            if (normal.Texture != null) m.SetTexture("Normal_Map", normal.Texture);
            m.SetFloat("Normal_Blend", normal.Texture != null ? 0.5f : 0f);
            m.enableInstancing = true;
            return true;
        }

        /// <summary>The texture times a colour (alpha too), saved as a PNG asset; a white texel for none</summary>
        private static Texture2D TintedCopy(string name, Texture source, Color tint)
        {
            if (!AssetDatabase.IsValidFolder(DecalFolder)) AssetDatabase.CreateFolder("Assets/LCQuest", "Decals");
            var path = $"{DecalFolder}/{name}.png";
            int width = source != null ? source.width : 4, height = source != null ? source.height : 4;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source != null ? source : Texture2D.whiteTexture, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var pixels = copy.GetPixels();
            for (var i = 0; i < pixels.Length; i++) pixels[i] *= tint;
            copy.SetPixels(pixels);
            System.IO.File.WriteAllBytes(path, copy.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(copy);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>What URP's inspector does after a change: keywords, blend state, render queue</summary>
        private static void Validate(Material m, string shaderGui)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(shaderGui))
                .FirstOrDefault(t => t != null);
            if (type == null)
            {
                Debug.LogWarning($"[HdrpMaterialConverter] {shaderGui} not found; {m.name} keeps default keywords");
                return;
            }
            var gui = (ShaderGUI)Activator.CreateInstance(type, true);
            gui.ValidateMaterial(m);
        }
    }
}

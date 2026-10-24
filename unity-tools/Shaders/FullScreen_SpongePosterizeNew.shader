// Lethal Company's full-screen custom pass ("LethalSponge" on the ship's global CustomPassVolume, before transparents),
// rewritten for URP from the game's compiled HDRP shader (scripts/shader_disasm.py "FullScreen/SpongePosterizeNew"):
//   1. sky pixels (no depth) pass through
//   2. a little contrast: 30% of an overlay blend of the colour with white
//   3. black outlines: the larger of a Sobel edge on the raw depth (neighbours _OutlineThickness * screen size pixels
//      away) and a per-channel Sobel edge on the colour (neighbours _OutlineThickness away in UV), each through
//      smoothstep(edge / threshold) ^ curve * strength
//   4. HDRP does all this before fogging; URP's input is already fogged, so the fog is taken out first and put back
//      after (pixels the mask blacks out end up as fog, as in HDRP, instead of black)
// Same name and properties as the game's shader, so its material (FullScreen_SpongePosterizeNew2) binds unchanged.
Shader "FullScreen/SpongePosterizeNew"
{
    Properties
    {
        _OutlineThickness ("Outline Thickness", Float) = 0.001
        _DepthThreshold ("Depth Threshold", Float) = 0.4
        _DepthCurve ("Depth Curve", Float) = 0.4
        _DepthStrength ("Depth Strength", Float) = 6
        _ColorThreshold ("Color Threshold", Float) = 0.47
        _ColorCurve ("Color Curve", Float) = 2.94
        _ColorStrength ("Color Strength", Float) = 0.65
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "ReadColor"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ FOG_LINEAR FOG_EXP FOG_EXP2

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _OutlineThickness;
            float _DepthThreshold, _DepthCurve, _DepthStrength;
            float _ColorThreshold, _ColorCurve, _ColorStrength;

            float3 Color(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, clamp(uv, 0, 1) * _BlitScaleBias.xy + _BlitScaleBias.zw, 0).rgb;
            }

            float Depth(float2 pixel)
            {
                return LoadSceneDepth(clamp(pixel, 0, _ScreenParams.xy - 1));
            }

            float Shape(float edge, float threshold, float curve, float strength)
            {
                float t = saturate(edge / threshold);
                return pow(max(t * t * (3 - 2 * t), 1e-6), curve) * strength;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 pixel = uv * _ScreenParams.xy;
                float raw = Depth(pixel);
                float3 c = Color(uv);
            #if UNITY_REVERSED_Z
                if (raw == 0) return float4(c, 1);
            #else
                if (raw == 1) return float4(c, 1);
            #endif

                // Take the fog out (URP fogged the scene already; HDRP fogs after this pass)
                float transmittance = 1;
            #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                float eye = LinearEyeDepth(raw, _ZBufferParams);
                #if defined(FOG_LINEAR)
                    transmittance = saturate(eye * unity_FogParams.z + unity_FogParams.w);
                #elif defined(FOG_EXP)
                    transmittance = saturate(exp2(-eye * unity_FogParams.y));
                #else
                    float f = eye * unity_FogParams.x;
                    transmittance = saturate(exp2(-f * f));
                #endif
            #endif
                float3 fogColor = unity_FogColor.rgb;
                float3 u = max(0, (c - fogColor * (1 - transmittance)) / max(transmittance, 1e-3));

                float m = saturate((length(u) - 0.01) * 100000);
                float3 cm = u * m;
                float lum = dot(u, float3(0.212673, 0.715152, 0.072175));
                float w = saturate(1 - (abs(0.656266 - lum) - 1) * 100000);
                float3 overlay = cm < 0.5 ? 2 * cm * w : 1 - 2 * (1 - cm) * (1 - w);
                float3 col = cm + 0.3 * (overlay - cm);

                float2 d = _OutlineThickness * _ScreenParams.xy;
                float tl = Depth(pixel + float2(-d.x, d.y)), t = Depth(pixel + float2(0, d.y)), tr = Depth(pixel + d);
                float l = Depth(pixel + float2(-d.x, 0)), r = Depth(pixel + float2(d.x, 0));
                float bl = Depth(pixel - d), b = Depth(pixel - float2(0, d.y)), br = Depth(pixel + float2(d.x, -d.y));
                float2 gd = float2(tl + 2 * l + bl - tr - 2 * r - br, tl + 2 * t + tr - bl - 2 * b - br);
                float depthEdge = Shape(length(gd), _DepthThreshold, _DepthCurve, _DepthStrength);

                float o = _OutlineThickness;
                float3 ctl = Color(uv + float2(-o, o)), ct = Color(uv + float2(0, o)), ctr = Color(uv + float2(o, o));
                float3 cl = Color(uv + float2(-o, 0)), cr = Color(uv + float2(o, 0));
                float3 cbl = Color(uv + float2(-o, -o)), cb = Color(uv + float2(0, -o)), cbr = Color(uv + float2(o, -o));
                float3 gx = ctl + 2 * cl + cbl - ctr - 2 * cr - cbr;
                float3 gy = ctl + 2 * ct + ctr - cbl - 2 * cb - cbr;
                float3 mag = sqrt(gx * gx + gy * gy);
                float colorEdge = Shape(max(mag.r, max(mag.g, mag.b)), _ColorThreshold, _ColorCurve, _ColorStrength);

                float edge = saturate(max(colorEdge, depthEdge));
                // Fog back on, as HDRP applies it to the outlined colour
                col = lerp(fogColor, col * (1 - edge), transmittance);
                return float4(col, 1);
            }
            ENDHLSL
        }
    }
}

// Lethal Company's water/spit-on-visor full-screen pass ("InkOnScreen" on the ship's CustomPassVolume, which the game
// switches on and fades with _Alpha), rewritten for URP from the game's compiled Shader Graph:
//   the screen seen through the droplets (offset by their normal map), overlay-blended with their colour, darkened by
//   0.2 x their mask, laid over the picture with opacity _Alpha.
// Same name and property names as the game's graph, so its material (WaterOnLensMat) binds unchanged; textures in the
// graph's order: mask (transparent), normal map, colour.
Shader "Shader Graphs/WaterOnLensEffect"
{
    Properties
    {
        _Alpha ("Alpha", Float) = 0
        _SampleTexture2D_0a2b89e6aa154420bea882fd73f4e604_Texture_1_Texture2D ("Mask", 2D) = "black" {}
        _SampleTexture2D_adade63c34b745e99c10c5dd81004c40_Texture_1_Texture2D ("Normal", 2D) = "bump" {}
        _SampleTexture2D_c7e7399bfe5741379abd27370395f2a5_Texture_1_Texture2D ("Color", 2D) = "grey" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "Custom Pass 0"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Alpha;
            TEXTURE2D(_SampleTexture2D_0a2b89e6aa154420bea882fd73f4e604_Texture_1_Texture2D);
            TEXTURE2D(_SampleTexture2D_adade63c34b745e99c10c5dd81004c40_Texture_1_Texture2D);
            TEXTURE2D(_SampleTexture2D_c7e7399bfe5741379abd27370395f2a5_Texture_1_Texture2D);

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float4 n = SAMPLE_TEXTURE2D(_SampleTexture2D_adade63c34b745e99c10c5dd81004c40_Texture_1_Texture2D, sampler_LinearRepeat, uv);
                float2 offset = float2(n.r * n.a, n.g) * 2 - 1;
                float2 screenUV = clamp(uv + offset, 0, 1);
                float3 base = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, screenUV * _BlitScaleBias.xy + _BlitScaleBias.zw, 0).rgb;
                float3 tint = SAMPLE_TEXTURE2D(_SampleTexture2D_c7e7399bfe5741379abd27370395f2a5_Texture_1_Texture2D, sampler_LinearRepeat, uv).rgb;
                float3 overlay = base >= 0.5 ? 1 - 2 * (1 - base) * (1 - tint) : 2 * base * tint;
                float mask = SAMPLE_TEXTURE2D(_SampleTexture2D_0a2b89e6aa154420bea882fd73f4e604_Texture_1_Texture2D, sampler_LinearRepeat, uv).a;
                return float4(overlay - 0.2 * mask, saturate(_Alpha));
            }
            ENDHLSL
        }
    }
}

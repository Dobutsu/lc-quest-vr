// HDRP's LayeredLit on URP, for the game's splat-mapped terrain meshes (Titan, Rend, Vow, Dine, Artifice...): four
// layers of albedo, normal and mask maps blended by the layer mask texture as HDRP does without height blending
// (red over the base layer -> layer 1, green -> layer 2, blue -> layer 3), lit by URP (Forward+, shadows, fog).
// HDRP mask maps: R metallic, G occlusion, A smoothness. Property names are HDRP's, so HdrpMaterialConverter only
// switches the shader.
Shader "LCQuest/LayeredLit"
{
    Properties
    {
        _LayerMaskMap ("Layer Mask", 2D) = "black" {}
        _LayerCount ("Layer Count", Float) = 4
        _BaseColor0 ("Base Color 0", Color) = (1,1,1,1)
        _BaseColor1 ("Base Color 1", Color) = (1,1,1,1)
        _BaseColor2 ("Base Color 2", Color) = (1,1,1,1)
        _BaseColor3 ("Base Color 3", Color) = (1,1,1,1)
        _BaseColorMap0 ("Albedo 0", 2D) = "white" {}
        _BaseColorMap1 ("Albedo 1", 2D) = "white" {}
        _BaseColorMap2 ("Albedo 2", 2D) = "white" {}
        _BaseColorMap3 ("Albedo 3", 2D) = "white" {}
        [Normal] _NormalMap0 ("Normal 0", 2D) = "bump" {}
        [Normal] _NormalMap1 ("Normal 1", 2D) = "bump" {}
        [Normal] _NormalMap2 ("Normal 2", 2D) = "bump" {}
        [Normal] _NormalMap3 ("Normal 3", 2D) = "bump" {}
        _NormalScale0 ("Normal Scale 0", Float) = 1
        _NormalScale1 ("Normal Scale 1", Float) = 1
        _NormalScale2 ("Normal Scale 2", Float) = 1
        _NormalScale3 ("Normal Scale 3", Float) = 1
        _MaskMap0 ("Mask 0", 2D) = "white" {}
        _MaskMap1 ("Mask 1", 2D) = "white" {}
        _MaskMap2 ("Mask 2", 2D) = "white" {}
        _MaskMap3 ("Mask 3", 2D) = "white" {}
        _Metallic0 ("Metallic 0", Float) = 0
        _Metallic1 ("Metallic 1", Float) = 0
        _Metallic2 ("Metallic 2", Float) = 0
        _Metallic3 ("Metallic 3", Float) = 0
        _Smoothness0 ("Smoothness 0", Float) = 0.5
        _Smoothness1 ("Smoothness 1", Float) = 0.5
        _Smoothness2 ("Smoothness 2", Float) = 0.5
        _Smoothness3 ("Smoothness 3", Float) = 0.5
        // 1 where the material's layer has a mask map (else _Smoothness/_Metallic alone)
        _HasMask0 ("Has Mask 0", Float) = 0
        _HasMask1 ("Has Mask 1", Float) = 0
        _HasMask2 ("Has Mask 2", Float) = 0
        _HasMask3 ("Has Mask 3", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_LayerMaskMap); SAMPLER(sampler_LayerMaskMap);
        TEXTURE2D(_BaseColorMap0); TEXTURE2D(_BaseColorMap1); TEXTURE2D(_BaseColorMap2); TEXTURE2D(_BaseColorMap3);
        TEXTURE2D(_NormalMap0); TEXTURE2D(_NormalMap1); TEXTURE2D(_NormalMap2); TEXTURE2D(_NormalMap3);
        TEXTURE2D(_MaskMap0); TEXTURE2D(_MaskMap1); TEXTURE2D(_MaskMap2); TEXTURE2D(_MaskMap3);
        SAMPLER(sampler_BaseColorMap0);

        CBUFFER_START(UnityPerMaterial)
            float4 _LayerMaskMap_ST;
            float4 _BaseColorMap0_ST, _BaseColorMap1_ST, _BaseColorMap2_ST, _BaseColorMap3_ST;
            float4 _BaseColor0, _BaseColor1, _BaseColor2, _BaseColor3;
            float _NormalScale0, _NormalScale1, _NormalScale2, _NormalScale3;
            float _Metallic0, _Metallic1, _Metallic2, _Metallic3;
            float _Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3;
            float _HasMask0, _HasMask1, _HasMask2, _HasMask3;
            float _LayerCount;
        CBUFFER_END

        // Layer weights: HDRP's LayeredLit without height blending, base layer under 1, 2, 3
        float4 LayerWeights(float2 uv)
        {
            float3 m = SAMPLE_TEXTURE2D(_LayerMaskMap, sampler_LayerMaskMap, uv * _LayerMaskMap_ST.xy + _LayerMaskMap_ST.zw).rgb;
            if (_LayerCount < 4) m.b = 0;
            if (_LayerCount < 3) m.g = 0;
            float w3 = m.b;
            float w2 = m.g * (1 - w3);
            float w1 = m.r * (1 - m.g) * (1 - w3);
            float w0 = 1 - w1 - w2 - w3;
            return float4(w0, w1, w2, w3);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            // No reflection probes, as the Lit materials (HdrpMaterialConverter): in Forward+ each pixel walks every
            // probe of its cluster
            #define _ENVIRONMENTREFLECTIONS_OFF

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float4 tangentWS : TEXCOORD3;
                float fogFactor : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs n = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = n.normalWS;
                o.tangentWS = float4(n.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                o.uv = input.uv;
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            void Layer(float2 uv, float4 st, TEXTURE2D_PARAM(albedoMap, s), TEXTURE2D(normalMap), TEXTURE2D(maskMap),
                       float4 tint, float normalScale, float metallic, float smoothness, float hasMask,
                       float weight, inout float3 albedo, inout float3 normalTS, inout float3 mrs)
            {
                if (weight <= 0.001) return;
                float2 luv = uv * st.xy + st.zw;
                albedo += weight * SAMPLE_TEXTURE2D(albedoMap, s, luv).rgb * tint.rgb;
                normalTS += weight * UnpackNormalScale(SAMPLE_TEXTURE2D(normalMap, s, luv), normalScale);
                float4 mask = SAMPLE_TEXTURE2D(maskMap, s, luv);
                mrs += weight * (hasMask > 0.5 ? float3(mask.r, mask.g, mask.a * smoothness) : float3(metallic, 1, smoothness));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float4 w = LayerWeights(input.uv);
                float3 albedo = 0, normalTS = 0, mrs = 0;
                Layer(input.uv, _BaseColorMap0_ST, TEXTURE2D_ARGS(_BaseColorMap0, sampler_BaseColorMap0), _NormalMap0, _MaskMap0, _BaseColor0, _NormalScale0, _Metallic0, _Smoothness0, _HasMask0, w.x, albedo, normalTS, mrs);
                Layer(input.uv, _BaseColorMap1_ST, TEXTURE2D_ARGS(_BaseColorMap1, sampler_BaseColorMap0), _NormalMap1, _MaskMap1, _BaseColor1, _NormalScale1, _Metallic1, _Smoothness1, _HasMask1, w.y, albedo, normalTS, mrs);
                Layer(input.uv, _BaseColorMap2_ST, TEXTURE2D_ARGS(_BaseColorMap2, sampler_BaseColorMap0), _NormalMap2, _MaskMap2, _BaseColor2, _NormalScale2, _Metallic2, _Smoothness2, _HasMask2, w.z, albedo, normalTS, mrs);
                Layer(input.uv, _BaseColorMap3_ST, TEXTURE2D_ARGS(_BaseColorMap3, sampler_BaseColorMap0), _NormalMap3, _MaskMap3, _BaseColor3, _NormalScale3, _Metallic3, _Smoothness3, _HasMask3, w.w, albedo, normalTS, mrs);

                float3 bitangent = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
                float3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalize(normalTS),
                    half3x3(input.tangentWS.xyz, bitangent, input.normalWS)));

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = mrs.x;
                surface.occlusion = mrs.y;
                surface.smoothness = mrs.z;
                surface.normalTS = normalTS;
                surface.alpha = 1;
            #if defined(_DBUFFER)
                ApplyDecalToSurfaceData(input.positionCS, surface, inputData);
            #endif
                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
        UsePass "Universal Render Pipeline/Lit/DEPTHONLY"
        UsePass "Universal Render Pipeline/Lit/DEPTHNORMALS"
    }
}

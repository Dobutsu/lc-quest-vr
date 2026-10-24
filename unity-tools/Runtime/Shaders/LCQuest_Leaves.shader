// The forest moons' trees (alpha-clipped URP Lit: bark and leaves in one cutout atlas) at a fraction of Lit's cost,
// for QuestPerformance.CheapLeaves (#11): near trees stack many leaf layers, and with alpha test the GPU shades every
// one of them, so the per-fragment cost is what counts. Here a fragment is the texture, the cutout, the sun's diffuse
// light with its shadow and the fog; the sky's ambient and the local lights (the flashlight, lamps) are summed per
// vertex, without their shadows (_LCQuestLeavesPixelLights 1: the local lights per pixel, still diffuse only). No
// specular, no normal map. The material block is Lit's (LitInput.hlsl), so the
// materials keep their values and the SRP batcher; shadows and depth are Lit's own passes.
Shader "LCQuest/Leaves"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor ("Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Cull [_Cull]

        Pass
        {
            Name "Leaves"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float _LCQuestLeavesPixelLights;

            // The local lights' diffuse light at a point; screenUV picks the Forward+ cluster
            half3 LocalLights(float3 positionWS, half3 normalWS, float2 screenUV)
            {
                half3 light = 0;
            #if defined(_FORWARD_PLUS)
                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.normalizedScreenSpaceUV = saturate(screenUV);
                uint lightsCount = 0;
                LIGHT_LOOP_BEGIN(lightsCount)
                    Light local = GetAdditionalLight(lightIndex, positionWS);
                    light += local.color * (local.distanceAttenuation * saturate(dot(normalWS, local.direction)));
                LIGHT_LOOP_END
            #else
                uint lightsCount = GetAdditionalLightsCount();
                for (uint i = 0; i < lightsCount; i++)
                {
                    Light local = GetAdditionalLight(i, positionWS);
                    light += local.color * (local.distanceAttenuation * saturate(dot(normalWS, local.direction)));
                }
            #endif
                return light;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half4 vertexLightAndFog : TEXCOORD3;  // rgb: ambient + local lights, a: fog factor
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                half3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.positionCS = position.positionCS;
                o.positionWS = position.positionWS;
                o.normalWS = normalWS;
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                half3 light = SampleSH(normalWS);
                UNITY_BRANCH
                if (_LCQuestLeavesPixelLights <= 0)
                {
                    float2 ndc = position.positionCS.xy / position.positionCS.w * 0.5 + 0.5;
                    if (_ProjectionParams.x < 0) ndc.y = 1 - ndc.y;
                    light += LocalLights(position.positionWS, normalWS, ndc);
                }
                o.vertexLightAndFog = half4(light, ComputeFogFactor(position.positionCS.z));
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                clip(albedo.a - _Cutoff);

                half3 normalWS = normalize(input.normalWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 light = input.vertexLightAndFog.rgb +
                              sun.color * (sun.distanceAttenuation * sun.shadowAttenuation * saturate(dot(normalWS, sun.direction)));
                UNITY_BRANCH
                if (_LCQuestLeavesPixelLights > 0)
                    light += LocalLights(input.positionWS, normalWS, GetNormalizedScreenSpaceUV(input.positionCS));
                return half4(MixFog(albedo.rgb * light, input.vertexLightAndFog.a), 1);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/SHADOWCASTER"
        UsePass "Universal Render Pipeline/Lit/DEPTHONLY"
    }
}

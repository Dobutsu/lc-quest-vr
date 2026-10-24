// The glow HDRP's volumetric fog puts around lamps, as an additive camera-facing quad (HdrpBridge places and colours
// them): a soft radial falloff, brighter towards the centre like light scattered close to its source.
Shader "LCQuest/Halo"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+50" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Halo"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv * 2 - 1;
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float r2 = dot(input.uv, input.uv);
                // ~1/(1+k r^2) falloff, cut to zero at the quad's edge
                float glow = saturate(1 - r2) * (1 / (1 + 12 * r2));
                return half4(UNITY_ACCESS_INSTANCED_PROP(Props, _Color).rgb * glow, 1);
            }
            ENDHLSL
        }
    }
}

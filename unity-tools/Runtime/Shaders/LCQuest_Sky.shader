// HDRP's HDRI sky as HDRP shows it: the cubemap (exposure, rotation) seen through the height fog, which HDRP also
// applies to the sky (up to the fog's max distance). URP's skyboxes are never fogged, and in Lethal Company the haze
// is most of what the sky looks like. The fog values come from HdrpBridge (unity-tools/Runtime/HdrpBridge.cs):
//   _LCQuestFog: x extinction at the base height (1/m), y base height, z scale height (m), w max fog distance (m)
// _FogAmount 0 renders the bare sky (the ambient probe is computed from it, as HDRP computes it from the sky alone).
Shader "LCQuest/Sky"
{
    Properties
    {
        [NoScaleOffset] _Tex ("Cubemap", Cube) = "grey" {}
        _Exposure ("Exposure", Float) = 1
        _Rotation ("Rotation", Range(0, 360)) = 0
        _FogAmount ("Fog Amount", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            samplerCUBE _Tex;
            half4 _Tex_HDR;
            float _Exposure, _Rotation, _FogAmount;
            float4 _LCQuestFog;

            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            float3 Rotate(float3 v, float degrees)
            {
                float s, c;
                sincos(degrees * UNITY_PI / 180.0, s, c);
                return float3(c * v.x - s * v.z, v.y, s * v.x + c * v.z);
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            // Optical depth along a ray from the camera through HDRP's height fog (uniform below the base height,
            // exponential above it), up to the max fog distance
            float OpticalDepth(float3 dir)
            {
                float k = _LCQuestFog.x, base = _LCQuestFog.y, H = max(_LCQuestFog.z, 0.01), L = _LCQuestFog.w;
                float y0 = _WorldSpaceCameraPos.y, dy = dir.y;
                float tCross = abs(dy) > 1e-4 ? (base - y0) / dy : -1;
                float below, t0, t1;
                if (y0 < base)
                {
                    below = dy > 0 ? min(tCross, L) : L;
                    t0 = below;
                    t1 = L;
                }
                else
                {
                    below = dy < 0 && tCross < L ? L - tCross : 0;
                    t0 = 0;
                    t1 = dy < 0 ? min(tCross, L) : L;
                }
                float above = 0;
                if (t1 > t0)
                {
                    float h0 = max(0, y0 + t0 * dy - base), h1 = max(0, y0 + t1 * dy - base);
                    above = abs(dy) > 1e-4 ? H / abs(dy) * abs(exp(-h0 / H) - exp(-h1 / H)) : (t1 - t0) * exp(-h0 / H);
                }
                return k * (below + above);
            }

            half4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                half3 sky = DecodeHDR(texCUBE(_Tex, Rotate(dir, _Rotation)), _Tex_HDR) * _Exposure;
                float transmittance = exp(-OpticalDepth(dir));
                half3 color = lerp(sky, lerp(unity_FogColor.rgb, sky, transmittance), _FogAmount);
                return half4(color, 1);
            }
            ENDCG
        }
    }
}

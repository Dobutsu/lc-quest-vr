// A cubemap unwrapped to latitude/longitude (u: azimuth 0..360, v: polar angle 0..180 from up), HDR-decoded, rotated
// about the vertical axis like LCQuest/Sky. HdrpBridge reads it back to project HDRP skies into an ambient probe.
Shader "Hidden/LCQuest/CubeToLatLong"
{
    Properties
    {
        [NoScaleOffset] _Tex ("Cubemap", Cube) = "black" {}
        _Rotation ("Rotation", Float) = 0
    }

    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            samplerCUBE _Tex;
            half4 _Tex_HDR;
            float _Rotation;

            float4 frag(v2f_img i) : SV_Target
            {
                float phi = i.uv.x * 2 * UNITY_PI, theta = (1 - i.uv.y) * UNITY_PI;
                float3 dir = float3(sin(theta) * cos(phi), cos(theta), sin(theta) * sin(phi));
                float s, c;
                sincos(_Rotation * UNITY_PI / 180.0, s, c);
                dir = float3(c * dir.x - s * dir.z, dir.y, s * dir.x + c * dir.z);
                return float4(DecodeHDR(texCUBElod(_Tex, float4(dir, 0)), _Tex_HDR), 1);
            }
            ENDCG
        }
    }
}

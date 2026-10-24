// Unity's UI-Default (2022.3) with Soft Mask: the shared body of the four Hidden/UI Default (Soft Masked) shaders.
// SOFTMASK_ETC1: alpha from _AlphaTex; SOFTMASK_PREMULTIPLIED: the colour premultiplied, the mask scales all of it.
#include "UnityCG.cginc"
#include "UnityUI.cginc"
#include "SoftMaskQuest.hlsl"

#pragma multi_compile_local _ UNITY_UI_CLIP_RECT
#pragma multi_compile_local _ UNITY_UI_ALPHACLIP
#pragma multi_compile_local SOFTMASK_SIMPLE SOFTMASK_SLICED SOFTMASK_TILED

struct appdata_t
{
    float4 vertex : POSITION;
    float4 color : COLOR;
    float2 texcoord : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct v2f
{
    float4 vertex : SV_POSITION;
    fixed4 color : COLOR;
    float2 texcoord : TEXCOORD0;
    float4 worldPosition : TEXCOORD1;
    float4 maskPosition : TEXCOORD2;
    UNITY_VERTEX_OUTPUT_STEREO
};

sampler2D _MainTex;
sampler2D _AlphaTex;
fixed4 _Color;
fixed4 _TextureSampleAdd;
float4 _ClipRect;
float4 _MainTex_ST;

v2f vert(appdata_t v)
{
    v2f OUT;
    UNITY_SETUP_INSTANCE_ID(v);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
    OUT.worldPosition = v.vertex;
    OUT.vertex = UnityObjectToClipPos(v.vertex);
    OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
    OUT.color = v.color * _Color;
    // UI vertices come in the canvas's space; Soft Mask's matrix takes them from there to the mask's
    OUT.maskPosition = mul(_SoftMask_WorldToMask, v.vertex);
    return OUT;
}

fixed4 frag(v2f IN) : SV_Target
{
    half4 color = tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd;
#if SOFTMASK_ETC1
    color.a = tex2D(_AlphaTex, IN.texcoord).r;
#endif
    color *= IN.color;
    float mask = SoftMask_GetMask(IN.maskPosition.xy);
#if SOFTMASK_PREMULTIPLIED
    color.rgb *= color.a;
    color *= mask;
#else
    color.a *= mask;
#endif
#ifdef UNITY_UI_CLIP_RECT
    color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
    #if SOFTMASK_PREMULTIPLIED
    color.rgb *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
    #endif
#endif
#ifdef UNITY_UI_ALPHACLIP
    clip(color.a - 0.001);
#endif
    return color;
}

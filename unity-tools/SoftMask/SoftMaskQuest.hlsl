// The game's Soft Mask (com.olegknyazev.softmask, MIT) shaders, rewritten: the export had placeholders, so the
// masked UI drew as plain rectangles (the compass on the right hand came out a white bar). Same properties as the DLL
// sets (_SoftMask_*); the mask is sampled in the mask's local space, mapped from its rect to the sprite's UV rect,
// nine-sliced or tiled like the source image.
#ifndef LCQUEST_SOFTMASK_INCLUDED
#define LCQUEST_SOFTMASK_INCLUDED

sampler2D _SoftMask;
float4 _SoftMask_Rect;
float4 _SoftMask_UVRect;
float4x4 _SoftMask_WorldToMask;
float4 _SoftMask_ChannelWeights;
float4 _SoftMask_BorderRect;
float4 _SoftMask_UVBorderRect;
float2 _SoftMask_TileRepeat;
float _SoftMask_InvertMask;
float _SoftMask_InvertOutsides;

float SoftMask_Inset(float x, float x1, float x2, float u1, float u2)
{
    float w = x2 - x1;
    return w != 0 ? lerp(u1, u2, (x - x1) / w) : u1;
}

float SoftMask_Slice(float x, float r1, float b1, float b2, float r2, float u1, float ub1, float ub2, float u2, float repeat)
{
    if (x < b1) return SoftMask_Inset(x, r1, b1, u1, ub1);
    if (x > b2) return SoftMask_Inset(x, b2, r2, ub2, u2);
#if SOFTMASK_TILED
    float t = b2 - b1 != 0 ? frac((x - b1) / (b2 - b1) * repeat) : 0;
    return lerp(ub1, ub2, t);
#else
    return SoftMask_Inset(x, b1, b2, ub1, ub2);
#endif
}

float2 SoftMask_UV(float2 p)
{
#if SOFTMASK_SLICED || SOFTMASK_TILED
    return float2(
        SoftMask_Slice(p.x, _SoftMask_Rect.x, _SoftMask_BorderRect.x, _SoftMask_BorderRect.z, _SoftMask_Rect.z,
                       _SoftMask_UVRect.x, _SoftMask_UVBorderRect.x, _SoftMask_UVBorderRect.z, _SoftMask_UVRect.z, _SoftMask_TileRepeat.x),
        SoftMask_Slice(p.y, _SoftMask_Rect.y, _SoftMask_BorderRect.y, _SoftMask_BorderRect.w, _SoftMask_Rect.w,
                       _SoftMask_UVRect.y, _SoftMask_UVBorderRect.y, _SoftMask_UVBorderRect.w, _SoftMask_UVRect.w, _SoftMask_TileRepeat.y));
#else
    return float2(SoftMask_Inset(p.x, _SoftMask_Rect.x, _SoftMask_Rect.z, _SoftMask_UVRect.x, _SoftMask_UVRect.z),
                  SoftMask_Inset(p.y, _SoftMask_Rect.y, _SoftMask_Rect.w, _SoftMask_UVRect.y, _SoftMask_UVRect.w));
#endif
}

float SoftMask_GetMask(float2 p)
{
    float mask = dot(tex2D(_SoftMask, SoftMask_UV(p)) * _SoftMask_ChannelWeights, 1);
    mask = _SoftMask_InvertMask > 0.5 ? 1 - mask : mask;
    float2 inside = step(_SoftMask_Rect.xy, p) * step(p, _SoftMask_Rect.zw);
    return lerp(_SoftMask_InvertOutsides > 0.5 ? 1 : 0, mask, inside.x * inside.y);
}

#endif

// Display.hlsl — display texture → swap chain back buffer
// Supports pan/zoom via ZoomCB + Lanczos-2 high-quality upsampling.

cbuffer ZoomCB : register(b0)
{
    float2 uvOffset;   // top-left of viewport in UV space   (0,0 = no zoom)
    float2 uvScale;    // size of viewport in UV space        (1,1 = no zoom)
    float2 srcTexel;   // 1.0 / source texture size in pixels
    float  zoomLevel;  // current zoom factor (1.0 = no zoom)
    float  _pad;
};

Texture2D<float4> displayTex    : register(t0);
SamplerState      linearSampler : register(s0);

struct VSOut
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VSOut VS(uint vid : SV_VertexID)
{
    float2 p = float2((vid << 1) & 2, vid & 2);
    VSOut o;
    o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    o.uv  = float2(p.x, 1.0 - p.y);
    return o;
}

// Normalised sinc: sin(pi*x)/(pi*x)
float nsinc(float x)
{
    float px = 3.14159265 * x;
    return abs(x) < 1e-4 ? 1.0 : sin(px) / px;
}

// Lanczos-2 weight
float L2(float x)
{
    return abs(x) < 2.0 ? nsinc(x) * nsinc(x * 0.5) : 0.0;
}

// 4×4 Lanczos-2 tap — avoids GPU pipeline stall vs SampleLevel
float4 SampleLanczos(float2 uv)
{
    float2 tc   = uv / srcTexel - 0.5;
    float2 base = floor(tc);
    float2 f    = tc - base;

    float4 sum  = 0;
    float  wsum = 0;

    [unroll] for (int dy = -1; dy <= 2; dy++)
    {
        float wy = L2(dy - f.y);
        [unroll] for (int dx = -1; dx <= 2; dx++)
        {
            float  w = L2(dx - f.x) * wy;
            float2 s = (base + float2(dx, dy) + 0.5) * srcTexel;
            sum  += w * displayTex.SampleLevel(linearSampler, s, 0);
            wsum += w;
        }
    }
    return sum / max(wsum, 1e-4);
}

float4 PS(VSOut i) : SV_Target
{
    float2 srcUV = uvOffset + i.uv * uvScale;
    float4 c = (zoomLevel > 1.49) ? SampleLanczos(srcUV)
                                   : displayTex.Sample(linearSampler, srcUV);
    return float4(c.rgb, 1.0);
}

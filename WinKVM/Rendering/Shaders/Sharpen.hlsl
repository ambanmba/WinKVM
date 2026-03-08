// Sharpen.hlsl — Contrast Adaptive Sharpening (CAS) post-processing pass.
// Runs as a compute shader on the Adreno GPU after YCbCr→RGB decode.
// Selectively sharpens edges without amplifying compression artefacts.
// Based on AMD FidelityFX CAS (MIT licence).

Texture2D<float4>   inputTex  : register(t0);
RWTexture2D<float4> outputTex : register(u0);

cbuffer CasConstants : register(b0)
{
    float2 texSize;    // (width, height) of the texture
    float  sharpness;  // 0.0 = off, 1.0 = maximum (default 0.6)
    float  _pad;
};

[numthreads(8, 8, 1)]
void CS(uint3 tid : SV_DispatchThreadID)
{
    uint2 xy = tid.xy;
    if (xy.x >= (uint)texSize.x || xy.y >= (uint)texSize.y) return;

    // Load 3×3 neighbourhood
    float3 a = inputTex[uint2(xy.x - 1, xy.y - 1)].rgb;
    float3 b = inputTex[uint2(xy.x,     xy.y - 1)].rgb;
    float3 c = inputTex[uint2(xy.x + 1, xy.y - 1)].rgb;
    float3 d = inputTex[uint2(xy.x - 1, xy.y    )].rgb;
    float3 e = inputTex[uint2(xy.x,     xy.y    )].rgb;
    float3 f = inputTex[uint2(xy.x + 1, xy.y    )].rgb;
    float3 g = inputTex[uint2(xy.x - 1, xy.y + 1)].rgb;
    float3 h = inputTex[uint2(xy.x,     xy.y + 1)].rgb;
    float3 i = inputTex[uint2(xy.x + 1, xy.y + 1)].rgb;

    // CAS: compute local min/max of the + cross neighbours
    float3 mnRGB = min(min(min(d, e), min(f, b)), h);
    float3 mxRGB = max(max(max(d, e), max(f, b)), h);

    // Sharpening weight — proportional to local contrast (no over-sharpening on flat areas)
    float3 rcpMx  = rcp(mxRGB + 1e-6);
    float3 amp    = saturate(min(mnRGB, 2.0 - mxRGB) * rcpMx);
    amp           = rsqrt(amp + 1e-6);

    float  peak   = -rcp(lerp(8.0, 5.0, saturate(sharpness)));
    float3 w      = amp * peak;
    float3 rcpW   = rcp(4.0 * w + 1.0);

    // Apply sharpening: blend centre with + cross weighted by local contrast
    float3 result = saturate(((b + d + f + h) * w + e) * rcpW);

    outputTex[xy] = float4(result, 1.0);
}

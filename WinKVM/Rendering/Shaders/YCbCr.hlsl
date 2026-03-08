// YCbCr.hlsl — GPU: YCbCr 4:2:0 → BGRA conversion
// Runs as a full-screen pixel shader; outputs directly to the display texture.
// BT.601 coefficients matching the Raritan Java client.

Texture2D<float> yTex  : register(t0);
Texture2D<float> cbTex : register(t1);
Texture2D<float> crTex : register(t2);
SamplerState     linearSampler : register(s0);

struct VSOut
{
    float4 pos     : SV_POSITION;
    float2 uv      : TEXCOORD0;
};

// Fullscreen triangle (no vertex buffer needed)
VSOut VS(uint vid : SV_VertexID)
{
    VSOut o;
    float2 p  = float2((vid << 1) & 2, vid & 2);
    o.pos     = float4(p * 2.0 - 1.0, 0.0, 1.0);
    o.uv      = float2(p.x, 1.0 - p.y);
    return o;
}

float4 PS(VSOut i) : SV_Target
{
    float y  = yTex .Sample(linearSampler, i.uv).r;
    float cb = cbTex.Sample(linearSampler, i.uv).r;
    float cr = crTex.Sample(linearSampler, i.uv).r;

    float cbS = cb - 0.5;
    float crS = cr - 0.5;

    // BT.601 full-range
    float r = saturate(y + 1.402  * crS);
    float g = saturate(y - 0.344  * cbS - 0.714 * crS);
    float b = saturate(y + 1.772  * cbS);

    // Output RGBA — D3D11 maps float4(r,g,b,a) to (R,G,B,A) channels regardless of BGRA memory layout
    return float4(r, g, b, 1.0);
}

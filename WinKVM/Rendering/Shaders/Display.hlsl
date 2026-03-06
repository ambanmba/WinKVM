// Display.hlsl — GPU: passthrough — displayTexture → swap chain back buffer

Texture2D<float4> displayTex : register(t0);
SamplerState      linearSampler : register(s0);

struct VSOut
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

VSOut VS(uint vid : SV_VertexID)
{
    VSOut o;
    float2 p = float2((vid << 1) & 2, vid & 2);
    o.pos    = float4(p * 2.0 - 1.0, 0.0, 1.0);
    o.uv     = float2(p.x, 1.0 - p.y);
    return o;
}

float4 PS(VSOut i) : SV_Target
{
    float4 c = displayTex.Sample(linearSampler, i.uv);
    return float4(c.rgb, 1.0); // force alpha = 1 for opaque display
}

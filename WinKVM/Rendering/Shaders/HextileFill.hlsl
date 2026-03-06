// HextileFill.hlsl — GPU compute: batch solid-colour fills for Hextile encoding
// One thread per FillCommand. Fills rectangular regions of the display texture.
// Mirrors the Metal hextile_fill kernel from SwiftKVM.

struct FillCommand
{
    uint2 origin;  // x, y packed as two uint16 in one uint32
    uint2 size;    // w, h packed
    uint  color;   // BGRA packed: B=bits[7:0], G=bits[15:8], R=bits[23:16], A=bits[31:24]
};

StructuredBuffer<FillCommand> cmds    : register(t0);
RWTexture2D<float4>           display : register(u0);

cbuffer Constants : register(b0)
{
    uint cmdCount;
};

[numthreads(64, 1, 1)]
void CS(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= cmdCount) return;

    FillCommand cmd = cmds[tid.x];

    uint x = cmd.origin.x & 0xFFFF;
    uint y = cmd.origin.x >> 16;
    uint w = cmd.size.x & 0xFFFF;
    uint h = cmd.size.x >> 16;

    float b = float( cmd.color        & 0xFF) / 255.0;
    float g = float((cmd.color >>  8) & 0xFF) / 255.0;
    float r = float((cmd.color >> 16) & 0xFF) / 255.0;
    float a = float((cmd.color >> 24) & 0xFF) / 255.0;
    float4 col = float4(b, g, r, a);  // BGRA layout matches DXGI_FORMAT_B8G8R8A8_UNORM

    for (uint py = y; py < y + h; py++)
    for (uint px = x; px < x + w; px++)
        display[uint2(px, py)] = col;
}

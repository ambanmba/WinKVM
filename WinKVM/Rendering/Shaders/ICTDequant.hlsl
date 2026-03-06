// ICTDequant.hlsl — GPU compute: parallel IDCT for ICT tile decoding
// Each thread processes one 8×8 block. Significantly faster than CPU
// ConcurrentPerform for large frames (2560×1440 = 14400 tiles/frame).
//
// Input:  dequantized DCT coefficients (float, zig-zag ordered, 64 floats/block)
// Output: spatial-domain bytes written into the appropriate Y/Cb/Cr plane texture.

StructuredBuffer<float>        coeffBuf : register(t0);  // [blockCount * 64] floats
RWTexture2D<unorm float>       yPlane   : register(u0);
RWTexture2D<unorm float>       cbPlane  : register(u1);
RWTexture2D<unorm float>       crPlane  : register(u2);

cbuffer Constants : register(b0)
{
    uint tilesX;    // tiles across frame width
    uint tilesY;    // tiles across frame height
    uint frameW;
    uint frameH;
};

static const float W1 = 0.98078528, W2 = 0.92387953, W3 = 0.83146961;
static const float W4 = 0.70710678, W5 = 0.55557023, W6 = 0.38268343, W7 = 0.19509032;

void IDCT8x8(in float coeff[64], out float result[64])
{
    float tmp[64];

    // Row pass
    [unroll] for (int r = 0; r < 8; r++)
    {
        int i = r * 8;
        float s0=coeff[i],s1=coeff[i+1],s2=coeff[i+2],s3=coeff[i+3];
        float s4=coeff[i+4],s5=coeff[i+5],s6=coeff[i+6],s7=coeff[i+7];
        float t0=(s0+s4)*W4, t1=(s0-s4)*W4, t2=s2*W6-s6*W2, t3=s2*W2+s6*W6;
        float p0=t0+t3,p1=t1+t2,p2=t1-t2,p3=t0-t3;
        float q0=s1*W7-s7*W1,q1=s3*W3-s5*W5,q2=s1*W1+s7*W7,q3=s3*W5+s5*W3;
        float r0=q0+q1,r1=q2+q3,r2=q2-q3,r3=q0-q1;
        float u0=(r0-r1)*W4,u1=(r2+r3)*W4;
        tmp[i+0]=p0+r1; tmp[i+1]=p1+u1+r3; tmp[i+2]=p2+u0; tmp[i+3]=p3-u1+r0-r1;
        tmp[i+4]=p3+u1-r0+r1; tmp[i+5]=p2-u0; tmp[i+6]=p1-u1-r3; tmp[i+7]=p0-r1;
    }

    // Column pass
    [unroll] for (int c = 0; c < 8; c++)
    {
        float s0=tmp[c],s1=tmp[c+8],s2=tmp[c+16],s3=tmp[c+24];
        float s4=tmp[c+32],s5=tmp[c+40],s6=tmp[c+48],s7=tmp[c+56];
        float t0=(s0+s4)*W4,t1=(s0-s4)*W4,t2=s2*W6-s6*W2,t3=s2*W2+s6*W6;
        float p0=t0+t3,p1=t1+t2,p2=t1-t2,p3=t0-t3;
        float q0=s1*W7-s7*W1,q1=s3*W3-s5*W5,q2=s1*W1+s7*W7,q3=s3*W5+s5*W3;
        float r0=q0+q1,r1=q2+q3,r2=q2-q3,r3=q0-q1;
        float u0=(r0-r1)*W4,u1=(r2+r3)*W4;
        result[c+0*8]=p0+r1; result[c+1*8]=p1+u1+r3; result[c+2*8]=p2+u0;
        result[c+3*8]=p3-u1+r0-r1; result[c+4*8]=p3+u1-r0+r1;
        result[c+5*8]=p2-u0; result[c+6*8]=p1-u1-r3; result[c+7*8]=p0-r1;
    }
}

// Each thread: one 8×8 luma block (blocks 0-3 in a 16×16 tile) or one chroma block
[numthreads(64, 1, 1)]
void CS(uint3 tid : SV_DispatchThreadID)
{
    // Total blocks = tilesX * tilesY * 6 (4 Y + 1 Cb + 1 Cr per tile)
    uint totalBlocks = tilesX * tilesY * 6;
    if (tid.x >= totalBlocks) return;

    uint blockInTile = tid.x % 6;
    uint tileIdx     = tid.x / 6;
    uint tileX       = tileIdx % tilesX;
    uint tileY       = tileIdx / tilesX;

    // Unpack 64 coefficients for this block
    uint base = tid.x * 64;
    float coeff[64];
    [unroll] for (int k = 0; k < 64; k++) coeff[k] = coeffBuf[base + k];

    float result[64];
    IDCT8x8(coeff, result);

    if (blockInTile < 4)
    {
        // Luma block
        uint bx = (blockInTile & 1) * 8;
        uint by = (blockInTile >> 1) * 8;
        uint dstX = tileX * 16 + bx;
        uint dstY = tileY * 16 + by;
        [unroll] for (int row = 0; row < 8; row++)
        [unroll] for (int col = 0; col < 8; col++)
        {
            uint px = dstX + col, py = dstY + row;
            if (px < frameW && py < frameH)
                yPlane[uint2(px, py)] = saturate((result[row*8+col] + 128.0) / 255.0);
        }
    }
    else
    {
        // Chroma block (Cb or Cr)
        uint cw = (frameW + 1) / 2, ch = (frameH + 1) / 2;
        uint dstX = tileX * 8, dstY = tileY * 8;
        RWTexture2D<unorm float> plane = (blockInTile == 4) ? cbPlane : crPlane;
        [unroll] for (int row = 0; row < 8; row++)
        [unroll] for (int col = 0; col < 8; col++)
        {
            uint px = dstX + col, py = dstY + row;
            if (px < cw && py < ch)
                plane[uint2(px, py)] = saturate((result[row*8+col] + 128.0) / 255.0);
        }
    }
}

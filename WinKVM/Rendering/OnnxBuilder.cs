using System.Runtime.InteropServices;

namespace WinKVM.Rendering;

/// Generates a minimal ONNX protobuf binary for a depthwise 3×3 sharpening
/// convolution without any external file or NuGet dependency.
///
/// Model: Conv(X, W, B) → Y
///   X: float[1, 3, H, W]   — input frame (NCHW, normalised 0..1)
///   W: float[3, 1, 3, 3]   — depthwise kernel (group=3)
///   B: float[3]             — bias (zeros)
///   Y: float[1, 3, H, W]   — sharpened frame
///
/// Kernel (unsharp-mask, k = sharpStrength):
///   [ 0,  -k,   0 ]
///   [-k, 1+4k, -k ]
///   [ 0,  -k,   0 ]
internal static class OnnxBuilder
{
    // ── Protobuf wire helpers ────────────────────────────────────────────────

    private static void WriteVarintRaw(List<byte> buf, ulong v)
    {
        while (v >= 0x80) { buf.Add((byte)((v & 0x7F) | 0x80)); v >>= 7; }
        buf.Add((byte)v);
    }

    // field tag = (fieldNumber << 3) | wireType
    private static void WriteVarintField(List<byte> buf, int field, long v)
    {
        WriteVarintRaw(buf, (ulong)((field << 3) | 0)); // wire type 0 = varint
        WriteVarintRaw(buf, (ulong)v);
    }

    private static void WriteBytesField(List<byte> buf, int field, byte[] data)
    {
        WriteVarintRaw(buf, (ulong)((field << 3) | 2)); // wire type 2 = len-delim
        WriteVarintRaw(buf, (ulong)data.Length);
        buf.AddRange(data);
    }

    private static void WriteBytesField(List<byte> buf, int field, List<byte> data)
        => WriteBytesField(buf, field, data.ToArray());

    private static void WriteStringField(List<byte> buf, int field, string s)
        => WriteBytesField(buf, field, System.Text.Encoding.UTF8.GetBytes(s));

    // Write packed repeated int64 (wire type 2, length-delimited blob of varints).
    // proto3 defaults to packed for repeated numeric fields; WinML expects this.
    private static void WritePackedInt64s(List<byte> buf, int field, params long[] vals)
    {
        var data = new List<byte>();
        foreach (var v in vals) WriteVarintRaw(data, (ulong)v);
        WriteBytesField(buf, field, data.ToArray());
    }

    // Non-packed variant for fields that explicitly need it (e.g. TensorProto.dims).
    private static void WriteRepeatedInt64(List<byte> buf, int field, params long[] vals)
    {
        foreach (var v in vals) WriteVarintField(buf, field, v);
    }

    // ── ONNX node/attribute builders ─────────────────────────────────────────

    private static byte[] BuildAttrInt(string name, long v)
    {
        var a = new List<byte>();
        WriteStringField(a, 1, name);   // name
        WriteVarintField(a, 20, 2);     // type = INT (enum value 2 = INT)
        WriteVarintField(a, 4, v);      // i (field 4, wire type 0 = varint)
        return a.ToArray();
    }

    private static byte[] BuildAttrString(string name, string val)
    {
        var a = new List<byte>();
        WriteStringField(a, 1, name);   // name
        WriteVarintField(a, 20, 3);     // type = STRING (enum value 3)
        WriteBytesField(a, 6, System.Text.Encoding.UTF8.GetBytes(val)); // s (field 6)
        return a.ToArray();
    }

    private static byte[] BuildAttrInts(string name, params long[] vals)
    {
        var a = new List<byte>();
        WriteStringField(a, 1, name);   // name
        WriteVarintField(a, 20, 7);     // type = INTS (enum value 7 = INTS)
        // ints field 7: packed repeated int64 (proto3 default, required by WinML)
        WritePackedInt64s(a, 7, vals);
        return a.ToArray();
    }

    // Simple op nodes for unsharp mask pipeline

    private static byte[] BuildNode(string opType, string name, string[] inputs, string[] outputs,
                                    params byte[][] attributes)
    {
        var n = new List<byte>();
        foreach (var inp in inputs)  WriteStringField(n, 1, inp);
        foreach (var outp in outputs) WriteStringField(n, 2, outp);
        WriteStringField(n, 3, name);
        WriteStringField(n, 4, opType);
        foreach (var attr in attributes) WriteBytesField(n, 5, attr);
        return n.ToArray();
    }

    // Float32 tensor initializer
    private static byte[] BuildTensor(string name, long[] dims, float[] vals)
    {
        var t = new List<byte>();
        WriteRepeatedInt64(t, 1, dims); // dims (repeated int64, non-packed)
        WriteVarintField(t, 2, 1);                 // data_type = FLOAT
        // float_data as raw bytes (field 4, repeated float → wire type 5 each,
        // but ONNX uses raw_data field 9 as a blob for efficiency)
        var raw = new byte[vals.Length * 4];
        Buffer.BlockCopy(vals, 0, raw, 0, raw.Length);
        WriteBytesField(t, 9, raw);                // raw_data
        WriteStringField(t, 8, name);              // name
        return t.ToArray();
    }

    // ValueInfoProto with concrete dims [n, c, h, w] — WinML requires static shapes
    private static byte[] BuildValueInfo(string name, long n, long c, long h = 1440, long w = 2560)
    {
        var shDim1 = new List<byte>(); WriteVarintField(shDim1, 1, n);
        var shDim2 = new List<byte>(); WriteVarintField(shDim2, 1, c);
        var shDimH = new List<byte>(); WriteVarintField(shDimH, 1, h);
        var shDimW = new List<byte>(); WriteVarintField(shDimW, 1, w);

        var shape = new List<byte>();
        WriteBytesField(shape, 1, shDim1); WriteBytesField(shape, 1, shDim2);
        WriteBytesField(shape, 1, shDimH); WriteBytesField(shape, 1, shDimW);

        var tensor = new List<byte>();
        WriteVarintField(tensor, 1, 1);            // elem_type = FLOAT
        WriteBytesField(tensor, 2, shape);          // shape

        var typeProto = new List<byte>();
        WriteBytesField(typeProto, 1, tensor);      // tensor_type

        var vi = new List<byte>();
        WriteStringField(vi, 1, name);
        WriteBytesField(vi, 2, typeProto);
        return vi.ToArray();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// Build ONNX 1×1 colour-enhancement Conv model (runs on Hexagon HTP via QNN).
    /// Applies a 3×3 RGB colour matrix that boosts saturation and contrast.
    /// strength: 0 = identity, 1 = vivid (default 0.5 = moderate boost).
    public static byte[] BuildDepthwiseSharpen(int channels = 3, float strength = 0.5f,
                                               int width = 2560, int height = 1440)
    {
        // Colour enhancement matrix: boost diagonal (self-channel) and
        // slightly subtract cross-channels to increase saturation.
        // At strength=0: identity. At strength=1: vivid saturation boost.
        float diag  = 1.0f + strength * 0.3f;   // e.g. 1.15 at strength=0.5
        float cross = -strength * 0.1f;           // e.g. -0.05 at strength=0.5

        var wData = new float[channels * channels * 1 * 1];
        for (int i = 0; i < channels; i++)
            for (int j = 0; j < channels; j++)
                wData[i * channels + j] = (i == j) ? diag : cross;

        var bData = new float[channels]; // zero bias

        var graph = new List<byte>();

        // 1×1 Conv: X[1,3,H,W] → ColourMatrix(W[3,3,1,1]) → Y[1,3,H,W]
        WriteBytesField(graph, 1, BuildNode("Conv", "sharpen",
            new[] { "X", "W", "B" }, new[] { "Y" }));
        WriteBytesField(graph, 5, BuildTensor("W", new long[] { channels, channels, 1, 1 }, wData));
        WriteBytesField(graph, 5, BuildTensor("B", new long[] { channels }, bData));
        WriteBytesField(graph, 11, BuildValueInfo("X", 1, channels, height, width));   // input
        WriteBytesField(graph, 12, BuildValueInfo("Y", 1, channels, height, width));   // output
        WriteStringField(graph, 2, "sharpen_graph");

        var opset = new List<byte>();
        WriteStringField(opset, 1, "");     // domain = "" (standard ONNX)
        WriteVarintField(opset, 2, 13);     // opset 13 — Conv kernel_shape inference supported

        var model = new List<byte>();
        WriteVarintField(model, 1, 7);      // ir_version = 7 (WinML max supported)
        WriteBytesField(model, 8, opset);   // opset_import
        WriteBytesField(model, 7, graph);   // graph
        WriteStringField(model, 2, "sharpen"); // domain
        WriteStringField(model, 3, "WinKVM sharpening");  // model_version string

        return model.ToArray();
    }
}

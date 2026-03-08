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

    // Packed repeated int64 (for tensor dims and attribute ints)
    private static byte[] PackedInt64s(params long[] vals)
    {
        var p = new List<byte>();
        foreach (var v in vals) WriteVarintRaw(p, (ulong)v);
        return p.ToArray();
    }

    // ── ONNX node/attribute builders ─────────────────────────────────────────

    private static byte[] BuildAttrInt(string name, long v)
    {
        var a = new List<byte>();
        WriteStringField(a, 1, name);           // name
        WriteVarintField(a, 20, 1);             // type = INT
        WriteVarintField(a, 4, v);              // i
        return a.ToArray();
    }

    private static byte[] BuildAttrInts(string name, params long[] vals)
    {
        var a = new List<byte>();
        WriteStringField(a, 1, name);           // name
        WriteVarintField(a, 20, 7);             // type = INTS
        WriteBytesField(a, 7, PackedInt64s(vals)); // ints (packed)
        return a.ToArray();
    }

    private static byte[] BuildConvNode(string x, string w, string b, string y)
    {
        var n = new List<byte>();
        WriteStringField(n, 1, x);              // input: X
        WriteStringField(n, 1, w);              // input: W
        WriteStringField(n, 1, b);              // input: B
        WriteStringField(n, 2, y);              // output: Y
        WriteStringField(n, 3, "sharpen");      // name
        WriteStringField(n, 4, "Conv");         // op_type
        WriteBytesField(n, 5, BuildAttrInts("dilations", 1, 1));
        WriteBytesField(n, 5, BuildAttrInt("group", 3));
        WriteBytesField(n, 5, BuildAttrInts("kernel_shape", 3, 3));
        WriteBytesField(n, 5, BuildAttrInts("pads", 1, 1, 1, 1));
        WriteBytesField(n, 5, BuildAttrInts("strides", 1, 1));
        return n.ToArray();
    }

    // Float32 tensor initializer
    private static byte[] BuildTensor(string name, long[] dims, float[] vals)
    {
        var t = new List<byte>();
        WriteBytesField(t, 1, PackedInt64s(dims)); // dims
        WriteVarintField(t, 2, 1);                 // data_type = FLOAT
        // float_data as raw bytes (field 4, repeated float → wire type 5 each,
        // but ONNX uses raw_data field 9 as a blob for efficiency)
        var raw = new byte[vals.Length * 4];
        Buffer.BlockCopy(vals, 0, raw, 0, raw.Length);
        WriteBytesField(t, 9, raw);                // raw_data
        WriteStringField(t, 8, name);              // name
        return t.ToArray();
    }

    // ValueInfoProto with symbolic H/W dims
    private static byte[] BuildValueInfo(string name, long n, long c)
    {
        // TypeProto.Tensor
        var shDim1 = new List<byte>(); WriteVarintField(shDim1, 1, n);
        var shDim2 = new List<byte>(); WriteVarintField(shDim2, 1, c);
        var shDimH = new List<byte>(); WriteStringField(shDimH, 2, "H");
        var shDimW = new List<byte>(); WriteStringField(shDimW, 2, "W");

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

    /// Build ONNX binary for a depthwise 3×3 sharpening conv.
    /// <param name="channels">Number of image channels (3 for RGB).</param>
    /// <param name="strength">Sharpening strength k (0.25 = moderate).</param>
    public static byte[] BuildDepthwiseSharpen(int channels = 3, float strength = 0.25f)
    {
        // Unsharp-mask kernel: identity + laplacian * strength
        // [ 0,  -k,   0 ]
        // [-k, 1+4k, -k ]
        // [ 0,  -k,   0 ]
        float k = strength;
        float[] k1 = { 0, -k, 0, -k, 1 + 4 * k, -k, 0, -k, 0 };

        // Weight tensor [channels, 1, 3, 3] — same kernel per channel
        var wData = new float[channels * 1 * 3 * 3];
        for (int c = 0; c < channels; c++)
            Array.Copy(k1, 0, wData, c * 9, 9);

        // Bias tensor [channels] — all zeros
        var bData = new float[channels]; // default 0

        var graph = new List<byte>();
        WriteBytesField(graph, 1, BuildConvNode("X", "W", "B", "Y"));  // node
        WriteBytesField(graph, 5, BuildTensor("W", [channels, 1, 3, 3], wData));
        WriteBytesField(graph, 5, BuildTensor("B", [channels], bData));
        WriteBytesField(graph, 11, BuildValueInfo("X", 1, channels));   // input
        WriteBytesField(graph, 12, BuildValueInfo("Y", 1, channels));   // output
        WriteStringField(graph, 2, "sharpen_graph");

        var opset = new List<byte>();
        WriteStringField(opset, 1, "");     // domain = "" (standard ONNX)
        WriteVarintField(opset, 2, 18);     // opset version

        var model = new List<byte>();
        WriteVarintField(model, 1, 8);      // ir_version = 8
        WriteBytesField(model, 8, opset);   // opset_import
        WriteBytesField(model, 7, graph);   // graph
        WriteStringField(model, 2, "sharpen"); // domain
        WriteStringField(model, 3, "WinKVM sharpening");  // model_version string

        return model.ToArray();
    }
}

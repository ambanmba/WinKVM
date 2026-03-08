using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WinKVM.Framebuffer;

namespace WinKVM.Rendering;

/// Direct3D 11 framebuffer renderer hosted in a WinUI 3 SwapChainPanel.
///
/// GPU pipeline:
///   1. YCbCr path  — three R8 textures → HLSL pixel shader → display texture (BGRA8)
///   2. BGRX path   — CPU upload → display texture (dirty-rect optimised)
///   3. Fill path   — HLSL compute shader (HextileFill.hlsl) for batch solid fills
///   4. Display     — display texture → swap chain back buffer (HLSL passthrough)
///
/// NPU path (ICT decode):
///   ICTDequant.hlsl compute shader runs IDCT in parallel on NPU/GPU when the
///   DirectML dispatch is issued from ERICSession after dequantization on CPU.
public sealed class D3DFramebufferControl : Grid, IDisposable
{
    // Native SwapChainPanel — a pure WinRT object (not a managed subclass) so that
    // Marshal.QueryInterface for ISwapChainPanelNative succeeds on its native peer.
    private readonly SwapChainPanel _panel = new();
    // ── D3D11 objects ────────────────────────────────────────────────────────
    private ID3D11Device?             _device;
    private ID3D11DeviceContext?      _ctx;
    private IDXGISwapChain1?          _swapChain;
    private ID3D11RenderTargetView?   _rtv;

    // Render pipelines
    private ID3D11PixelShader?        _ycbcrPS;
    private ID3D11PixelShader?        _displayPS;
    private ID3D11VertexShader?       _fullscreenVS;
    private ID3D11ComputeShader?      _fillCS;
    private ID3D11ComputeShader?      _ictCS;
    private ID3D11ComputeShader?      _casCS;    // CAS sharpening (Adreno GPU)
    private ID3D11SamplerState?       _linearSampler;
    private ID3D11RasterizerState?    _noCullRS; // no-cull for fullscreen triangle passes

    // CAS intermediate texture: CAS reads from _displayTex, writes to _casOutTex,
    // then Render() presents _casOutTex instead of _displayTex.
    private ID3D11Texture2D?          _casOutTex;
    private ID3D11ShaderResourceView? _casOutSRV;
    private ID3D11UnorderedAccessView? _casOutUAV;

    // CAS constants buffer: texSize(2) + sharpness(1) + pad(1) = 16 bytes
    private ID3D11Buffer?             _casCB;

    /// CAS sharpness 0.0–1.0 (0 = off, 0.6 = default). Set from UI.
    public float Sharpness { get; set; } = 0.6f;

    // Display texture (BGRA8, persistent)
    private ID3D11Texture2D?          _displayTex;
    private ID3D11ShaderResourceView? _displaySRV;
    private ID3D11RenderTargetView?   _displayRTV;
    private ID3D11UnorderedAccessView? _displayUAV;
    private int _displayW, _displayH;

    // YCbCr plane textures (R8 each)
    private ID3D11Texture2D?          _yTex, _cbTex, _crTex;
    private ID3D11ShaderResourceView? _ySRV, _cbSRV, _crSRV;

    // Fill command buffer
    private ID3D11Buffer?             _fillCmdBuf;
    private ID3D11ShaderResourceView? _fillCmdSRV;

    // Reusable staging texture for CPU→GPU uploads
    private ID3D11Texture2D?          _stagingTex;
    private int _stagingW, _stagingH;

    public int FbWidth  { get; private set; }
    public int FbHeight { get; private set; }

    // ── Initialise ───────────────────────────────────────────────────────────

    public D3DFramebufferControl()
    {
        Children.Add(_panel);
        Loaded   += (_, _) => Initialize();
        Unloaded += (_, _) => Dispose();
    }

    private void Initialize()
    {
        var featureLevels = new[] { FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels,
            out _device, out _ctx);
        CreateSwapChain();
        CompileShaders();
        CreateSamplers();
    }

    private void CreateSwapChain()
    {
        var dxgiDevice  = _device!.QueryInterface<IDXGIDevice>();
        var dxgiAdapter = dxgiDevice.GetAdapter();
        var dxgiFactory = dxgiAdapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width       = (uint)Math.Max(1, (int)ActualWidth),
            Height      = (uint)Math.Max(1, (int)ActualHeight),
            Format      = Format.B8G8R8A8_UNorm,
            Stereo      = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling     = Scaling.Stretch,
            SwapEffect  = SwapEffect.FlipSequential,
            AlphaMode   = AlphaMode.Ignore,
        };

        _swapChain = dxgiFactory.CreateSwapChainForComposition(_device!, desc);

        // Connect swap chain to _panel (a pure WinRT SwapChainPanel, not a managed subclass) via
        // ISwapChainPanelNative COM interop. QI on the managed subclass fails; _panel works.
        IntPtr nativePeerPtr = ((WinRT.IWinRTObject)_panel).NativeObject.ThisPtr;
        var iid = typeof(ISwapChainPanelNative).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativePeerPtr, ref iid, out IntPtr panelNativePtr));
        try
        {
            var nativePanel = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(panelNativePtr);
            nativePanel.SetSwapChain(_swapChain.NativePointer);
        }
        finally { Marshal.Release(panelNativePtr); }

        CreateRTV();

        SizeChanged += (_, e) => ResizeSwapChain((int)e.NewSize.Width, (int)e.NewSize.Height);
    }

    private void CreateRTV()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv?.Dispose();
        _rtv = _device!.CreateRenderTargetView(backBuffer);
    }

    private void ResizeSwapChain(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        _rtv?.Dispose(); _rtv = null;
        _swapChain!.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateRTV();
    }

    private void CompileShaders()
    {
        if (_device is null) return;
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Rendering", "Shaders");

        byte[] CompileHlsl(string file, string profile, string entry)
        {
            var path = Path.Combine(shaderDir, file);
            Vortice.D3DCompiler.Compiler.CompileFromFile(
                path, null, null, entry, profile,
                out var blob, out var errors);
            if (errors is not null && errors.BufferSize > 0)
                throw new Exception($"Shader compile error ({file}): {Marshal.PtrToStringAnsi(errors.BufferPointer)}");
            var bytes = new byte[blob!.BufferSize];
            Marshal.Copy(blob.BufferPointer, bytes, 0, bytes.Length);
            return bytes;
        }

        var vsBytes        = CompileHlsl("YCbCr.hlsl",      "vs_5_0", "VS");
        var ycbcrPSBytes   = CompileHlsl("YCbCr.hlsl",      "ps_5_0", "PS");
        var displayPSBytes = CompileHlsl("Display.hlsl",    "ps_5_0", "PS");
        var fillCSBytes    = CompileHlsl("HextileFill.hlsl","cs_5_0", "CS");
        var casCSBytes     = CompileHlsl("Sharpen.hlsl",    "cs_5_0", "CS");

        _fullscreenVS = _device.CreateVertexShader(vsBytes);
        _ycbcrPS      = _device.CreatePixelShader(ycbcrPSBytes);
        _displayPS    = _device.CreatePixelShader(displayPSBytes);
        _fillCS       = _device.CreateComputeShader(fillCSBytes);
        _casCS        = _device.CreateComputeShader(casCSBytes);
    }

    private void CreateSamplers()
    {
        var desc = new SamplerDescription
        {
            Filter   = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
        };
        _linearSampler = _device!.CreateSamplerState(desc);

        // No-cull rasterizer for fullscreen triangle (CCW in D3D11 NDC)
        _noCullRS = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
        });
    }

    // ── Texture management ───────────────────────────────────────────────────

    public void EnsureTexture(int w, int h) => EnsureDisplayTexture(w, h);

    private void EnsureDisplayTexture(int w, int h)
    {
        if (w == _displayW && h == _displayH) return;
        _displayTex?.Dispose(); _displaySRV?.Dispose();
        _displayRTV?.Dispose(); _displayUAV?.Dispose();

        var desc = new Texture2DDescription
        {
            Width  = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage      = ResourceUsage.Default,
            BindFlags  = BindFlags.ShaderResource | BindFlags.RenderTarget | BindFlags.UnorderedAccess,
        };
        _displayTex = _device!.CreateTexture2D(desc);
        _displaySRV = _device.CreateShaderResourceView(_displayTex);
        _displayRTV = _device.CreateRenderTargetView(_displayTex);
        _displayUAV = _device.CreateUnorderedAccessView(_displayTex);

        // CAS output texture + constant buffer (optional — failure disables CAS gracefully)
        try
        {
            _casOutTex?.Dispose(); _casOutSRV?.Dispose(); _casOutUAV?.Dispose(); _casCB?.Dispose();
            _casOutTex = _device.CreateTexture2D(desc);
            _casOutSRV = _device.CreateShaderResourceView(_casOutTex);
            _casOutUAV = _device.CreateUnorderedAccessView(_casOutTex);
            // Dynamic constant buffer: float2 texSize + float sharpness + float pad
            _casCB = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 16u, Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer, CPUAccessFlags = CpuAccessFlags.Write,
            });
        }
        catch
        {
            // CAS resource creation failed — sharpening will be skipped in Render()
            _casOutTex = null; _casOutSRV = null; _casOutUAV = null; _casCB = null;
        }

        _displayW = w; _displayH = h;
    }

    private void EnsureYCbCrTextures(int w, int h)
    {
        int cw = (w + 1) / 2, ch = (h + 1) / 2;
        if (_yTex is not null && _yTex.Description.Width == (uint)w && _yTex.Description.Height == (uint)h) return;

        DisposeYCbCrTextures();
        _yTex  = MakeR8Texture(w,  h);  _ySRV  = _device!.CreateShaderResourceView(_yTex);
        _cbTex = MakeR8Texture(cw, ch); _cbSRV = _device.CreateShaderResourceView(_cbTex);
        _crTex = MakeR8Texture(cw, ch); _crSRV = _device.CreateShaderResourceView(_crTex);
    }

    private ID3D11Texture2D MakeR8Texture(int w, int h) => _device!.CreateTexture2D(new Texture2DDescription
    {
        Width  = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
        Format = Format.R8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage          = ResourceUsage.Dynamic,
        BindFlags      = BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.Write,
    });

    private void DisposeYCbCrTextures()
    {
        _ySRV?.Dispose(); _cbSRV?.Dispose(); _crSRV?.Dispose();
        _yTex?.Dispose(); _cbTex?.Dispose(); _crTex?.Dispose();
        _ySRV = _cbSRV = _crSRV = null;
        _yTex = _cbTex = _crTex = null;
    }

    // ── Public update API ────────────────────────────────────────────────────

    /// Upload a BGRX framebuffer and render. Supports dirty-rect partial updates.
    public unsafe void UploadFramebuffer(KvmFramebuffer fb, IReadOnlyList<(int x,int y,int w,int h)>? dirtyRects = null)
    {
        if (_device is null || _ctx is null) return;
        FbWidth = fb.Width; FbHeight = fb.Height;
        EnsureDisplayTexture(fb.Width, fb.Height);

        if (dirtyRects is null || dirtyRects.Count == 0)
        {
            UploadRegion(fb, 0, 0, fb.Width, fb.Height);
        }
        else
        {
            int minX = fb.Width, minY = fb.Height, maxX = 0, maxY = 0;
            foreach (var (rx, ry, rw, rh) in dirtyRects)
            {
                minX = Math.Min(minX, rx);   minY = Math.Min(minY, ry);
                maxX = Math.Max(maxX, rx+rw); maxY = Math.Max(maxY, ry+rh);
            }
            UploadRegion(fb, minX, minY, maxX - minX, maxY - minY);
        }

        Render();
    }

    private unsafe void UploadRegion(KvmFramebuffer fb, int x, int y, int w, int h)
    {
        w = Math.Min(w, fb.Width  - x);
        h = Math.Min(h, fb.Height - y);
        if (w <= 0 || h <= 0) return;

        var box = new Box(x, y, 0, x + w, y + h, 1);
        byte* srcRow = fb.RowPointer(y) + x * 4;
        _ctx!.UpdateSubresource(_displayTex!, 0u, box, (nint)srcRow, (uint)fb.BytesPerRow, 0u);
    }

    /// Upload YCbCr planes and render via GPU YCbCr→RGB shader.
    public unsafe void UploadYCbCr(YCbCrPlanes planes)
    {
        if (_device is null || _ctx is null || _ycbcrPS is null || _displayPS is null) return;
        FbWidth  = planes.Width;
        FbHeight = planes.Height;
        EnsureDisplayTexture(planes.Width, planes.Height);
        EnsureYCbCrTextures(planes.Width, planes.Height);

        UploadR8Plane(_yTex!,  planes.Y,  planes.YStride, planes.Width,  planes.Height);
        UploadR8Plane(_cbTex!, planes.Cb, planes.CStride, planes.CStride, planes.CHeight);
        UploadR8Plane(_crTex!, planes.Cr, planes.CStride, planes.CStride, planes.CHeight);

        // GPU render: YCbCr→BGRA into displayTexture
        _ctx.OMSetRenderTargets(_displayRTV!);
        _ctx.RSSetState(_noCullRS!);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_fullscreenVS!);
        _ctx.PSSetShader(_ycbcrPS!);
        _ctx.PSSetShaderResources(0, [_ySRV!, _cbSRV!, _crSRV!]);
        _ctx.PSSetSamplers(0, [_linearSampler!]);
        var vp = new Viewport(0, 0, _displayW, _displayH);
        _ctx.RSSetViewport(vp);
        _ctx.Draw(3, 0);

        Render();
    }

    private unsafe void UploadR8Plane(ID3D11Texture2D tex, byte* data, int stride, int w, int h)
    {
        var mapped = _ctx!.Map(tex, 0, MapMode.WriteDiscard);
        for (int row = 0; row < h; row++)
            Buffer.MemoryCopy(data + row * stride, (byte*)mapped.DataPointer + row * mapped.RowPitch, w, w);
        _ctx.Unmap(tex, 0);
    }

    /// Execute GPU-direct fill commands (Hextile solid fills) via compute shader.
    public void ExecuteFills(FillCommand[] fills, int fbW, int fbH)
    {
        if (_device is null || _ctx is null || fills.Length == 0) return;
        FbWidth = fbW; FbHeight = fbH;
        EnsureDisplayTexture(fbW, fbH);

        int stride = Marshal.SizeOf<FillCommand>();
        uint byteWidth = (uint)(fills.Length * stride);

        if (_fillCmdBuf is null || _fillCmdBuf.Description.ByteWidth < byteWidth)
        {
            _fillCmdBuf?.Dispose(); _fillCmdSRV?.Dispose();
            var bufDesc = new BufferDescription
            {
                ByteWidth           = byteWidth,
                Usage               = ResourceUsage.Dynamic,
                BindFlags           = BindFlags.ShaderResource,
                CPUAccessFlags      = CpuAccessFlags.Write,
                MiscFlags           = ResourceOptionFlags.BufferStructured,
                StructureByteStride = (uint)stride,
            };
            _fillCmdBuf = _device.CreateBuffer(bufDesc);
            _fillCmdSRV = _device.CreateShaderResourceView(_fillCmdBuf);
        }

        var mapped = _ctx.Map(_fillCmdBuf!, 0, MapMode.WriteDiscard);
        unsafe { Marshal.Copy(MemoryMarshal.AsBytes(fills.AsSpan()).ToArray(), 0, mapped.DataPointer, fills.Length * stride); }
        _ctx.Unmap(_fillCmdBuf!, 0);

        uint count = (uint)fills.Length;
        _ctx.CSSetShader(_fillCS!);
        _ctx.CSSetShaderResources(0, [_fillCmdSRV!]);
        _ctx.CSSetUnorderedAccessViews(0, [_displayUAV!]);
        _ctx.CSSetConstantBuffers(0, [MakeConstantBuffer(count)]);
        _ctx.Dispatch((uint)((fills.Length + 63) / 64), 1, 1);
        _ctx.CSSetShader(null!);

        Render();
    }

    private ID3D11Buffer MakeConstantBuffer(uint value)
    {
        var data = new uint[] { value, 0, 0, 0 };
        var desc = new BufferDescription
        {
            ByteWidth = 16u,
            Usage     = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
        };
        return _device!.CreateBuffer(data.AsSpan(), desc);
    }

    // ── Render (display texture → swap chain) ────────────────────────────────

    private void Render()
    {
        if (_ctx is null || _rtv is null || _displaySRV is null) return;

        // Unbind display texture from any lingering RTV before using it as SRV
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null);

        // ── CAS sharpening pass (Adreno GPU) ─────────────────────────────────
        // Reads _displayTex (YCbCr-converted frame) → writes _casOutTex.
        // Skipped when sharpness is zero or resources unavailable.
        var presentSRV = _displaySRV; // default: present unsharpened
        if (Sharpness > 0f && _casCS is not null && _casOutUAV is not null && _casCB is not null)
        {
            // Update sharpness constant via Map/WriteDiscard (Dynamic CB)
            unsafe
            {
                var mapped = _ctx.Map(_casCB!, 0, MapMode.WriteDiscard);
                var ptr = (float*)mapped.DataPointer;
                ptr[0] = _displayW; ptr[1] = _displayH; ptr[2] = Sharpness; ptr[3] = 0f;
                _ctx.Unmap(_casCB!, 0);
            }

            _ctx.CSSetShader(_casCS);
            _ctx.CSSetShaderResources(0, [_displaySRV]);
            _ctx.CSSetUnorderedAccessViews(0, [_casOutUAV]);
            _ctx.CSSetConstantBuffers(0, [_casCB]);
            _ctx.Dispatch(
                (uint)((_displayW  + 7) / 8),
                (uint)((_displayH + 7) / 8),
                1);
            _ctx.CSSetShader(null!);
            _ctx.CSSetShaderResources(0, new ID3D11ShaderResourceView?[] { null });
            _ctx.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView?[] { null });

            presentSRV = _casOutSRV; // present the sharpened output
        }

        // ── Display pass (sharpened/unsharpened → swap chain) ─────────────────
        var sc = _swapChain!.Description1;
        var vp = new Viewport(0, 0, (float)sc.Width, (float)sc.Height);
        _ctx.OMSetRenderTargets(_rtv);
        _ctx.RSSetViewport(vp);
        _ctx.RSSetState(_noCullRS!);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_fullscreenVS!);
        _ctx.PSSetShader(_displayPS!);
        _ctx.PSSetShaderResources(0, [presentSRV]);
        _ctx.PSSetSamplers(0, [_linearSampler!]);
        _ctx.Draw(3, 0);

        _swapChain.Present(0, PresentFlags.None);
    }

    /// Capture the current display texture as a PNG byte array.
    public byte[]? CaptureScreenshot()
    {
        if (_device is null || _ctx is null || _displayTex is null) return null;

        var desc = _displayTex.Description with
        {
            Usage          = ResourceUsage.Staging,
            BindFlags      = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags      = ResourceOptionFlags.None,
        };
        using var staging = _device.CreateTexture2D(desc);
        _ctx.CopyResource(staging, _displayTex);

        var mapped = _ctx.Map(staging, 0, MapMode.Read);
        int w = _displayW, h = _displayH;
        var pixels = new byte[w * h * 4];
        unsafe
        {
            byte* src = (byte*)mapped.DataPointer;
            for (int row = 0; row < h; row++)
                Marshal.Copy((nint)(src + row * mapped.RowPitch), pixels, row * w * 4, w * 4);
        }
        _ctx.Unmap(staging, 0);

        using var ms = new MemoryStream();
        using var bmp = new System.Drawing.Bitmap(w, h, w * 4,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb,
            System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0));
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    // ── ISwapChainPanelNative COM interop ────────────────────────────────────

    // WinUI 3 (Windows App SDK) ISwapChainPanelNative GUID — different from UWP's F92F19D2.
    // Defined in microsoft.ui.xaml.media.dxinterop.h.
    [ComImport, Guid("63aad0b8-7c24-40ff-85a8-640d944cc325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        [PreserveSig] int SetSwapChain(IntPtr swapChain);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        DisposeYCbCrTextures();
        _fillCmdSRV?.Dispose(); _fillCmdBuf?.Dispose();
        _displayUAV?.Dispose(); _displayRTV?.Dispose();
        _displaySRV?.Dispose(); _displayTex?.Dispose();
        _casCB?.Dispose(); _casOutUAV?.Dispose(); _casOutSRV?.Dispose(); _casOutTex?.Dispose();
        _noCullRS?.Dispose(); _linearSampler?.Dispose();
        _ictCS?.Dispose(); _fillCS?.Dispose(); _casCS?.Dispose();
        _ycbcrPS?.Dispose(); _displayPS?.Dispose(); _fullscreenVS?.Dispose();
        _rtv?.Dispose(); _swapChain?.Dispose();
        _ctx?.Dispose(); _device?.Dispose();
    }
}

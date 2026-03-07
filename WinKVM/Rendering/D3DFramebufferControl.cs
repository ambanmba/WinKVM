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
public sealed class D3DFramebufferControl : SwapChainPanel, IDisposable
{
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
    private ID3D11SamplerState?       _linearSampler;

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
        Loaded   += (_, _) => Initialize();
        Unloaded += (_, _) => Dispose();
    }

    private void Initialize()
    {
        var featureLevels = new[] { FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device,
            out _ctx);

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

        // Connect swap chain to this SwapChainPanel via ISwapChainPanelNative COM interop
        var nativePanel = (ISwapChainPanelNative)(object)this;
        nativePanel.SetSwapChain(_swapChain);

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
        var ictCSBytes     = CompileHlsl("ICTDequant.hlsl", "cs_5_0", "CS");

        _fullscreenVS = _device.CreateVertexShader(vsBytes);
        _ycbcrPS      = _device.CreatePixelShader(ycbcrPSBytes);
        _displayPS    = _device.CreatePixelShader(displayPSBytes);
        _fillCS       = _device.CreateComputeShader(fillCSBytes);
        _ictCS        = _device.CreateComputeShader(ictCSBytes);
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
        _displayW   = w; _displayH = h;
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
        if (_device is null || _ctx is null) return;
        FbWidth  = planes.Width;
        FbHeight = planes.Height;
        EnsureDisplayTexture(planes.Width, planes.Height);
        EnsureYCbCrTextures(planes.Width, planes.Height);

        UploadR8Plane(_yTex!,  planes.Y,  planes.YStride, planes.Width,  planes.Height);
        UploadR8Plane(_cbTex!, planes.Cb, planes.CStride, planes.CStride, planes.CHeight);
        UploadR8Plane(_crTex!, planes.Cr, planes.CStride, planes.CStride, planes.CHeight);

        // GPU render: YCbCr→BGRA into displayTexture
        _ctx.OMSetRenderTargets(_displayRTV!);
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

        var sc = _swapChain!.Description1;
        var vp = new Viewport(0, 0, (float)sc.Width, (float)sc.Height);

        _ctx.OMSetRenderTargets(_rtv);
        _ctx.RSSetViewport(vp);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_fullscreenVS!);
        _ctx.PSSetShader(_displayPS!);
        _ctx.PSSetShaderResources(0, [_displaySRV]);
        _ctx.PSSetSamplers(0, [_linearSampler!]);
        _ctx.Draw(3, 0);

        _swapChain.Present(1, PresentFlags.None); // VSync
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

    [ComImport, Guid("F92F19D2-3ADE-45A6-A20C-F6F1EA90554B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative
    {
        void SetSwapChain([MarshalAs(UnmanagedType.Interface)] IDXGISwapChain swapChain);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        DisposeYCbCrTextures();
        _fillCmdSRV?.Dispose(); _fillCmdBuf?.Dispose();
        _displayUAV?.Dispose(); _displayRTV?.Dispose();
        _displaySRV?.Dispose(); _displayTex?.Dispose();
        _linearSampler?.Dispose();
        _ictCS?.Dispose(); _fillCS?.Dispose();
        _ycbcrPS?.Dispose(); _displayPS?.Dispose(); _fullscreenVS?.Dispose();
        _rtv?.Dispose(); _swapChain?.Dispose();
        _ctx?.Dispose(); _device?.Dispose();
    }
}

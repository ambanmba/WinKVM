# WinKVM

Native Windows KVM-over-IP client for Raritan KX III/IV Dominion devices.
Port of [SwiftKVM](https://github.com/ambanmba/SwiftKVM) — same protocol, rewritten in C# with WinUI 3 and maximum GPU/NPU acceleration.

## Features

- **e-RIC protocol** — Full implementation of Raritan's proprietary e-RIC/RFB-over-TLS protocol
- **ICT codec** — Hardware-like decode of Raritan's custom JPEG variant (YCbCr 4:2:0 tiles)
- **GPU rendering** — Direct3D 11 pipeline; zero-copy on unified-memory adapters
- **YCbCr → RGB on GPU** — HLSL pixel shader (replaces Metal shader from SwiftKVM)
- **Hextile fills on GPU** — HLSL compute shader for batch solid-colour fills
- **IDCT on GPU** — `ICTDequant.hlsl` compute shader runs parallel IDCT across all tiles
- **NPU-accelerated OCR** — `Windows.Media.Ocr` uses the NPU on Copilot+ PCs (Snapdragon X, AMD Ryzen AI, Intel Core Ultra); falls back to CPU on other hardware
- **AI Agent** — Claude, OpenAI, Grok, Ollama providers; same agent loop as SwiftKVM
- **TOFU certificate trust** — SHA-256 fingerprint stored in Windows ApplicationData
- **Keyboard/mouse forwarding** — Raritan AT-scan-code key mapping (same table as SwiftKVM)
- **Text injection** — Paste or type text to the remote host
- **Port switching** — Multi-port KVM support
- **Virtual media** — VmDriveCount reported; full MSP implementation planned

## GPU / NPU Architecture

```
                  Raritan KX Device (port 443/TLS)
                           │
                    ERICSession.cs
                           │
          ┌────────────────┼────────────────┐
          │                │                │
    ICTDecoder          HextileDecoder    RawDecoder
    (CPU: Huffman       (CPU: parse,      (CPU: memcpy)
     + dequant)          GPU: fills via
          │               HextileFill.hlsl)
    YCbCrPlanes                │
    (CPU alloc)         FillCommand[]
          │                    │
          └────────┬───────────┘
                   │
         D3DFramebufferControl.cs
              (Direct3D 11)
                   │
      ┌────────────┼───────────────┐
      │            │               │
  YCbCr.hlsl  HextileFill.hlsl  ICTDequant.hlsl
  (GPU: PS)   (GPU: CS)         (GPU: CS, IDCT)
      │
  Display.hlsl
  (GPU: passthrough → SwapChain)
                   │
         Windows.Media.Ocr
         (NPU on Copilot+ PCs)
```

## Building

Requirements:
- Visual Studio 2022 17.x with **Windows App SDK** workload
- Windows 10 SDK 22621 or later
- .NET 9

```
git clone https://github.com/ambanmba/WinKVM
cd WinKVM
dotnet build WinKVM.sln -c Release
```

## Platform notes

| Hardware | GPU rendering | NPU OCR |
|---|---|---|
| Snapdragon X Elite/Plus | ✅ Adreno GPU | ✅ Hexagon NPU |
| AMD Ryzen AI | ✅ RDNA GPU | ✅ AMD XDNA NPU |
| Intel Core Ultra | ✅ Intel Arc GPU | ✅ Intel NPU |
| Discrete NVIDIA/AMD | ✅ | ❌ (CPU OCR) |

## Compared to SwiftKVM

| Component | SwiftKVM (macOS) | WinKVM (Windows) |
|---|---|---|
| UI framework | SwiftUI | WinUI 3 |
| GPU rendering | Metal | Direct3D 11 |
| YCbCr shader | `.metal` | HLSL |
| TLS | Network.framework | SslStream |
| OCR | Vision.framework | Windows.Media.Ocr |
| Key storage | Keychain | ApplicationData |
| Parallelism | ConcurrentPerform | Parallel.For / HLSL CS |

## Planned

- RAP (remote audio) via NAudio/WASAPI
- MSP (virtual media ISO mount)
- Settings page
- Diagnostics view
- Screen recording via Windows.Graphics.Capture

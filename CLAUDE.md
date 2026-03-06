# WinKVM Development Notes

## Project Overview
WinKVM is a native Windows KVM-over-IP client for Raritan KX III/IV Dominion devices.
Port of SwiftKVM (Swift/Metal/macOS) to C#/WinUI 3/Direct3D 11/Windows.

## Architecture
- **Protocol**: e-RIC over TLS (port 443) — identical to SwiftKVM
- **Video**: ICT codec (Raritan JPEG-like) → YCbCr planes → GPU HLSL shaders → Direct3D 11
- **Input**: HID keyboard/mouse over e-RIC (same Raritan keycode table as SwiftKVM)

## GPU / NPU acceleration layers
1. **Display rendering** — Direct3D 11 SwapChainPanel (always GPU)
2. **YCbCr → RGB** — `YCbCr.hlsl` pixel shader (GPU)
3. **Hextile fills** — `HextileFill.hlsl` compute shader (GPU)
4. **IDCT** — `ICTDequant.hlsl` compute shader (GPU/NPU via DirectML in future)
5. **OCR** — `Windows.Media.Ocr` (NPU on Copilot+ PCs, CPU otherwise)

## Key files
- `Protocol/ERICSession.cs` — Main session controller (message loop, auth, decoders)
- `Protocol/ERICConnection.cs` — TLS connection via SslStream
- `Protocol/ERICMessages.cs` — Message types, BinaryReader/Writer, ChunkReader
- `Framebuffer/ICTDecoder.cs` — ICT Huffman + IDCT decoder (CPU path)
- `Framebuffer/HextileDecoder.cs` — Hextile → GPU fill/raw commands
- `Rendering/D3DFramebufferControl.cs` — Direct3D 11 renderer (SwapChainPanel)
- `Rendering/Shaders/*.hlsl` — HLSL shaders (YCbCr, Display, HextileFill, ICTDequant)
- `Agent/AgentLoop.cs` — AI agent loop (port of AgentLoop.swift)
- `Agent/WindowsOcr.cs` — Windows.Media.Ocr (NPU)

## Protocol notes (from SwiftKVM CLAUDE.md)
- e-RIC = extended RFB over TLS, Raritan proprietary
- ICT codec = custom JPEG-like, 16×16 tiles, YCbCr 4:2:0
- Keycodes = AT scan codes − 1 (NOT USB HID, NOT X11 keysyms)
- Multiple TCP connections: e-RIC (main), RAP (audio), MSP (virtual media), CRP (smart card)
- Only e-RIC fully implemented; RAP/MSP/CRP are planned

## Build requirements
- Visual Studio 2022 + Windows App SDK workload
- .NET 9 + Windows 10 SDK 22621+
- NuGet: Microsoft.WindowsAppSDK, Vortice.Direct3D11, Vortice.DXGI, Vortice.D3DCompiler, Microsoft.AI.DirectML, Tesseract

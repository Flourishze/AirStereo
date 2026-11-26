# Building AirStereo2

## Prerequisites

- Windows 10 22H2 (19045) / Windows 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`net8.0-windows10.0.19041.0`)
- [Rust toolchain](https://rustup.rs/) (`stable-x86_64-pc-windows-msvc`) + Windows 10/11 SDK (`rc.exe` for embedding application icon and version resources)
- PowerShell 7 (`pwsh`)
- Optional (for MSI packaging): [WiX Toolset v4](https://wixtoolset.org/) with `WixToolset.UI.wixext` and `WixToolset.Util.wixext`

## Build Portable Release

```powershell
pwsh -NoProfile -File .\build.ps1
```

This builds:
1. `source/Backend` → `AirStereo2.Engine.exe` (Rust audio capture, ALAC/PCM encoding, PTP/NTP timing, AirPlay 1/2 RTSP/RTP streaming)
2. `source/Core` → `AirStereo2.Core.dll` (device discovery, session orchestration, equalizer/diagnostics/firewall management)
3. `source/Shell` → `AirStereo2.exe` (WinUI 3 Fluent tray popup & Settings window)

Output is staged to `dist/release/`.

## Build MSI Installer

```powershell
pwsh -NoProfile -File .\tools\Build-Msi.ps1 -PayloadDirectory 'dist\release' -OutputDirectory 'dist' -Version '1.1.1' -SkipShellRepublish
```

## Run Unit Tests

```powershell
cd source\Backend
cargo test --workspace
```

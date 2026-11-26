# Build AirStereo2 (WinUI 3 shell + Core + Rust AirStereo2.Engine)
#Requires -Version 7.0
param(
    [string]$OutputDir = 'dist\release'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = [IO.Path]::GetFullPath((Join-Path $root $OutputDir))

Write-Host '==> Building Rust audio engine (AirStereo2.Engine.exe)...'
Push-Location (Join-Path $root 'source\Backend')
try {
    cargo build --release --bin airstereo2-engine
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed: $LASTEXITCODE" }
} finally { Pop-Location }

Write-Host '==> Publishing WinUI 3 shell & core (AirStereo2.exe)...'
dotnet publish (Join-Path $root 'source\Shell\AirStereo2.csproj') -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

Copy-Item -LiteralPath (Join-Path $root 'source\Backend\target\release\airstereo2-engine.exe') -Destination (Join-Path $out 'AirStereo2.Engine.exe') -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $out 'LICENSE') -Force
Write-Host "Build completed: $out"

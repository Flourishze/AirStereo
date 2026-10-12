#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDir = '..\dist',
    [string]$DotNet = 'dotnet',
    [string]$NuGetSource,
    [string]$NuGetPackageRoot,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'AirStereo.WinUI.csproj'
$output = if ([IO.Path]::IsPathRooted($OutputDir)) { [IO.Path]::GetFullPath($OutputDir) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDir)) }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$properties = @('-p:NuGetAudit=false')
if ($NuGetPackageRoot) { $properties += "-p:RestorePackagesPath=$NuGetPackageRoot" }
if (-not $NoRestore) {
    $restore = @('restore',$project) + $properties
    if ($NuGetSource) { $restore += @('--source',$NuGetSource) }
    & $DotNet @restore
    if ($LASTEXITCODE -ne 0) { throw 'WinUI restore failed.' }
}
& $DotNet publish $project --no-restore -c Release -o $output -p:PublishReadyToRun=false @properties
if ($LASTEXITCODE -ne 0) { throw 'WinUI publish failed.' }
$pri = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.19041.0\win-x64\AirStereo.pri'
if (-not (Test-Path -LiteralPath $pri)) { throw 'Compiled WinUI resources are missing.' }
Copy-Item -LiteralPath $pri -Destination (Join-Path $output 'AirStereo.pri') -Force
Write-Output (Join-Path $output 'AirStereo.exe')

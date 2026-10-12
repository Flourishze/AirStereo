#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceDirectory = 'dist',
    [string]$PayloadDirectory = 'packages\release-1.0.7\payload',
    [string]$DotNetRoot,
    [string]$WindowsAppSDKDirectory,
    [string]$Version = '1.0.7'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function WorkspacePath([string]$path) {
    $full = [IO.Path]::GetFullPath((Join-Path $root $path))
    if (-not $full.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Paths must remain inside the AirStereo workspace.'
    }
    return $full
}
$source = WorkspacePath $SourceDirectory
$payload = WorkspacePath $PayloadDirectory
if (Test-Path -LiteralPath $payload) { throw 'Use a new payload directory to preserve previous builds.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'AirStereo.dll')).ProductVersion -ne $Version) {
    throw 'Application version mismatch.'
}
New-Item -ItemType Directory -Path $payload | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    if ($file.Extension -in @('.pdb', '.log', '.pfx', '.pem', '.key') -or
        $file.Name -match '^(?:winui-)?startup-(?:error|trace)\.' -or
        $file.Name -match '^(memory-test-trace|BuildInfo|settings)\.' -or
        $file.Name -match '^AirStereo\.WinUI\.') { continue }
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    if ($relative -match '(^|\\)(Diagnostics|bin|obj)(\\|$)') { continue }
    $destination = Join-Path $payload $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $payload 'LICENSE.txt')
$license = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw
$rtfText = $license.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}').Replace("`r", '').Replace("`n", '\par ')
('{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs20 ' + $rtfText + '}') |
    Set-Content -LiteralPath (Join-Path $payload 'AirStereo-License.rtf') -Encoding ASCII
$notices = Join-Path $payload 'ThirdPartyNotices'
New-Item -ItemType Directory -Path $notices | Out-Null
if (-not $DotNetRoot) {
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $DotNetRoot = Split-Path -Parent $dotnetCommand.Source
}
$dotnetRoot = [IO.Path]::GetFullPath($DotNetRoot)
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $notices 'DotNet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $notices 'DotNet-ThirdPartyNotices.txt')
if (-not $WindowsAppSDKDirectory) {
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $WindowsAppSDKDirectory = Join-Path $nugetRoot 'microsoft.windowsappsdk\1.8.260921001'
}
$sdkRoot = [IO.Path]::GetFullPath($WindowsAppSDKDirectory)
Copy-Item -LiteralPath (Join-Path $sdkRoot 'license.txt') -Destination (Join-Path $notices 'WindowsAppSDK-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $sdkRoot 'NOTICE.txt') -Destination (Join-Path $notices 'WindowsAppSDK-NOTICE.txt')
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    @{ Name = [IO.Path]::GetRelativePath($payload, $_.FullName); SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{ BuildId = $Version; Architecture = 'x64'; Version = $Version; Files = $files } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'BuildInfo.json') -Encoding utf8
Write-Output "payload=$payload files=$($files.Count)"

#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$IdentityFile,
    [string]$PayloadDirectory = 'packages\release-1.0.7\payload',
    [string]$OutputDirectory = 'packages\release-1.0.7\msix',
    [string]$CompiledResourceIndex,
    [string]$LauncherDirectory = 'src\launcher',
    [string]$IconFile = 'assets\icons\AirStereo.png',
    [string]$Version = '1.0.7'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function WorkspacePath([string]$path) {
    $full = [IO.Path]::GetFullPath((Join-Path $root $path))
    if (-not $full.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Paths must remain inside the workspace.'
    }
    return $full
}
if ($Version -notmatch '^[1-9]\d*\.\d+\.\d+$' -or @($Version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count) { throw 'Invalid Store version.' }
$output = WorkspacePath $OutputDirectory
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory; do not overwrite existing packages.' }
$identity = Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
foreach ($name in @('Name','Publisher','PublisherDisplayName','DisplayName')) {
    if ([string]::IsNullOrWhiteSpace($identity.$name) -or $identity.$name -match 'REPLACE|YOUR_|LocalValidation') { throw "Invalid identity field: $name" }
}
$source = WorkspacePath $PayloadDirectory
$launchers = WorkspacePath $LauncherDirectory
$info = Get-Content -LiteralPath (Join-Path $source 'BuildInfo.json') -Raw | ConvertFrom-Json
if ($info.BuildId -ne $Version -or $info.Architecture -ne 'x64') { throw 'Payload identity mismatch.' }
foreach ($entry in $info.Files) {
    if ((Get-FileHash -LiteralPath (Join-Path $source $entry.Name)).Hash -ne $entry.SHA256) { throw "Payload checksum mismatch: $($entry.Name)" }
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'AirStereo.dll')).ProductVersion -ne $Version) { throw 'App version mismatch.' }
$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' | Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
$cl = Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe' | Sort-Object { [version]$_.Directory.Parent.Parent.Parent.Name } -Descending | Select-Object -First 1
if (-not $sdk -or -not $cl) { throw 'Windows SDK and Visual C++ Build Tools required.' }
$sdkVersion = $sdk.Directory.Parent.Name
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$include = Join-Path $sdkRoot "Include\$sdkVersion"
$lib = Join-Path $sdkRoot "Lib\$sdkVersion"
$vc = $cl.Directory.Parent.Parent.Parent.FullName
$build = Join-Path $output 'build'
$payload = Join-Path $output 'payload'
$app = $payload
New-Item -ItemType Directory -Force -Path $build,$app | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    if ($file.Name -in @('BuildInfo.json','AirStereo-License.rtf','Uninstall.exe')) { continue }
    if ($file.Extension -in @('.pfx','.pem','.key','.cer','.pdb','.log') -or
        $relative -match '(^|\\)(Diagnostics|obj|bin)(\\|$)' -or
        $file.Name -match '^(settings|startup-error|startup-trace|winui-startup|memory-test-trace)') { throw "Unexpected private/build file: $relative" }
    $dest = Join-Path $app $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $dest
}
$config = Get-Content -LiteralPath (Join-Path $app 'AirStereo.runtimeconfig.json') -Raw | ConvertFrom-Json
$core = $config.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App'
if (-not $core) { throw 'A self-contained runtime is required.' }
$oldInclude = $env:INCLUDE
$oldLib = $env:LIB
try {
    $env:INCLUDE = @("$vc\include","$include\ucrt","$include\shared","$include\um","$include\winrt","$include\cppwinrt") -join ';'
    $env:LIB = @("$vc\lib\x64","$lib\um\x64","$lib\ucrt\x64") -join ';'
    & $cl.FullName /nologo /c /O2 /MT /EHsc /std:c++20 /guard:cf /W4 /utf-8 /DWINRT_NO_SOURCE_LOCATION "/Fo$build\AirStereoStore.obj" (Join-Path $launchers 'AirStereoStore.cpp')
    if ($LASTEXITCODE -ne 0) { throw 'Store bridge compilation failed.' }
    & "$($cl.Directory.FullName)\link.exe" /nologo /DLL /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /guard:cf "/OUT:$app\AirStereo.Store.dll" "/IMPLIB:$build\AirStereo.Store.lib" "$build\AirStereoStore.obj" windowsapp.lib runtimeobject.lib ole32.lib kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Store bridge linking failed.' }
    & $cl.FullName /nologo /c /O2 /MT /guard:cf /W3 /utf-8 "/Fo$build\AirStereoStartup.obj" (Join-Path $launchers 'AirStereoWinUiStartup.c')
    if ($LASTEXITCODE -ne 0) { throw 'Startup compilation failed.' }
    & "$($cl.Directory.FullName)\link.exe" /nologo /SUBSYSTEM:WINDOWS /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /guard:cf "/OUT:$app\AirStereoStartup.exe" "$build\AirStereoStartup.obj" kernel32.lib user32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Startup linking failed.' }
} finally { $env:INCLUDE=$oldInclude; $env:LIB=$oldLib }
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Path $assets | Out-Null
function SaveLogo([int]$size,[string]$name) {
    $image = [Drawing.Image]::FromFile((WorkspacePath $IconFile))
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($image,0,0,$size,$size)
        $bitmap.Save((Join-Path $assets $name),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose();$bitmap.Dispose();$image.Dispose() }
}
SaveLogo 50 'StoreLogo.png'
foreach ($scale in @(100,125,150,200,400)) {
    SaveLogo ([int](44*$scale/100)) "Square44x44Logo.scale-$scale.png"
    SaveLogo ([int](150*$scale/100)) "Square150x150Logo.scale-$scale.png"
}
function XmlEscape([string]$value) { [Security.SecurityElement]::Escape($value) }
@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap desktop rescap">
 <Identity Name="$(XmlEscape $identity.Name)" Publisher="$(XmlEscape $identity.Publisher)" Version="$Version.0" ProcessorArchitecture="x64"/>
 <Properties><DisplayName>$(XmlEscape $identity.DisplayName)</DisplayName><PublisherDisplayName>$(XmlEscape $identity.PublisherDisplayName)</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19045.0" MaxVersionTested="$sdkVersion"/></Dependencies>
 <Resources><Resource Language="zh-CN"/><Resource Language="en-US"/></Resources>
 <Applications><Application Id="AirStereo" Executable="AirStereo.exe" EntryPoint="Windows.FullTrustApplication">
  <uap:VisualElements DisplayName="$(XmlEscape $identity.DisplayName)" Description="AirStereo AirPlay audio sender" BackgroundColor="transparent" Square44x44Logo="Assets\Square44x44Logo.png" Square150x150Logo="Assets\Square150x150Logo.png"/>
  <Extensions><desktop:Extension Category="windows.startupTask" Executable="AirStereoStartup.exe" EntryPoint="Windows.FullTrustApplication"><desktop:StartupTask TaskId="AirStereoStartup" Enabled="false" DisplayName="AirStereo"/></desktop:Extension></Extensions>
 </Application></Applications>
 <Capabilities><Capability Name="internetClientServer"/><Capability Name="privateNetworkClientServer"/><rescap:Capability Name="runFullTrust"/></Capabilities>
</Package>
"@ | Set-Content -LiteralPath (Join-Path $payload 'AppxManifest.xml') -Encoding utf8
$makePri = Join-Path $sdk.Directory.FullName 'makepri.exe'
# A clean source build may supply an equivalent PRI without historical output
# entries. Require all embedded XAML bytes to match before replacing it.
if ($CompiledResourceIndex) {
    $replacement = WorkspacePath $CompiledResourceIndex
    $originalDump = Join-Path $build 'original-pri.xml'
    $replacementDump = Join-Path $build 'replacement-pri.xml'
    & $makePri dump /if (Join-Path $app 'AirStereo.pri') /of $originalDump /dt detailed *> (Join-Path $build 'original-pri-dump.log')
    if ($LASTEXITCODE) { throw 'Original application PRI dump failed.' }
    & $makePri dump /if $replacement /of $replacementDump /dt detailed *> (Join-Path $build 'replacement-pri-dump.log')
    if ($LASTEXITCODE) { throw 'Replacement application PRI dump failed.' }
    [xml]$originalXml = Get-Content -LiteralPath $originalDump -Raw
    [xml]$replacementXml = Get-Content -LiteralPath $replacementDump -Raw
    $originalEntries = @($originalXml.SelectNodes('//Candidate[@type="EmbeddedData"]'))
    $replacementEntries = @($replacementXml.SelectNodes('//Candidate[@type="EmbeddedData"]'))
    if ($originalEntries.Count -ne $replacementEntries.Count) { throw 'Compiled XAML resource count mismatch.' }
    foreach ($entry in $originalEntries) {
        if (-not @($replacementEntries | Where-Object { $_.OuterXml -eq $entry.OuterXml }).Count) { throw 'Compiled XAML resource bytes differ.' }
    }
    Copy-Item -LiteralPath $replacement -Destination (Join-Path $app 'AirStereo.pri') -Force
}
# Every path in each shipped resource index must resolve inside this package.
foreach ($index in Get-ChildItem -LiteralPath $app -Filter '*.pri' -File) {
    $dump = Join-Path $build ($index.BaseName + '-validated.xml')
    & $makePri dump /if $index.FullName /of $dump /dt detailed *> (Join-Path $build ($index.BaseName + '-dump.log'))
    if ($LASTEXITCODE) { throw "Resource index dump failed: $($index.Name)" }
    [xml]$indexXml = Get-Content -LiteralPath $dump -Raw
    foreach ($entry in $indexXml.SelectNodes('//Candidate[@type="Path"]/Value')) {
        $relative = $entry.InnerText
        $target = [IO.Path]::GetFullPath((Join-Path $app $relative))
        if (-not $target.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "Resource path outside or missing from package: $relative"
        }
    }
}
$priConfig = Join-Path $build 'priconfig.xml'
& $makePri createconfig /cf $priConfig /dq zh-CN /pv 10.0.0 *> (Join-Path $output 'makepri-config.log')
if ($LASTEXITCODE -ne 0) { throw 'MakePri config failed.' }
[xml]$priXml = Get-Content -LiteralPath $priConfig -Raw
foreach ($node in @($priXml.SelectNodes('//packaging'))) { [void]$node.ParentNode.RemoveChild($node) }
$priXml.Save($priConfig)
# Index application/SDK compiled resources and Store artwork only. Indexing all
# runtime files again would duplicate embedded SDK assets and misread DLL names
# as resource qualifiers. Resource paths remain relative to the package root.
$resourceInput = Join-Path $build 'resource-input'
New-Item -ItemType Directory -Path $resourceInput | Out-Null
Copy-Item -LiteralPath $assets -Destination (Join-Path $resourceInput 'Assets') -Recurse
# Merge the path-validated app index so package identity resolves compiled
# XAML as well as Store artwork. Do not index runtime folders a second time.
Copy-Item -LiteralPath (Join-Path $app 'AirStereo.pri') -Destination $resourceInput
& $makePri new /pr $resourceInput /cf $priConfig /in $identity.Name /of (Join-Path $payload 'resources.pri') *> (Join-Path $output 'makepri-new.log')
if ($LASTEXITCODE -ne 0) { throw 'MakePri indexing failed; see makepri-new.log.' }
$combinedDump = Join-Path $build 'combined-resources.xml'
& $makePri dump /if (Join-Path $payload 'resources.pri') /of $combinedDump /dt detailed *> (Join-Path $build 'combined-resources-dump.log')
if ($LASTEXITCODE) { throw 'Combined resource index dump failed.' }
[xml]$combinedXml = Get-Content -LiteralPath $combinedDump -Raw
foreach ($entry in $combinedXml.SelectNodes('//Candidate[@type="Path"]/Value')) {
    $target = [IO.Path]::GetFullPath((Join-Path $app $entry.InnerText))
    if (-not $target.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Combined resource path outside or missing: $($entry.InnerText)"
    }
}
foreach ($name in @('App.xbf','WinUiMainWindow.xbf','WinUiSettingsWindow.xbf')) {
    if (-not $combinedXml.SelectSingleNode("//NamedResource[@name='$name']/Candidate[@type='EmbeddedData']")) { throw "Missing compiled package XAML: $name" }
}
$package = Join-Path $output "AirStereo-$Version-x64.msix"
& $sdk.FullName pack /d $payload /p $package /h SHA256 *> (Join-Path $output 'makeappx-pack.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $output 'makeappx-pack.log') -Tail 20; throw 'MakeAppx full validation failed.' }
$unpack = Join-Path $output 'verified-unpack'
& $sdk.FullName unpack /p $package /d $unpack *> (Join-Path $output 'makeappx-unpack.log')
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx unpack failed.' }
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($payload,$file.FullName)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $unpack $relative)).Hash) { throw "Roundtrip checksum mismatch: $relative" }
}
$hash = (Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($package))" | Set-Content (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
[ordered]@{Version="$Version.0";Identity=$identity;Architecture='x64';Signed=$false;MinimumWindowsVersion='10.0.19045.0';WindowsSDK=$sdkVersion;CoreRuntime=$core.version;WindowsAppSDK='1.8.260921001';FilesVerified=$files.Count;SHA256=$hash;Bytes=(Get-Item $package).Length;ResourceIndexGenerated=$true;DeployedTestPassed=$false;WackPassed=$false;AudioHardwareTestPassed=$false} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'PackageInfo.json') -Encoding utf8
Write-Output "msix=$package filesVerified=$($files.Count) sha256=$hash"

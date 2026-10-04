[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Store')][string]$IdentityFile,
    [Parameter(Mandatory, ParameterSetName = 'Local')][switch]$LocalValidation,
    [string]$OutputDirectory = 'packages\msix-1.0.4',
    [string]$Version = '1.0.4',
    [string]$MinimumWindowsVersion = '10.0.19045.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function WorkspacePath([string]$path) {
    $full = [IO.Path]::GetFullPath((Join-Path $root $path))
    if (-not $full.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Output paths must remain inside the AirStereo workspace.'
    }
    return $full
}
if ($Version -notmatch '^[1-9]\d*\.\d+\.\d+$' -or
    @($Version.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count) {
    throw 'Store version must be major.minor.patch, each <= 65535, major > 0. Revision is always 0.'
}
if ([version]$MinimumWindowsVersion -lt [version]'10.0.19045.0') {
    throw 'This bundled-runtime package targets Windows 10 22H2 or newer.'
}
$output = WorkspacePath $OutputDirectory
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; never overwrite an existing package.' }
if ($LocalValidation) {
    # Deliberately NOT a production identity. Cannot be uploaded as the user's Store app.
    $identity = [pscustomobject]@{
        Name = 'AirStereo.LocalValidation'; Publisher = 'CN=AirStereo Local Validation'
        PublisherDisplayName = 'AirStereo Local Validation'; DisplayName = 'AirStereo (Local Validation)'
    }
} else {
    $identity = Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
    foreach ($name in @('Name', 'Publisher', 'PublisherDisplayName', 'DisplayName')) {
        if ([string]::IsNullOrWhiteSpace($identity.$name)) { throw "Identity field missing: $name" }
    }
    if ($identity.Name -match 'LocalValidation|REPLACE|YOUR_' -or $identity.Publisher -match 'REPLACE|YOUR_') {
        throw 'Use the exact identity assigned by Partner Center; placeholders are not allowed.'
    }
}
$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
$cl = Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe' |
    Sort-Object { [version]$_.Directory.Parent.Parent.Parent.Name } -Descending | Select-Object -First 1
if (-not $sdk -or -not $cl) { throw 'Windows SDK and current Visual Studio C++ Build Tools are required.' }
$sdkVersion = $sdk.Directory.Parent.Name
$makePri = Join-Path $sdk.Directory.FullName 'makepri.exe'
if (-not (Test-Path -LiteralPath $makePri)) { throw 'MakePri from the selected Windows SDK is required.' }
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$include = Join-Path $sdkRoot "Include\$sdkVersion"
$lib = Join-Path $sdkRoot "Lib\$sdkVersion"
$vc = $cl.Directory.Parent.Parent.Parent.FullName
New-Item -ItemType Directory -Path $output | Out-Null
$build = Join-Path $output 'build'
$payload = Join-Path $output 'payload'
$app = Join-Path $payload 'App'
New-Item -ItemType Directory -Force -Path $app | Out-Null
& (Join-Path $root 'build.ps1') -OutputDir ([IO.Path]::GetRelativePath($root, $build)) `
    -ObjectDir ([IO.Path]::GetRelativePath($root, (Join-Path $build 'native')))
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $build 'AirStereo.dll'))
if ($fileVersion.ProductVersion -ne $Version) { throw 'App version does not match the requested package version.' }
foreach ($name in @('AirStereo.exe', 'AirStereo.dll', 'AirStereo.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $build $name))) { throw "Missing build file: $name" }
    Copy-Item -LiteralPath (Join-Path $build $name) -Destination $app
}
$config = Get-Content -LiteralPath (Join-Path $build 'AirStereo.runtimeconfig.json') -Raw | ConvertFrom-Json
$dotnet = Join-Path $env:ProgramFiles 'dotnet'
$core = $config.runtimeOptions.frameworks | Where-Object name -eq 'Microsoft.NETCore.App'
$desktop = $config.runtimeOptions.frameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App'
if (-not $core -or -not $desktop) { throw 'Matching Core and Desktop runtimes must both be declared.' }
foreach ($framework in @($core, $desktop)) {
    $relative = "shared\$($framework.name)\$($framework.version)"
    $target = Join-Path $app $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $dotnet $relative) -Destination $target -Recurse
}
$fxr = "host\fxr\$($core.version)"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent (Join-Path $app $fxr)) | Out-Null
Copy-Item -LiteralPath (Join-Path $dotnet $fxr) -Destination (Join-Path $app $fxr) -Recurse
foreach ($name in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $dotnet $name) -Destination $app
}
$license = Join-Path $root 'LICENSE'
if (-not (Test-Path -LiteralPath $license)) { throw 'AirStereo MIT license is missing.' }
Copy-Item -LiteralPath $license -Destination (Join-Path $app 'AirStereo-LICENSE.txt')

# C++/WinRT bridge: package data path and Windows startup-task API, no registry emulation.
$previousInclude = $env:INCLUDE
$previousLib = $env:LIB
try {
    $env:INCLUDE = @((Join-Path $vc 'include'), "$include\ucrt", "$include\shared",
        "$include\um", "$include\winrt", "$include\cppwinrt") -join ';'
    $env:LIB = @((Join-Path $vc 'lib\x64'), "$lib\um\x64", "$lib\ucrt\x64") -join ';'
    $bridge = Join-Path $app 'AirStereo.Store.dll'
    & $cl.FullName /nologo /c /O2 /MT /EHsc /std:c++20 /guard:cf /W4 /utf-8 /DWINRT_NO_SOURCE_LOCATION `
        "/Fo$(Join-Path $build 'AirStereoStore.obj')" (Join-Path $root 'src\launcher\AirStereoStore.cpp')
    if ($LASTEXITCODE -ne 0) { throw 'Store bridge compilation failed.' }
    & (Join-Path $cl.Directory.FullName 'link.exe') /nologo /DLL /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /guard:cf `
        "/OUT:$bridge" "/IMPLIB:$(Join-Path $build 'AirStereo.Store.lib')" `
        (Join-Path $build 'AirStereoStore.obj') windowsapp.lib runtimeobject.lib ole32.lib kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Store bridge linking failed.' }
    & $cl.FullName /nologo /c /O2 /MT /guard:cf /W3 /utf-8 /DAIRSTEREO_STARTUP `
        "/Fo$(Join-Path $build 'AirStereoStartup.obj')" (Join-Path $root 'src\launcher\AirStereoLauncher.c')
    if ($LASTEXITCODE -ne 0) { throw 'Startup launcher compilation failed.' }
    & (Join-Path $cl.Directory.FullName 'link.exe') /nologo /SUBSYSTEM:WINDOWS /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /guard:cf `
        "/OUT:$(Join-Path $app 'AirStereoStartup.exe')" (Join-Path $build 'AirStereoStartup.obj') `
        (Join-Path $build 'native\AirStereoLauncher.res') kernel32.lib user32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Startup launcher linking failed.' }
} finally { $env:INCLUDE = $previousInclude; $env:LIB = $previousLib }

# Reuse the existing brand icon, render at each actual pixel size (not a renamed ICO).
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $payload 'Assets'
New-Item -ItemType Directory -Path $assets | Out-Null
function SaveLogo([int]$size, [string]$name) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $fill = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(19, 142, 148))
    $pen = [Drawing.Pen]::new([Drawing.Color]::White, [single](4 * $size / 64))
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([Drawing.Color]::Transparent)
        $scale = [single]($size / 64)
        $graphics.ScaleTransform($scale, $scale)
        $pen.Width = 4
        $graphics.FillEllipse($fill, 2, 2, 60, 60)
        $points = [Drawing.PointF[]]@([Drawing.PointF]::new(16,38), [Drawing.PointF]::new(22,38),
            [Drawing.PointF]::new(28,24), [Drawing.PointF]::new(36,46),
            [Drawing.PointF]::new(44,18), [Drawing.PointF]::new(50,38))
        $graphics.DrawLines($pen, $points)
        $bitmap.Save((Join-Path $assets $name), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $pen.Dispose(); $fill.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
SaveLogo 50 'StoreLogo.png'
SaveLogo 44 'Square44x44Logo.png'
SaveLogo 150 'Square150x150Logo.png'
foreach ($scale in @(125,150,200,400)) {
    SaveLogo ([int](44*$scale/100)) "Square44x44Logo.scale-$scale.png"
    SaveLogo ([int](150*$scale/100)) "Square150x150Logo.scale-$scale.png"
}
function XmlEscape([string]$value) { return [Security.SecurityElement]::Escape($value) }
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap desktop rescap">
 <Identity Name="$(XmlEscape $identity.Name)" Publisher="$(XmlEscape $identity.Publisher)" Version="$Version.0" ProcessorArchitecture="x64"/>
 <Properties>
  <DisplayName>$(XmlEscape $identity.DisplayName)</DisplayName>
  <PublisherDisplayName>$(XmlEscape $identity.PublisherDisplayName)</PublisherDisplayName>
  <Logo>Assets\StoreLogo.png</Logo>
 </Properties>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="$MinimumWindowsVersion" MaxVersionTested="$sdkVersion"/></Dependencies>
 <Resources><Resource Language="zh-CN"/></Resources>
 <Applications>
  <Application Id="AirStereo" Executable="App\AirStereo.exe" EntryPoint="Windows.FullTrustApplication">
   <uap:VisualElements DisplayName="$(XmlEscape $identity.DisplayName)" Description="AirStereo AirPlay audio sender"
    BackgroundColor="transparent" Square44x44Logo="Assets\Square44x44Logo.png" Square150x150Logo="Assets\Square150x150Logo.png"/>
   <Extensions>
    <desktop:Extension Category="windows.startupTask" Executable="App\AirStereoStartup.exe" EntryPoint="Windows.FullTrustApplication">
     <desktop:StartupTask TaskId="AirStereoStartup" Enabled="false" DisplayName="AirStereo"/>
    </desktop:Extension>
   </Extensions>
  </Application>
 </Applications>
 <Capabilities>
  <Capability Name="internetClientServer"/>
  <Capability Name="privateNetworkClientServer"/>
  <rescap:Capability Name="runFullTrust"/>
 </Capabilities>
</Package>
"@
$manifest | Set-Content -LiteralPath (Join-Path $payload 'AppxManifest.xml') -Encoding utf8
# Index scale-qualified icons so Windows can select the right asset at high DPI.
# Keep the configuration and diagnostic XML outside the shipped payload.
$priConfig = Join-Path $build 'priconfig.xml'
& $makePri createconfig /cf $priConfig /dq zh-CN /pv 10.0.0 2>&1 |
    Out-File -LiteralPath (Join-Path $output 'makepri-config.log') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'MakePri configuration failed; see makepri-config.log.' }
# Ship one complete package rather than generating separate scale resource packs.
[xml]$priXml = Get-Content -LiteralPath $priConfig -Raw
foreach ($node in @($priXml.SelectNodes('//packaging'))) {
    [void]$node.ParentNode.RemoveChild($node)
}
$priXml.Save($priConfig)
& $makePri new /pr $payload /cf $priConfig /in $identity.Name `
    /of (Join-Path $payload 'resources.pri') 2>&1 |
    Out-File -LiteralPath (Join-Path $output 'makepri-new.log') -Encoding utf8
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $payload 'resources.pri'))) {
    throw 'MakePri resource indexing failed; see makepri-new.log.'
}
$suffix = if ($LocalValidation) { '-local-validation' } else { '-store' }
$package = Join-Path $output "AirStereo-$Version-x64$suffix.msix"
# Do not pass /nv: MakeAppx must perform full schema and semantic validation.
& $sdk.FullName pack /d $payload /p $package /h SHA256 2>&1 |
    Out-File -LiteralPath (Join-Path $output 'makeappx-pack.log') -Encoding utf8
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath (Join-Path $output 'makeappx-pack.log') -Tail 20
    throw 'MSIX schema/semantic validation or packing failed.'
}
& $sdk.FullName unpack /p $package /d (Join-Path $output 'verified-unpack') 2>&1 |
    Out-File -LiteralPath (Join-Path $output 'makeappx-unpack.log') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'MSIX read-back validation failed; see makeappx-unpack.log.' }
$unpacked = Join-Path $output 'verified-unpack'
foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $unpacked $relative)).Hash) {
        throw "MSIX round-trip checksum mismatch: $relative"
    }
}
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
("$hash  " + [IO.Path]::GetFileName($package)) |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
[ordered]@{
    Version = "$Version.0"; Architecture = 'x64'; Identity = $identity
    LocalValidationOnly = [bool]$LocalValidation; Signed = $false
    StoreIdentityProvided = -not [bool]$LocalValidation; PartnerCenterValidated = $false
    WindowsSDK = $sdkVersion; MinimumWindowsVersion = $MinimumWindowsVersion
    ResourceIndexGenerated = $true
    CoreRuntime = $core.version; DesktopRuntime = $desktop.version
    SHA256 = $hash; Bytes = (Get-Item -LiteralPath $package).Length
    FilesVerified = @(Get-ChildItem -LiteralPath $payload -File -Recurse).Count
    DeployedTestPassed = $false; WackPassed = $false; AudioHardwareTestPassed = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'PackageInfo.json') -Encoding utf8
Write-Output "msix=$package"
Write-Output "sha256=$hash"
Write-Output "localValidationOnly=$LocalValidation signed=false deploymentTest=false wack=false"

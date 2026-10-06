[CmdletBinding()]
param(
    [string]$PayloadDirectory = 'packages\release-1.0.5\payload',
    [string]$OutputDirectory = 'packages\release-assets-1.0.5',
    [string]$Version = '1.0.5',
    [string]$WixExe = 'packages\wix-tools\wix-4.0.6\tools\net6.0\any\wix.exe'
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
function StableGuid([string]$name) {
    $bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('AirStereo.MSI.' + $name.ToLowerInvariant()))
    return [Guid]::new([byte[]]$bytes[0..15]).ToString().ToUpperInvariant()
}
$payload = WorkspacePath $PayloadDirectory
$output = WorkspacePath $OutputDirectory
$wix = WorkspacePath $WixExe
if (-not (Test-Path -LiteralPath $wix -PathType Leaf)) { throw "WiX not found: $wix" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
$info = Get-Content -LiteralPath (Join-Path $payload 'BuildInfo.json') -Raw | ConvertFrom-Json
if ($info.BuildId -ne $Version -or $info.Architecture -ne 'x64') { throw 'Payload version or architecture mismatch.' }
foreach ($entry in $info.Files) {
    $file = Join-Path $payload $entry.Name
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.SHA256) {
        throw "Payload checksum mismatch: $($entry.Name)"
    }
}
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'AirStereo.dll'))
if ($versionInfo.ProductVersion -ne $Version) { throw 'AirStereo.dll version mismatch.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$msi = Join-Path $output "AirStereo-$Version-x64.msi"
if (Test-Path -LiteralPath $msi) { throw 'MSI already exists. Choose a new output directory; never overwrite a release asset.' }
$work = Join-Path $output 'msi-build'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$wxs = Join-Path $work 'AirStereo.wxs'

$xml = [Xml.XmlDocument]::new()
$ns = 'http://wixtoolset.org/schemas/v4/wxs'
function Element([Xml.XmlNode]$parent, [string]$name, [hashtable]$attributes) {
    $element = $xml.CreateElement($name, $ns)
    foreach ($key in $attributes.Keys) { $element.SetAttribute($key, [string]$attributes[$key]) }
    [void]$parent.AppendChild($element)
    return $element
}
$rootElement = $xml.CreateElement('Wix', $ns)
[void]$xml.AppendChild($rootElement)
$package = Element $rootElement 'Package' @{
    Name = 'AirStereo'; Manufacturer = 'Flourishze'; Version = $Version
    UpgradeCode = 'BC748A75-41B0-4A54-9AE8-01C901419467'; Scope = 'perMachine'; InstallerVersion = '500'
}
[void](Element $package 'MajorUpgrade' @{ DowngradeErrorMessage = 'A newer version of AirStereo is already installed.' })
[void](Element $package 'MediaTemplate' @{ EmbedCab = 'yes'; CompressionLevel = 'high' })
$programFiles = Element $package 'StandardDirectory' @{ Id = 'ProgramFiles64Folder' }
$installDir = Element $programFiles 'Directory' @{ Id = 'INSTALLFOLDER'; Name = 'AirStereo' }
$directories = @{ '' = $installDir }
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse |
    Where-Object { $_.Name -notin @('Uninstall.exe', 'BuildInfo.json') } |
    Sort-Object FullName)
$index = 0
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
    $segments = $relative -split '\\'
    $parent = $installDir
    $subpath = ''
    for ($j = 0; $j -lt $segments.Length - 1; $j++) {
        $subpath = if ($subpath) { "$subpath\$($segments[$j])" } else { $segments[$j] }
        if (-not $directories.ContainsKey($subpath)) {
            $dir = Element $parent 'Directory' @{ Id = ('Dir_' + (StableGuid $subpath).Replace('-', '')); Name = $segments[$j] }
            $directories[$subpath] = $dir
        }
        $parent = $directories[$subpath]
    }
    $index++
    $component = Element $parent 'Component' @{ Id = "Component_$index"; Guid = (StableGuid $relative); Bitness = 'always64' }
    $fileElement = Element $component 'File' @{ Id = "File_$index"; Source = $file.FullName; KeyPath = 'yes' }
    if ($relative -eq 'AirStereo.exe') {
        [void](Element $fileElement 'Shortcut' @{ Id = 'StartMenuShortcut'; Directory = 'ProgramMenuFolder'; Name = 'AirStereo'; Advertise = 'no'; Arguments = 'gui'; WorkingDirectory = 'INSTALLFOLDER' })
        [void](Element $fileElement 'Shortcut' @{ Id = 'DesktopShortcut'; Directory = 'DesktopFolder'; Name = 'AirStereo'; Advertise = 'no'; Arguments = 'gui'; WorkingDirectory = 'INSTALLFOLDER' })
    }
}
$feature = Element $package 'Feature' @{ Id = 'MainFeature'; Title = 'AirStereo'; Level = '1' }
for ($i = 1; $i -le $index; $i++) { [void](Element $feature 'ComponentRef' @{ Id = "Component_$i" }) }
$xml.Save($wxs)
& $wix build $wxs -arch x64 -out $msi -intermediateFolder (Join-Path $work 'obj')
if ($LASTEXITCODE -ne 0) { throw "WiX build failed: $LASTEXITCODE" }
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + [IO.Path]::GetFileName($msi)) | Set-Content -LiteralPath (Join-Path $output "AirStereo-$Version-x64.msi.sha256") -Encoding ASCII
Write-Output "msi=$msi"
Write-Output "files=$index bytes=$((Get-Item -LiteralPath $msi).Length) sha256=$hash"




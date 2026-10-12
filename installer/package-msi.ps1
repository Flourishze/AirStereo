#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PayloadDirectory = 'packages\release-1.0.7\payload',
    [string]$OutputDirectory = 'packages\release-1.0.7\ready',
    [string]$Version = '1.0.7',
    [string]$WixExe = 'packages\wix-tools\wix-4.0.6\tools\net6.0\any\wix.exe',
    [string]$UiExtension = 'packages\wix-tools\wixtoolset.ui.wixext-4.0.6\wixext4\WixToolset.UI.wixext.dll',
    [string]$UtilExtension = 'packages\wix-tools\wixtoolset.util.wixext-4.0.6\wixext4\WixToolset.Util.wixext.dll',
    [string]$Localization = 'installer\resources\WixUI_zh-CN.wxl'
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
$ui = WorkspacePath $UiExtension
$util = WorkspacePath $UtilExtension
$loc = WorkspacePath $Localization
foreach ($extension in @($ui, $util)) {
    if (-not (Test-Path -LiteralPath $extension -PathType Leaf)) { throw "WiX 4 UI/Util extension not found: $extension" }
}
if (-not (Test-Path -LiteralPath $loc -PathType Leaf)) { throw "WiX Chinese localization not found: $loc" }
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
function ExtensionElement([Xml.XmlNode]$parent, [string]$prefix, [string]$name, [hashtable]$attributes) {
    $element = $xml.CreateElement($prefix, $name, ($ns + '/' + $prefix))
    foreach ($key in $attributes.Keys) { $element.SetAttribute($key, [string]$attributes[$key]) }
    [void]$parent.AppendChild($element)
    return $element
}
$rootElement = $xml.CreateElement('Wix', $ns)
[void]$rootElement.SetAttribute('xmlns:ui', 'http://wixtoolset.org/schemas/v4/wxs/ui')
[void]$rootElement.SetAttribute('xmlns:util', 'http://wixtoolset.org/schemas/v4/wxs/util')
[void]$xml.AppendChild($rootElement)
$package = Element $rootElement 'Package' @{
    Name = 'AirStereo'; Manufacturer = 'Flourishze'; Version = $Version
    UpgradeCode = 'BC748A75-41B0-4A54-9AE8-01C901419467'; Scope = 'perMachine'; InstallerVersion = '500'
    Language = '2052'; Codepage = '936'
}
# 1.0.6 was language-neutral (0). Detect it even though this wizard is zh-CN.
[void](Element $package 'MajorUpgrade' @{ DowngradeErrorMessage = '已经安装了更新版本的 AirStereo。'; Schedule = 'afterInstallInitialize'; IgnoreLanguage = 'yes'; AllowSameVersionUpgrades = 'yes' })
[void](Element $package 'MediaTemplate' @{ EmbedCab = 'yes'; CompressionLevel = 'high' })
# WixUI_InstallDir includes the standard FilesInUse/MsiRMFilesInUse dialogs and
# its own WixUIRMOption=UseRM property. Do not author that property twice.
[void](ExtensionElement $package 'ui' 'WixUI' @{ Id = 'WixUI_InstallDir'; InstallDirectory = 'INSTALLFOLDER' })
[void](Element $package 'WixVariable' @{ Id = 'WixUILicenseRtf'; Value = (Join-Path $payload 'AirStereo-License.rtf') })
[void](Element $package 'Icon' @{ Id = 'AirStereoIcon'; SourceFile = (Join-Path $payload 'AirStereo.ico') })
[void](Element $package 'Property' @{ Id = 'ARPPRODUCTICON'; Value = 'AirStereoIcon' })
[void](Element $package 'Property' @{ Id = 'WIXUI_EXITDIALOGOPTIONALTEXT'; Value = ('AirStereo ' + $Version + ' - Windows x64') })
# Close the GUI before InstallValidate inspects locked files. Only immediate
# close/end-session messages are used here: deferred elevated/terminate actions
# cannot run before InstallInitialize. Standard Restart Manager remains enabled.
foreach ($target in @('AirStereo.exe', 'AirStereo.WinUI.exe')) {
    [void](ExtensionElement $package 'util' 'CloseApplication' @{
        Id = $(if ($target -eq 'AirStereo.exe') { 'CloseAirStereo' } else { 'CloseLegacyWinUi' })
        Target = $target; CloseMessage = 'yes'; EndSessionMessage = 'yes'; RebootPrompt = 'no'; Timeout = '8'
    })
}
$executeSequence = Element $package 'InstallExecuteSequence' @{}
[void](Element $executeSequence 'Custom' @{
    Action = 'Wix4CloseApplications_X64'; Before = 'InstallValidate'; Condition = 'VersionNT > 400'
})
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
}
# Non-advertised shortcuts have their own HKCU key path; do not mix them with
# the per-machine executable component (ICE43/ICE57). Names and locations stay
# the same. No application preferences are written by the installer.
$shortcuts = Element $installDir 'Component' @{
    Id = 'ApplicationShortcuts'; Guid = (StableGuid 'ApplicationShortcuts'); Bitness = 'always64'
}
[void](Element $shortcuts 'Shortcut' @{ Id = 'StartMenuShortcut'; Directory = 'ProgramMenuFolder'; Name = 'AirStereo'; Target = '[INSTALLFOLDER]AirStereo.exe'; Arguments = 'gui'; WorkingDirectory = 'INSTALLFOLDER' })
[void](Element $shortcuts 'Shortcut' @{ Id = 'DesktopShortcut'; Directory = 'DesktopFolder'; Name = 'AirStereo'; Target = '[INSTALLFOLDER]AirStereo.exe'; Arguments = 'gui'; WorkingDirectory = 'INSTALLFOLDER' })
[void](Element $shortcuts 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\Flourishze\AirStereo\Installer'; Name = 'ShortcutsInstalled'; Type = 'integer'; Value = '1'; KeyPath = 'yes' })
$feature = Element $package 'Feature' @{ Id = 'MainFeature'; Title = 'AirStereo'; Level = '1' }
[void](Element $feature 'ComponentRef' @{ Id = 'ApplicationShortcuts' })
for ($i = 1; $i -le $index; $i++) { [void](Element $feature 'ComponentRef' @{ Id = "Component_$i" }) }
$xml.Save($wxs)
& $wix build $wxs -arch x64 -culture zh-CN -loc $loc -ext $ui -ext $util -out $msi -intermediateFolder (Join-Path $work 'obj')
if ($LASTEXITCODE -ne 0) { throw "WiX build failed: $LASTEXITCODE" }
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + [IO.Path]::GetFileName($msi)) | Set-Content -LiteralPath (Join-Path $output "AirStereo-$Version-x64.msi.sha256") -Encoding ASCII
Write-Output "msi=$msi"
Write-Output "files=$index bytes=$((Get-Item -LiteralPath $msi).Length) sha256=$hash"

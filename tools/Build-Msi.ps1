# Build a local AirStereo2 MSI for the isolated Fluent shell.
# This packages test-build without changing it.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PayloadDirectory = 'dist\release',
    [string]$OutputDirectory = 'dist',
    [string]$Version = '1.1.1',
    [string]$OutputFileName = '',
    [string]$PackagesRoot = '',
    [string]$WixExe = 'wix-tools\wix-4.0.6\tools\net6.0\any\wix.exe',
    [string]$UiExtension = 'wix-tools\wixtoolset.ui.wixext-4.0.6\wixext4\WixToolset.UI.wixext.dll',
    [string]$UtilExtension = 'wix-tools\wixtoolset.util.wixext-4.0.6\wixext4\WixToolset.Util.wixext.dll',
    [string]$Localization = 'tools\WixUI_AirStereo2_zh-CN.wxl',
    [string]$LicenseRtf = 'tools\AirStereo2-License.rtf',
    [switch]$SkipShellRepublish
)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
function Resolve-AppPath([string]$path) {
    $full = [IO.Path]::GetFullPath((Join-Path $appRoot $path))
    if (-not $full.StartsWith($appRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload and output paths must stay inside AirStereo2: $path"
    }
    return $full
}
if ([string]::IsNullOrWhiteSpace($PackagesRoot)) {
    $candidates = @(
        (Join-Path (Split-Path -Parent $appRoot) 'packages'),
        (Join-Path $appRoot 'packages')
    )
    $PackagesRoot = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($PackagesRoot)) { throw 'Packages directory not found; pass -PackagesRoot.' }
function Resolve-PackagesPath([string]$path) { return [IO.Path]::GetFullPath((Join-Path $PackagesRoot $path)) }
function StableGuid([string]$name) {
    $bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('AirStereo2.MSI.' + $name.ToLowerInvariant()))
    return [Guid]::new([byte[]]$bytes[0..15]).ToString().ToUpperInvariant()
}
$payload = Resolve-AppPath $PayloadDirectory
$output = Resolve-AppPath $OutputDirectory
$wix = Resolve-PackagesPath $WixExe
$ui = Resolve-PackagesPath $UiExtension
$util = Resolve-PackagesPath $UtilExtension
$loc = Resolve-AppPath $Localization
$license = Resolve-AppPath $LicenseRtf
if (-not (Test-Path -LiteralPath (Join-Path $payload 'AirStereo2.exe') -PathType Leaf)) { throw "Missing built payload: $payload\AirStereo2.exe" }
if (-not (Test-Path -LiteralPath $wix -PathType Leaf)) { throw "WiX not found: $wix" }
if (-not (Test-Path -LiteralPath $ui -PathType Leaf)) { throw "WiX UI extension not found: $ui" }
if (-not (Test-Path -LiteralPath $util -PathType Leaf)) { throw "WiX Util extension not found: $util" }
if (-not (Test-Path -LiteralPath $loc -PathType Leaf)) { throw "WiX localization not found: $loc" }
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) { throw "License RTF not found: $license" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
if ([string]::IsNullOrWhiteSpace($OutputFileName)) { $OutputFileName = "AirStereo2-$Version-x64.msi" }
$msi = Join-Path $output $OutputFileName
if (Test-Path -LiteralPath $msi) { throw "Refusing to overwrite existing MSI: $msi" }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$work = Join-Path $output 'msi-build'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$stagedPayload = Join-Path $work 'payload'
if (Test-Path -LiteralPath $stagedPayload) { Remove-Item -LiteralPath $stagedPayload -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stagedPayload | Out-Null
Get-ChildItem -LiteralPath $payload -Force | Where-Object Name -ne 'Data' | Copy-Item -Destination $stagedPayload -Recurse -Force
Remove-Item -LiteralPath (Join-Path $stagedPayload 'engine\Data') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $stagedPayload 'engine\Assets\Testing') -Recurse -Force -ErrorAction SilentlyContinue
if (-not $SkipShellRepublish) {
    $shellProject = Join-Path $appRoot 'source\Shell\AirStereo2.csproj'
    $shellOutput = Join-Path $work 'AirStereo2-shell'
    $dotnet = Resolve-PackagesPath 'AirStereo-dev\tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "Bundled .NET SDK host not found: $dotnet" }
    & $dotnet publish $shellProject --no-restore -c Release -o $shellOutput
    if ($LASTEXITCODE -ne 0) { throw "AirStereo2 shell publish failed: $LASTEXITCODE" }
    $shellPri = Join-Path $appRoot 'source\Shell\bin\Release\net8.0-windows10.0.19041.0\win-x64\AirStereo2.pri'
    if (-not (Test-Path -LiteralPath $shellPri -PathType Leaf)) { throw "Generated AirStereo2 PRI not found: $shellPri" }
    Get-ChildItem -LiteralPath $shellOutput -File | Where-Object Extension -ne '.pdb' | Copy-Item -Destination $stagedPayload -Force
    Copy-Item -LiteralPath $shellPri -Destination $stagedPayload -Force
}
$rootLicense = Join-Path $appRoot 'LICENSE'
if (Test-Path -LiteralPath $rootLicense -PathType Leaf) { Copy-Item -LiteralPath $rootLicense -Destination $stagedPayload -Force }
foreach ($name in 'AirStereo2.exe','AirStereo2.dll','AirStereo2.Core.dll','AirStereo2.Engine.exe','AirStereo2.deps.json','AirStereo2.runtimeconfig.json','AirStereo2.pri') {
    if (-not (Test-Path -LiteralPath (Join-Path $stagedPayload $name) -PathType Leaf)) { throw "Missing staged payload file: $name" }
}
$payload = $stagedPayload
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
[void]$rootElement.SetAttribute('xmlns:ui', $ns + '/ui')
[void]$rootElement.SetAttribute('xmlns:util', $ns + '/util')
[void]$xml.AppendChild($rootElement)
$package = Element $rootElement 'Package' @{
    Name = 'AirStereo2'; Manufacturer = 'Flourishze'; Version = $Version
    UpgradeCode = '6A7264AC-1FE7-442C-9770-42DD27D21EB8'; Scope = 'perMachine'; InstallerVersion = '500'
    Language = '2052'; Codepage = '936'
}
# Detect any prior AirStereo2 installation regardless of language or version and perform clean overwrite.
# Schedule="afterInstallInitialize" removes old files first (avoiding Error 1310/1307 .rbf owner issues on non-C: drives),
# while REINSTALLMODE="amus" forces all files (including shared MUI DLLs like Microsoft.ui.xaml.dll) to be installed cleanly.
[void](Element $package 'MajorUpgrade' @{ AllowDowngrades = 'yes'; Schedule = 'afterInstallInitialize'; IgnoreLanguage = 'yes' })
[void](Element $package 'Property' @{ Id = 'REINSTALLMODE'; Value = 'amus' })
[void](Element $package 'MediaTemplate' @{ EmbedCab = 'yes'; CompressionLevel = 'high' })
[void](ExtensionElement $package 'ui' 'WixUI' @{ Id = 'WixUI_InstallDir'; InstallDirectory = 'INSTALLFOLDER' })
$stagedLicense = Join-Path $work 'AirStereo2-License.rtf'
$licenseText = [IO.File]::ReadAllText($license, [Text.Encoding]::ASCII)
$licenseText = [Regex]::Replace($licenseText, 'AirStereo2 \d+\.\d+\.\d+', "AirStereo2 $Version")
[IO.File]::WriteAllText($stagedLicense, $licenseText, [Text.Encoding]::ASCII)
[void](Element $package 'WixVariable' @{ Id = 'WixUILicenseRtf'; Value = $stagedLicense })
[void](Element $package 'Icon' @{ Id = 'AirStereo2Icon'; SourceFile = (Join-Path $payload 'AirStereo2.ico') })
[void](Element $package 'Property' @{ Id = 'ARPPRODUCTICON'; Value = 'AirStereo2Icon' })
[void](Element $package 'Property' @{ Id = 'ARPCOMMENTS'; Value = "AirStereo2 $Version - Windows 立体声 AirPlay 音频发送器（MIT 开源）" })
[void](Element $package 'Property' @{ Id = 'ARPURLINFOABOUT'; Value = 'https://github.com/Flourishze/AirStereo' })
[void](Element $package 'Property' @{ Id = 'ARPHELPLINK'; Value = 'https://github.com/Flourishze/AirStereo/issues' })
[void](Element $package 'Property' @{ Id = 'ARPURLUPDATEINFO'; Value = 'https://github.com/Flourishze/AirStereo/releases' })
[void](Element $package 'Property' @{ Id = 'WIXUI_EXITDIALOGOPTIONALTEXT'; Value = "AirStereo2 $Version · https://github.com/Flourishze/AirStereo" })
[void](Element $package 'Property' @{ Id = 'WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'; Value = '立即启动 AirStereo2' })
[void](Element $package 'Property' @{ Id = 'WIXUI_EXITDIALOGOPTIONALCHECKBOX'; Value = '1' })

# Remember and automatically reuse the previous install folder during upgrades.
$prevInstallProp = Element $package 'Property' @{ Id = 'PREVIOUS_INSTALLFOLDER' }
[void](Element $prevInstallProp 'RegistrySearch' @{
    Id = 'FindPreviousInstallFolder'; Root = 'HKLM'; Key = 'Software\Flourishze\AirStereo2'
    Name = 'InstallFolder'; Type = 'raw'; Bitness = 'always64'
})
[void](Element $package 'SetProperty' @{ Id = 'INSTALLFOLDER'; Value = '[PREVIOUS_INSTALLFOLDER]'; After = 'AppSearch'; Condition = 'PREVIOUS_INSTALLFOLDER' })
[void](Element $package 'SetProperty' @{ Id = 'ARPINSTALLLOCATION'; Value = '[INSTALLFOLDER]'; After = 'CostFinalize' })

# Surface clear Chinese overwrite/upgrade notices when an existing installation is detected.
[void](Element $package 'SetProperty' @{
    Id = 'UPGRADE_NOTICE'
    Value = '检测到本机已安装 AirStereo2，继续操作将自动关闭正在运行的实例并覆盖更新现有程序。'
    After = 'FindRelatedProducts'; Sequence = 'ui'; Condition = 'WIX_UPGRADE_DETECTED'
})
[void](Element $package 'SetProperty' @{
    Id = 'INSTALLDIR_HINT'
    Value = '已检测到现有安装，可沿用原目录覆盖更新或更改新位置。'
    After = 'FindRelatedProducts'; Sequence = 'ui'; Condition = 'WIX_UPGRADE_DETECTED'
})

# Gracefully close running AirStereo2 & Engine before InstallValidate checks locked files.
[void](ExtensionElement $package 'util' 'CloseApplication' @{
    Id = 'CloseAirStereo2'; Target = 'AirStereo2.exe'
    CloseMessage = 'yes'; EndSessionMessage = 'yes'; RebootPrompt = 'no'; Timeout = '8'
})
[void](ExtensionElement $package 'util' 'CloseApplication' @{
    Id = 'CloseAirStereo2Engine'; Target = 'AirStereo2.Engine.exe'
    CloseMessage = 'yes'; EndSessionMessage = 'yes'; RebootPrompt = 'no'; Timeout = '5'
})
$executeSequence = Element $package 'InstallExecuteSequence' @{}
[void](Element $executeSequence 'Custom' @{
    Action = 'Wix4CloseApplications_X64'; Before = 'InstallValidate'; Condition = 'VersionNT > 400'
})

$programFiles = Element $package 'StandardDirectory' @{ Id = 'ProgramFiles64Folder' }
$installDir = Element $programFiles 'Directory' @{ Id = 'INSTALLFOLDER'; Name = 'AirStereo2' }
$directories = @{ '' = $installDir }
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Where-Object Extension -ne '.pdb' | Sort-Object FullName)
$index = 0
$exeFileId = ''
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
    $fileId = "File_$index"
    if ($relative -ieq 'AirStereo2.exe') { $exeFileId = $fileId }
    $component = Element $parent 'Component' @{ Id = "Component_$index"; Guid = (StableGuid $relative); Bitness = 'always64' }
    [void](Element $component 'File' @{ Id = $fileId; Source = $file.FullName; KeyPath = 'yes' })
}
if ([string]::IsNullOrWhiteSpace($exeFileId)) { throw 'AirStereo2.exe was not found in staged payload.' }

# Launch AirStereo2 after finishing installation when the checkbox is checked.
[void](Element $package 'Property' @{ Id = 'WixShellExecTarget'; Value = "[#$exeFileId]" })
[void](Element $package 'CustomActionRef' @{ Id = 'Wix4ShellExec_X64' })
$uiBlock = Element $package 'UI' @{}
[void](Element $uiBlock 'Publish' @{
    Dialog = 'ExitDialog'; Control = 'Finish'; Event = 'DoAction'; Value = 'Wix4ShellExec_X64'
    Condition = 'WIXUI_EXITDIALOGOPTIONALCHECKBOX = 1 and NOT Installed'
})

# Persist install path & version in HKLM so upgrades remember the directory.
$metadata = Element $installDir 'Component' @{ Id = 'InstallMetadata'; Guid = (StableGuid 'InstallMetadata'); Bitness = 'always64' }
[void](Element $metadata 'RegistryValue' @{ Root = 'HKLM'; Key = 'Software\Flourishze\AirStereo2'; Name = 'InstallFolder'; Type = 'string'; Value = '[INSTALLFOLDER]'; KeyPath = 'yes' })
[void](Element $metadata 'RegistryValue' @{ Root = 'HKLM'; Key = 'Software\Flourishze\AirStereo2'; Name = 'Version'; Type = 'string'; Value = $Version })

# Create writable Data folders under Program Files so standard users can save settings/logs.
$appDataDir = Element $installDir 'Directory' @{ Id = 'Dir_AppData'; Name = 'Data' }
$appDataComp = Element $appDataDir 'Component' @{ Id = 'AppDataFolderComponent'; Guid = (StableGuid 'DataFolder'); Bitness = 'always64' }
$appDataCreate = Element $appDataComp 'CreateFolder' @{}
[void](ExtensionElement $appDataCreate 'util' 'PermissionEx' @{ User = 'Users'; GenericAll = 'yes' })

$hasLegacyEngineDir = $directories.ContainsKey('engine')
if ($hasLegacyEngineDir) {
    $engineDir = $directories['engine']
    $engineDataDir = Element $engineDir 'Directory' @{ Id = 'Dir_EngineData'; Name = 'Data' }
    $engineDataComp = Element $engineDataDir 'Component' @{ Id = 'EngineDataFolderComponent'; Guid = (StableGuid 'EngineDataFolder'); Bitness = 'always64' }
    $engineDataCreate = Element $engineDataComp 'CreateFolder' @{}
    [void](ExtensionElement $engineDataCreate 'util' 'PermissionEx' @{ User = 'Users'; GenericAll = 'yes' })
}

$shortcutDesc = 'AirStereo2 · 立体声 AirPlay 音频发送器'
$shortcuts = Element $installDir 'Component' @{ Id = 'ApplicationShortcut'; Guid = (StableGuid 'ApplicationShortcut'); Bitness = 'always64' }
[void](Element $shortcuts 'Shortcut' @{ Id = 'StartMenuShortcut'; Directory = 'ProgramMenuFolder'; Name = 'AirStereo2'; Description = $shortcutDesc; Target = '[INSTALLFOLDER]AirStereo2.exe'; WorkingDirectory = 'INSTALLFOLDER'; Icon = 'AirStereo2Icon' })
[void](Element $shortcuts 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\Flourishze\AirStereo2\Installer'; Name = 'ShortcutInstalled'; Type = 'integer'; Value = '1'; KeyPath = 'yes' })
$desktopShortcuts = Element $installDir 'Component' @{ Id = 'DesktopShortcutComponent'; Guid = (StableGuid 'DesktopShortcut'); Bitness = 'always64' }
[void](Element $desktopShortcuts 'Shortcut' @{ Id = 'DesktopShortcut'; Directory = 'DesktopFolder'; Name = 'AirStereo2'; Description = $shortcutDesc; Target = '[INSTALLFOLDER]AirStereo2.exe'; WorkingDirectory = 'INSTALLFOLDER'; Icon = 'AirStereo2Icon' })
[void](Element $desktopShortcuts 'RegistryValue' @{ Root = 'HKCU'; Key = 'Software\Flourishze\AirStereo2\Installer'; Name = 'DesktopShortcutInstalled'; Type = 'integer'; Value = '1'; KeyPath = 'yes' })

$feature = Element $package 'Feature' @{ Id = 'MainFeature'; Title = 'AirStereo2'; Level = '1' }
[void](Element $feature 'ComponentRef' @{ Id = 'InstallMetadata' })
[void](Element $feature 'ComponentRef' @{ Id = 'AppDataFolderComponent' })
if ($hasLegacyEngineDir) {
    [void](Element $feature 'ComponentRef' @{ Id = 'EngineDataFolderComponent' })
}
[void](Element $feature 'ComponentRef' @{ Id = 'ApplicationShortcut' })
[void](Element $feature 'ComponentRef' @{ Id = 'DesktopShortcutComponent' })
for ($i = 1; $i -le $index; $i++) { [void](Element $feature 'ComponentRef' @{ Id = "Component_$i" }) }

$wxs = Join-Path $work 'AirStereo2.wxs'
$xml.Save($wxs)
& $wix build $wxs -arch x64 -culture zh-CN -loc $loc -ext $ui -ext $util -out $msi -intermediateFolder (Join-Path $work 'obj')
if ($LASTEXITCODE -ne 0) { throw "WiX build failed: $LASTEXITCODE" }

# Eliminate the gray COLOR_BTNFACE rectangle behind ExitDialog's OptionalCheckBox on the white banner:
# shrink the CheckBox control to the exact 13x13 box (9x8 DLU) and render its label via a transparent Text control.
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class MsiPatcher {
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiOpenDatabaseW(string szDatabasePath, IntPtr szPersist, out uint phDatabase);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiDatabaseOpenViewW(uint hDatabase, string szQuery, out uint phView);
    [DllImport("msi.dll")]
    static extern uint MsiViewExecute(uint hView, uint hRecord);
    [DllImport("msi.dll")]
    static extern uint MsiViewClose(uint hView);
    [DllImport("msi.dll")]
    static extern uint MsiDatabaseCommit(uint hDatabase);
    [DllImport("msi.dll")]
    static extern uint MsiCloseHandle(uint hAny);

    public static void Exec(string msiPath, params string[] queries) {
        uint db;
        uint rc = MsiOpenDatabaseW(msiPath, (IntPtr)1, out db); // MSIDBOPEN_TRANSACT
        if (rc != 0) throw new InvalidOperationException("MsiOpenDatabaseW failed: " + rc);
        try {
            foreach (string sql in queries) {
                uint view;
                rc = MsiDatabaseOpenViewW(db, sql, out view);
                if (rc != 0) throw new InvalidOperationException("MsiDatabaseOpenViewW failed (" + rc + "): " + sql);
                try {
                    rc = MsiViewExecute(view, 0);
                    if (rc != 0) throw new InvalidOperationException("MsiViewExecute failed (" + rc + "): " + sql);
                    MsiViewClose(view);
                } finally { MsiCloseHandle(view); }
            }
            rc = MsiDatabaseCommit(db);
            if (rc != 0) throw new InvalidOperationException("MsiDatabaseCommit failed: " + rc);
        } finally { MsiCloseHandle(db); }
    }
}
"@
[MsiPatcher]::Exec($msi,
    "UPDATE ``Control`` SET ``X``=135, ``Y``=204, ``Width``=9, ``Height``=8, ``Text``='' WHERE ``Dialog_``='ExitDialog' AND ``Control``='OptionalCheckBox'",
    "INSERT INTO ``Control`` (``Dialog_``, ``Control``, ``Type``, ``X``, ``Y``, ``Width``, ``Height``, ``Attributes``, ``Property``, ``Text``, ``Control_Next``, ``Help``) VALUES ('ExitDialog', 'OptionalCheckBoxLabel', 'Text', 148, 204, 205, 14, 196610, '', '[WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT]', '', '')",
    "INSERT INTO ``ControlCondition`` (``Dialog_``, ``Control_``, ``Action``, ``Condition``) VALUES ('ExitDialog', 'OptionalCheckBoxLabel', 'Show', 'WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT AND NOT Installed')"
)

$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + [IO.Path]::GetFileName($msi)) | Set-Content -LiteralPath ($msi + '.sha256') -Encoding ASCII
Write-Output "Built MSI: $msi"
Write-Output "SHA-256: $hash"


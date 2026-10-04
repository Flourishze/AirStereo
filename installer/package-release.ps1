[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$BuildId,
    [string]$BaseInstaller = 'installer\template\AirStereo-Setup-base.exe'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function WorkspacePath([string]$relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package paths must remain inside the AirStereo workspace.'
    }
    return $path
}
$build = WorkspacePath $BuildDirectory
# Do not ship an installer with labels that disagree with the application payload.
if ($BuildId -notmatch '^\d+\.\d+\.\d+$') { throw 'BuildId must be a stable major.minor.patch version.' }
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $build 'AirStereo.dll'))
if ($appVersion.ProductVersion -ne $BuildId -or $appVersion.FileVersion -ne ($BuildId + '.0')) {
    throw "Application payload version does not match BuildId $BuildId. Rebuild the matching source first."
}
$output = WorkspacePath $OutputDirectory
$installer = Join-Path $output 'AirStereo-Setup.exe'
if (Test-Path -LiteralPath $installer) { throw 'An installer already exists here. Choose a new output directory.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$payload = Join-Path $output 'payload'
if (Test-Path -LiteralPath $payload) { throw 'Payload directory already exists. Choose a new output directory.' }
New-Item -ItemType Directory -Path $payload | Out-Null
$programFiles = @('AirStereo.dll', 'AirStereo.exe', 'AirStereo.runtimeconfig.json', 'AirStereo.cmd')
foreach ($name in $programFiles) {
    $file = Join-Path $build $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing build file: $name" }
    Copy-Item -LiteralPath $file -Destination (Join-Path $payload $name)
}
$config = Get-Content -LiteralPath (Join-Path $build 'AirStereo.runtimeconfig.json') -Raw | ConvertFrom-Json
$dotnet = Join-Path $env:ProgramFiles 'dotnet'
$core = $config.runtimeOptions.frameworks | Where-Object name -eq 'Microsoft.NETCore.App'
$desktop = $config.runtimeOptions.frameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App'
if ($null -eq $core -or $null -eq $desktop) { throw 'Both Core and Desktop runtimes must be declared.' }
foreach ($framework in @($core, $desktop)) {
    $relative = 'shared\' + $framework.name + '\' + $framework.version
    $source = Join-Path $dotnet $relative
    if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Matching runtime is missing: $relative" }
    $target = Join-Path $payload $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Recurse
}
$fxrRelative = 'host\fxr\' + $core.version
$fxrSource = Join-Path $dotnet $fxrRelative
if (-not (Test-Path -LiteralPath $fxrSource -PathType Container)) { throw 'Matching hostfxr is missing.' }
$fxrTarget = Join-Path $payload $fxrRelative
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fxrTarget) | Out-Null
Copy-Item -LiteralPath $fxrSource -Destination $fxrTarget -Recurse
foreach ($name in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $dotnet $name) -Destination (Join-Path $payload $name)
}
# Reuse the established installation wizard and independent uninstaller, but replace
# the embedded payload as a real managed resource rather than fixed-length byte patching.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$cecil = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\18\BuildTools\Common7\IDE\Extensions\TestPlatform\Extensions\Mono.Cecil.dll'
Add-Type -Path $cecil
. (Join-Path $PSScriptRoot 'patch-installer.ps1')
$baseInstaller = WorkspacePath $BaseInstaller
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($baseInstaller)
try {
    $resource = $assembly.MainModule.Resources | Where-Object Name -eq 'AirStereoPayload.zip' | Select-Object -First 1
    if ($null -eq $resource) { throw 'Existing installer has no expected payload.' }
    $stream = [IO.MemoryStream]::new($resource.GetResourceData())
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $entry = $archive.GetEntry('Uninstall.exe')
        if ($null -eq $entry) { throw 'Existing independent uninstaller is missing.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $payload 'Uninstall.exe'))
    } finally { $archive.Dispose(); $stream.Dispose() }
    $buildInfo = [ordered]@{
        Kind = 'GitHubRelease'; BuildId = $BuildId; Architecture = 'x64'
        CoreRuntime = $core.version; DesktopRuntime = $desktop.version
        FormalRelease = $true; Version = $BuildId; GitHubUploaded = $false
        Files = @($programFiles | ForEach-Object {
            @{ Name = $_; SHA256 = (Get-FileHash -LiteralPath (Join-Path $payload $_) -Algorithm SHA256).Hash }
        })
    }
    $buildInfo | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $payload 'BuildInfo.json') -Encoding UTF8
    foreach ($licenseName in @('LICENSE', 'README.md', 'README.en.md')) {
        Copy-Item -LiteralPath (Join-Path $root $licenseName) -Destination (Join-Path $payload ('AirStereo-' + $licenseName))
    }
    # Avoid a PowerShell here-string here: on some localized PowerShell hosts the
    # UTF-8 script is tokenized incorrectly when the closing marker follows CJK
    # text.  Joining ordinary interpolated lines produces the same release notes.
    $notes = @(
        "AirStereo 正式版本 $BuildId（Windows x64）"
        ""
        "包含：托盘弹出设备列表、深色设置与均衡器背景、开机自启、故障记录和打开故障文件夹、勾选设备时窗口不再跳动、主动停止不再误记连接故障、GitHub 手动检查更新。"
        "内置匹配的 .NET Core / Desktop $($core.version) 运行时，程序优先使用安装目录内的 dotnet.exe。"
        "保留单设备完整立体声、自选双设备 L/R 与原生配对音频链路，以及 EQ、平衡和测试音。"
        ""
        "安装前请从右下角托盘菜单退出旧版。可选择原目录进行覆盖安装。"
        "本版改进：采集与重采样缓冲上限、滚动日志撤销历史清理、首次打开托盘窗口后点击桌面自动收回。"
        "故障记录保存在软件目录 Diagnostics；目录不可写时回退到当前用户的本地应用数据目录。"
        "可在设置的故障记录页查看，并直接打开实际存储目录。"
        ""
        "版本信息以指定仓库 https://github.com/Flourishze/AirStereo 的正式 Release 为准。"
    ) -join [Environment]::NewLine
    $notes | Set-Content -LiteralPath (Join-Path $payload '发布说明.txt') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $payload '发布说明.txt') -Destination (Join-Path $output '发布说明.txt')
    $zip = Join-Path $output 'AirStereoPayload.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $assembly.MainModule.Resources.Remove($resource) | Out-Null
    $newResource = [Mono.Cecil.EmbeddedResource]::new('AirStereoPayload.zip', [Mono.Cecil.ManifestResourceAttributes]::Private, [IO.File]::ReadAllBytes($zip))
    $assembly.MainModule.Resources.Add($newResource)
    $setupType = $assembly.MainModule.Types | Where-Object Name -eq 'AirStereoSetup'
    $wizard = $setupType.NestedTypes | Where-Object Name -eq 'WizardForm'
    if ($null -eq $wizard) { throw 'Expected installer wizard was not found.' }
    $uninstallVersionCount = 0
    $registerMethod = $setupType.Methods | Where-Object Name -eq 'RegisterUninstall' | Select-Object -First 1
    if ($null -ne $registerMethod -and $registerMethod.HasBody) {
        $instructions = @($registerMethod.Body.Instructions)
        for ($i = 0; $i -lt $instructions.Count - 1; $i++) {
            $current = $instructions[$i]
            $next = $instructions[$i + 1]
            if ($current.OpCode.Code -eq 'Ldstr' -and $current.Operand -eq 'DisplayVersion' -and
                $next.OpCode.Code -eq 'Ldstr' -and $next.Operand -is [string] -and
                $next.Operand -match '^\d+\.\d+(?:\.\d+)?$') {
                $next.Operand = $BuildId
                $uninstallVersionCount++
            }
        }
    }
    if ($uninstallVersionCount -ne 1) { throw "Unexpected uninstall version fields: $uninstallVersionCount" }
    $labelCount = 0
    foreach ($method in $wizard.Methods) {
        if (-not $method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            if ($instruction.OpCode.Code -ne 'Ldstr') { continue }
            if ($instruction.Operand.StartsWith('HomePod 立体声 AirPlay 发送器') -or $instruction.Operand.StartsWith('版本 ')) {
                $instruction.Operand = [regex]::Replace($instruction.Operand, '(?<=版本 )\d+\.\d+(?:\.\d+)?', $BuildId)
                $versionLabel = [regex]::Match($instruction.Operand, '(?<=版本 )\d+\.\d+(?:\.\d+)?').Value
                if ($versionLabel -ne $BuildId) { throw 'Installer wizard version label was not updated.' }
                $labelCount++
            }
        }
    }
    if ($labelCount -lt 1) { throw "Unexpected wizard identity labels: $labelCount" }
    # The established previous-path installer template already contains this
    # method. Reuse it instead of injecting a second copy; older templates are
    # still supported through the Cecil patch as a fallback.
    $hasPreviousInstallPathSupport = @(
        $setupType.Methods | Where-Object Name -eq 'GetPreviousInstallLocation'
    ).Count -gt 0
    if (-not $hasPreviousInstallPathSupport) {
        Add-PreviousInstallPathSupport -Assembly $assembly
    }
    $assembly.Name.Version = [version]($BuildId + '.0')
    $assembly.Write($installer)
} finally { $assembly.Dispose() }
. (Join-Path $PSScriptRoot 'version-installer.ps1')
Set-InstallerFileVersion -Installer $installer -Application (Join-Path $payload 'AirStereo.exe') -Version $BuildId
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
($hash + '  AirStereo-Setup.exe') | Set-Content -LiteralPath (Join-Path $output 'AirStereo-Setup.exe.sha256') -Encoding ASCII
$metadata = [ordered]@{
    BuildId = $BuildId; Kind = 'GitHubRelease'; Installer = 'AirStereo-Setup.exe'
    SHA256 = $hash; Bytes = (Get-Item -LiteralPath $installer).Length
    PayloadEntries = @(Get-ChildItem -LiteralPath $payload -File -Recurse).Count
    CoreRuntime = $core.version; DesktopRuntime = $desktop.version
    GitHubUploaded = $false
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'PackageInfo.json') -Encoding UTF8
Write-Output "installer=$installer"
Write-Output "sha256=$hash"
Write-Output "bytes=$($metadata.Bytes) payloadEntries=$($metadata.PayloadEntries)"



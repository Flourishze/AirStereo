[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Shortcut,
    [string]$Arguments = 'gui',
    [string]$OutputDir = 'dist'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path $compiler)) {
    throw 'A current Roslyn compiler from Visual Studio Build Tools is required.'
}

# The machine has .NET runtimes but no SDK, so the shared framework directories are
# used directly as reference assemblies. Native libraries are filtered out.
$dotnetRoot = Join-Path $env:ProgramFiles 'dotnet'

function Select-SharedFramework([string]$name, [string]$pattern) {
    $parent = Join-Path $dotnetRoot "shared\$name"
    if (-not (Test-Path $parent)) { return $null }
    return Get-ChildItem $parent -Directory |
        Where-Object { $_.Name -like $pattern } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
}

$desktop = Select-SharedFramework 'Microsoft.WindowsDesktop.App' '*'
if (-not $desktop) {
    throw 'No Windows Desktop Runtime is installed.'
}
$runtimeVersion = [version]$desktop.Name
$shared = Select-SharedFramework 'Microsoft.NETCore.App' "$($runtimeVersion.Major).*"
if (-not $shared) { throw "No .NET $($runtimeVersion.Major) core runtime matching the desktop runtime is installed." }

$outputDirectory = Join-Path $root $OutputDir
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory 'AirStereo.dll'

$sources = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.cs' |
    ForEach-Object { $_.FullName }

$cscArgs = New-Object System.Collections.Generic.List[string]
# csc.rsp next to the compiler references the .NET Framework assemblies by default, which
# would drag System.Windows.Forms 4.0.0.0 and System.Drawing 4.0.0.0 into a .NET 8 build and
# make the desktop types ambiguous.
$cscArgs.Add('/noconfig')
$cscArgs.Add('/nologo')
$cscArgs.Add('/target:exe')
$cscArgs.Add('/nostdlib')
$cscArgs.Add('/langversion:latest')
$cscArgs.Add('/optimize+')
$cscArgs.Add('/deterministic+')
$cscArgs.Add('/warn:4')
$cscArgs.Add("/out:$output")

# Microsoft.NETCore.App and Microsoft.WindowsDesktop.App overlap on a handful of assembly
# names (Microsoft.VisualBasic, System.Drawing, WindowsBase). The desktop copies are the
# ones that belong to a desktop app, so they win, and each name is referenced once.
# The desktop framework carries a few native WPF/CRT libraries beside the managed ones.
$nativePattern = 'Native|_cor3|^DirectWriteForwarder|^clr|^coreclr|^hostpolicy|^mscord|^mscorrc|^msquic|^ucrtbase|^mscoree'
$references = [ordered]@{}
foreach ($directory in @($shared.FullName, $desktop.FullName)) {
    Get-ChildItem $directory -Filter *.dll |
        Where-Object { $_.Name -notmatch $nativePattern } |
        ForEach-Object { $references[$_.Name.ToLowerInvariant()] = $_.FullName }
}
foreach ($path in $references.Values) { $cscArgs.Add("-r:$path") }

foreach ($source in $sources) { $cscArgs.Add($source) }

& $compiler $cscArgs
if ($LASTEXITCODE -ne 0) { throw "compilation failed with exit code $LASTEXITCODE" }

# Both frameworks have to be named, otherwise the loader cannot find System.Windows.Forms.
$runtimeConfig = Join-Path $outputDirectory 'AirStereo.runtimeconfig.json'
$runtimeOptions = @{ runtimeOptions = @{
    tfm = "net$($runtimeVersion.Major).0-windows"
    rollForward = 'LatestPatch'
    frameworks = @(
        @{ name = 'Microsoft.NETCore.App'; version = $shared.Name },
        @{ name = 'Microsoft.WindowsDesktop.App'; version = $desktop.Name }
    )
} }
$runtimeOptions | ConvertTo-Json -Depth 5 | Set-Content -Path $runtimeConfig -Encoding UTF8

$launcher = Join-Path $outputDirectory 'AirStereo.cmd'
@'
@echo off
if "%~1"=="" (
  dotnet "%~dp0AirStereo.dll" help
) else (
  dotnet "%~dp0AirStereo.dll" %*
)
'@ | Set-Content -Path $launcher -Encoding ASCII

# AirStereo.exe is a small native stub that starts the managed assembly. It is only built
# when a C toolchain is present; the app works without it through AirStereo.cmd.
function Build-LauncherIcon([string]$path) {
    $bitmap = $null
    $graphics = $null
    $icon = $null
    $stream = $null
    try {
        Add-Type -AssemblyName System.Drawing
        $bitmap = [System.Drawing.Bitmap]::new(64, 64)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(19, 142, 148))
        $line = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 4)
        try {
            $graphics.FillEllipse($fill, 2, 2, 60, 60)
            $graphics.DrawLine($line, 16, 38, 22, 38)
            $graphics.DrawLine($line, 22, 38, 28, 24)
            $graphics.DrawLine($line, 28, 24, 36, 46)
            $graphics.DrawLine($line, 36, 46, 44, 18)
            $graphics.DrawLine($line, 44, 18, 50, 38)
        }
        finally {
            $fill.Dispose()
            $line.Dispose()
        }

        $handle = $bitmap.GetHicon()
        $icon = [System.Drawing.Icon]::FromHandle($handle)
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Create)
        $icon.Save($stream)
        return $true
    }
    catch {
        Write-Warning ("could not create launcher icon: " + $_.Exception.Message)
        return $false
    }
    finally {
        if ($stream) { $stream.Dispose() }
        if ($icon) { $icon.Dispose() }
        if ($graphics) { $graphics.Dispose() }
        if ($bitmap) { $bitmap.Dispose() }
    }
}

function Build-Launcher([string]$OutputDirectory) {
    $source = Join-Path $root 'src\launcher\AirStereoLauncher.c'
    if (-not (Test-Path $source)) { return $null }

    $cl = Get-ChildItem 'C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Parent.Parent.Name } -Descending |
        Select-Object -First 1
    if (-not $cl) { return $null }

    # ...\VC\Tools\MSVC\<version>\bin\Hostx64\x64\cl.exe
    $vcTools = $cl.Directory.Parent.Parent.Parent.FullName
    $link = Join-Path $cl.Directory.FullName 'link.exe'
    $sdkLib = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\Lib\*' -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    $sdkInclude = Join-Path 'C:\Program Files (x86)\Windows Kits\10\Include' $sdkLib.Name
    if (-not (Test-Path $link) -or -not $sdkLib -or -not (Test-Path $sdkInclude)) { return $null }

    $objectDirectory = Join-Path $root 'obj'
    New-Item -ItemType Directory -Force -Path $objectDirectory | Out-Null
    $objectFile = Join-Path $objectDirectory 'AirStereoLauncher.obj'
    $iconFile = Join-Path $objectDirectory 'AirStereo.ico'
    $resourceSource = Join-Path $objectDirectory 'AirStereoLauncher.rc'
    $resourceFile = Join-Path $objectDirectory 'AirStereoLauncher.res'
    $exe = Join-Path $OutputDirectory 'AirStereo.exe'

    $resourceCompiler = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\rc.exe' `
        -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    $hasResource = $false
    if ($resourceCompiler -and (Build-LauncherIcon $iconFile)) {
        $iconResourcePath = $iconFile.Replace('\', '/')
        Set-Content -Path $resourceSource -Encoding ASCII -Value ("1 ICON `"" + $iconResourcePath + "`"")
        & $resourceCompiler.FullName /nologo "/fo$resourceFile" $resourceSource | Out-Null
        if ($LASTEXITCODE -eq 0 -and (Test-Path $resourceFile) -and (Get-Item $resourceFile).Length -gt 0) {
            $hasResource = $true
        }
    }

    # cl.exe and link.exe find their headers and import libraries through INCLUDE and LIB;
    # passing the same paths as /I: and /LIBPATH: arguments loses them when the path
    # contains spaces, which every path here does.
    $previousInclude = $env:INCLUDE
    $previousLib = $env:LIB
    try {
        $env:INCLUDE = @(
            (Join-Path $vcTools 'include'),
            (Join-Path $sdkInclude 'ucrt'),
            (Join-Path $sdkInclude 'shared'),
            (Join-Path $sdkInclude 'um')) -join ';'
        $env:LIB = @(
            (Join-Path $vcTools 'lib\x64'),
            (Join-Path $sdkLib.FullName 'um\x64'),
            (Join-Path $sdkLib.FullName 'ucrt\x64')) -join ';'

        & $cl.FullName /nologo /c /O2 /MT /W3 "/Fo:$objectFile" $source | Out-Null
        if ($LASTEXITCODE -ne 0) { return $null }

        $linkInputs = @($objectFile)
        if ($hasResource) { $linkInputs += $resourceFile }
        & $link /nologo /SUBSYSTEM:WINDOWS /MACHINE:X64 "/OUT:$exe" `
            $linkInputs kernel32.lib user32.lib | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) { return $null }
        return $exe
    }
    finally {
        $env:INCLUDE = $previousInclude
        $env:LIB = $previousLib
    }
}

$exe = Build-Launcher -OutputDirectory $outputDirectory

Write-Host "built $output"
Write-Host "reference framework $($shared.FullName)"
if ($desktop) { Write-Host "desktop framework   $($desktop.FullName)" }
if ($exe) { Write-Host "built $exe" } else { Write-Host "warning: AirStereo.exe was not built (no C toolchain found)" }

if ($Shortcut) {
    $target = if ($exe) { $exe } else { $launcher }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @(
        [Environment]::GetFolderPath('Desktop'),
        (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))) {
        if (-not (Test-Path $folder)) { continue }
        $path = Join-Path $folder 'AirStereo.lnk'
        $link = $shell.CreateShortcut($path)
        $link.TargetPath = $target
        $link.WorkingDirectory = $outputDirectory
        $link.Description = 'AirStereo - stereo aware AirPlay sender'
        $link.Save()
        Write-Host "shortcut $path"
    }
}

if ($Run) { & dotnet $output @($Arguments.Split(' ')) }

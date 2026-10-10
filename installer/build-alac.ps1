[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$msbuild = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$sdk = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Lib') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not (Test-Path $msbuild) -or -not $sdk) { throw 'Current Visual Studio Build Tools and Windows SDK are required.' }
$project = Join-Path $root 'src\native\libalac\LibALAC.vcxproj'
$output = Join-Path $root 'obj\native-alac\bin\'
$objects = Join-Path $root 'obj\native-alac\obj\'
& $msbuild $project /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v145 `
    "/p:WindowsTargetPlatformVersion=$($sdk.Name)" "/p:OutDir=$output" "/p:IntDir=$objects" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'LibALAC x64 compilation failed.' }
Copy-Item -LiteralPath (Join-Path $output 'LibALAC64.dll') -Destination (Join-Path $root 'src\native\LibALAC64.dll') -Force
Write-Host 'Built LibALAC64.dll with initialized magic-cookie capacity and static CRT.'

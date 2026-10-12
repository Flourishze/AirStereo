#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDir='dist',[string]$DotNet='dotnet',[string]$NuGetSource,[string]$NuGetPackageRoot,[switch]$NoRestore)
$ErrorActionPreference='Stop'
$output=if ([IO.Path]::IsPathRooted($OutputDir)) { [IO.Path]::GetFullPath($OutputDir) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDir)) }
& (Join-Path $PSScriptRoot 'winui3/build.ps1') -OutputDir $output -DotNet $DotNet -NuGetSource $NuGetSource -NuGetPackageRoot $NuGetPackageRoot -NoRestore:$NoRestore

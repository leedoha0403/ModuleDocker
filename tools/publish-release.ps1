<#
.SYNOPSIS
    Builds the ModuleDock release package (self-contained, single-file win-x64 exe + bundled sample widgets)
    under dist\release. See SELF_UPDATE_RELEASE_GUIDE.md.

.EXAMPLE
    .\tools\publish-release.ps1
    .\tools\publish-release.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Dora.Widget.Host\Dora.Widget.Host.csproj"
$appName = "ModuleDock"   # = AssemblyName = SelfUpdater.ExeAssetName without ".exe"

if (-not $Version) {
    [xml]$csprojXml = Get-Content $project
    $Version = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "Could not determine version from $project. Pass -Version explicitly." }

$releaseDir = Join-Path $root "dist\release"
$publishDir = Join-Path $releaseDir "$Version\$Runtime"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "Publishing $appName v$Version ($Runtime, self-contained, single-file)..."

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:InformationalVersion=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$zipName = "$appName-v$Version-$Runtime.zip"
$zipPath = Join-Path $releaseDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

# Standalone exe asset (fixed name) for in-app auto-update; the zip carries the exe plus the bundled widgets\ folder.
$exeName = "$appName.exe"
$exePath = Join-Path $releaseDir $exeName
Copy-Item (Join-Path $publishDir $exeName) $exePath -Force

$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
$exeHash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash
$sumsPath = Join-Path $releaseDir "SHA256SUMS.txt"
$existing = @()
if (Test-Path $sumsPath) {
    $existing = Get-Content $sumsPath | Where-Object { $_ -notmatch [regex]::Escape($zipName) -and $_ -notmatch ([regex]::Escape($exeName) + '$') }
}
$existing + "$hash  $zipName" + "$exeHash  $exeName" | Set-Content -Path $sumsPath -Encoding utf8

Write-Host ""
Write-Host "Release package created:"
Write-Host "  $zipPath"
Write-Host "  $exePath"
Write-Host "  SHA256: $hash (zip)  $exeHash (exe)"

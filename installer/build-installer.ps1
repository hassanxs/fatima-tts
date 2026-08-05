<#
.SYNOPSIS
    Builds the Fatima TTS self-contained release and MSI installer.

.DESCRIPTION
    Publishes a self-contained, single-file win-x64 build, then packages it
    into an MSI with WiX v5. Output MSI: dist/FatimaTTS-v<Version>-installer.msi

.PARAMETER Version
    Release version, e.g. 1.1.0. Should match <Version> in FatimaTTS.csproj.

.EXAMPLE
    installer/build-installer.ps1 -Version 1.1.0

.NOTES
    Requirements (one-time):
      dotnet tool install --global wix --version 5.*
      wix extension add -g WixToolset.UI.wixext/5.0.2
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

# Resolve repo root (parent of this script's folder) and move there.
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot
try {
    $PublishDir = Join-Path $RepoRoot 'publish'
    $DistDir    = Join-Path $RepoRoot 'dist'
    $Msi        = Join-Path $DistDir "FatimaTTS-v$Version-installer.msi"

    Write-Host "==> Publishing self-contained build (v$Version)..." -ForegroundColor Cyan
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
    dotnet publish FatimaTTS/FatimaTTS.csproj `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -o $PublishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    # Ship the app, not debug symbols.
    Remove-Item (Join-Path $PublishDir '*.pdb') -Force -ErrorAction SilentlyContinue

    Write-Host "==> Building MSI installer..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
    $IconFile   = Join-Path $RepoRoot 'FatimaTTS/Assets/icon.ico'
    $LicenseRtf = Join-Path $RepoRoot 'installer/License.rtf'
    wix build installer/FatimaTTS.wxs `
        -ext WixToolset.UI.wixext `
        -d "Version=$Version" `
        -d "PublishDir=$PublishDir" `
        -d "IconFile=$IconFile" `
        -d "LicenseRtf=$LicenseRtf" `
        -o $Msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

    $hash = (Get-FileHash $Msi -Algorithm SHA256).Hash
    $sizeMb = [math]::Round((Get-Item $Msi).Length / 1MB, 1)
    Write-Host ""
    Write-Host "==> Done." -ForegroundColor Green
    Write-Host "    MSI:    $Msi ($sizeMb MB)"
    Write-Host "    SHA256: $hash"
}
finally {
    Pop-Location
}

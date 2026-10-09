# Builds a complete release: self-contained publish -> identity package -> Inno Setup installer.
# Output: artifacts\DynamicBay-Setup-<version>.exe
# Usage: powershell -File tools\build-release.ps1 [-Version 1.0.0]
param([string]$Version = "")
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

if (-not $Version) {
    $Version = ([xml](Get-Content src\DynamicBay\DynamicBay.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Output "== DynamicBay $Version =="

$publish = Join-Path $root "artifacts\publish"
$package = Join-Path $root "artifacts\package"
Remove-Item $publish, $package -Recurse -Force -ErrorAction SilentlyContinue

# 1) Self-contained app (includes the .NET runtime; ReadyToRun for faster start)
dotnet publish src\DynamicBay\DynamicBay.csproj -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 2) Identity package for notification access
& "$root\packaging\build-sparse.ps1" -Version $Version -Out $package

# 3) Installer
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/DPackageDir=$package" "installer\DynamicBay.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $root "artifacts\DynamicBay-Setup-$Version.exe"
Write-Output "Setup: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"

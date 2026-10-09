# Builds and signs the identity (sparse) package DynamicBay.msix plus the public certificate DynamicBay.cer.
# The installer trusts the certificate (LocalMachine\TrustedPeople) and the app registers the package on first run.
#
# Usage: powershell -File packaging\build-sparse.ps1 -Version 1.0.0 -Out <dir> [-Pfx <path> -PfxPassword <pw>]
# Without -Pfx a fresh self-signed certificate is generated (fine for open-source builds; see docs/NOTIFICATIONS.md).
param(
    [string]$Version = "1.0.0",
    [string]$Out = "$PSScriptRoot\out",
    [string]$Pfx = "",
    [string]$PfxPassword = "dynamicbay"
)
$ErrorActionPreference = "Stop"
$publisher = "CN=DynamicBay Open Source"

$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $kit) { throw "Windows SDK (makeappx.exe) not found. Install it with: winget install Microsoft.WindowsSDK.10.0.26100" }
$makeappx = $kit.FullName
$signtool = Join-Path $kit.DirectoryName "signtool.exe"

$stage = Join-Path $Out "stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\Assets" | Out-Null

# Manifest with the version (MSIX needs four parts)
$v = ($Version.Split('-')[0] + ".0.0.0").Split('.')[0..3] -join '.'
(Get-Content "$PSScriptRoot\AppxManifest.xml" -Raw).Replace("__VERSION__", $v) | Set-Content "$stage\AppxManifest.xml" -Encoding utf8

# Logos rendered from the app icon
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile((Resolve-Path "$PSScriptRoot\..\src\DynamicBay\Assets\DynamicBay.png"))
foreach ($asset in @(@("StoreLogo.png", 50), @("Square150x150Logo.png", 150), @("Square44x44Logo.png", 44))) {
    $bmp = New-Object System.Drawing.Bitmap $asset[1], $asset[1]
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.InterpolationMode = "HighQualityBicubic"
    $g.DrawImage($src, 0, 0, $asset[1], $asset[1]); $g.Dispose()
    $bmp.Save("$stage\Assets\$($asset[0])", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
$src.Dispose()

$msix = Join-Path $Out "DynamicBay.msix"
& $makeappx pack /d $stage /p $msix /nv /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

if (-not $Pfx) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName "DynamicBay package signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    $Pfx = Join-Path $Out "DynamicBay.pfx"
    Export-PfxCertificate -Cert $cert -FilePath $Pfx -Password (ConvertTo-SecureString $PfxPassword -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath (Join-Path $Out "DynamicBay.cer") | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}
& $signtool sign /fd SHA256 /f $Pfx /p $PfxPassword $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
Remove-Item $stage -Recurse -Force
Write-Output "Built $msix (version $v)"

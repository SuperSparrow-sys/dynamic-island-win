# Creates the fixed certificate that signs the identity package (CN=DynamicBay Open Source) and stores it as the
# GitHub secrets SIGNING_PFX_BASE64 and SIGNING_PFX_PASSWORD, which the release build uses. Run once, by the owner.
# A copy goes to -Out (keep it private and backed up, never commit it). Without the secrets every build signs with
# a new self-signed certificate, and each update adds another one to TrustedPeople.
# Usage: powershell -File tools\new-signing-cert.ps1 [-Out <folder>] [-Repo owner/name]
param(
    [string]$Out = "$env:USERPROFILE\Documents\DynamicBay-signing",
    [string]$Repo = "SuperSparrow-sys/dynamic-island-win"
)
$ErrorActionPreference = "Stop"
if (Test-Path "$Out\DynamicBay-signing.pfx") { throw "$Out\DynamicBay-signing.pfx exists already - reuse it instead of making a new one" }
New-Item -ItemType Directory -Force $Out | Out-Null

$bytes = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes)

$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=DynamicBay Open Source" -KeyUsage DigitalSignature `
    -FriendlyName "DynamicBay package signing" -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(25) `
    -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy Exportable `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
try {
    Export-PfxCertificate -Cert $cert -FilePath "$Out\DynamicBay-signing.pfx" -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath "$Out\DynamicBay.cer" | Out-Null
} finally {
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}
Set-Content "$Out\password.txt" $password -NoNewline

[Convert]::ToBase64String([IO.File]::ReadAllBytes("$Out\DynamicBay-signing.pfx")) | gh secret set SIGNING_PFX_BASE64 -R $Repo
if ($LASTEXITCODE -ne 0) { throw "gh secret set SIGNING_PFX_BASE64 failed" }
$password | gh secret set SIGNING_PFX_PASSWORD -R $Repo
if ($LASTEXITCODE -ne 0) { throw "gh secret set SIGNING_PFX_PASSWORD failed" }

Write-Output "Certificate $($cert.Thumbprint), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Output "Saved to $Out and stored as GitHub secrets in $Repo"

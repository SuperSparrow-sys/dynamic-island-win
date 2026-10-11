# Update smoke test: installs older releases, updates to a new installer the way the in-app updater does
# (/VERYSILENT /UPDATE) and checks after every step that DynamicBay.exe starts and runs with package identity.
# Catches breakage that only the installed app shows (identity package, certificate, restart after update).
#
# Only on a throwaway Windows (CI runner, VM): it installs into Program Files, registers the identity package
# and stops every running DynamicBay. Needs admin rights.
# Usage: powershell -File tools\update-smoke.ps1 -NewInstaller artifacts\DynamicBay-Setup-1.0.18.exe [-OldVersions 1.0.15,1.0.16]
param(
    [Parameter(Mandatory)] [string]$NewInstaller,
    [string[]]$OldVersions = @("1.0.15", "1.0.16"),
    [string]$Repo = "SuperSparrow-sys/dynamic-island-win",
    [string]$Work = "$env:TEMP\dynamicbay-smoke"
)
$ErrorActionPreference = "Stop"
$NewInstaller = (Resolve-Path $NewInstaller).Path
$exe = "$env:ProgramFiles\DynamicBay\DynamicBay.exe"
$log = "$env:APPDATA\DynamicBay\logs\dynamicbay.log"
New-Item -ItemType Directory -Force $Work | Out-Null

function Stop-App {
    Get-Process DynamicBay -ErrorAction SilentlyContinue | Stop-Process -Force
    for ($i = 0; $i -lt 40 -and (Get-Process DynamicBay -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    Start-Sleep -Seconds 2 # let Windows apply pending package operations, as after a real exit
}

function Install([string]$setup, [string]$name, [switch]$Update) {
    $setupArgs = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$Work\setup-$name.log`"")
    if ($Update) { $setupArgs += "/UPDATE" }
    Write-Output "-- install $name$(if ($Update) { ' (update)' })"
    $p = Start-Process $setup -ArgumentList $setupArgs -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "Setup $name failed with exit code $($p.ExitCode)" }
}

function LogLength { if (Test-Path $log) { (Get-Item $log).Length } else { 0 } }

function NewLog([long]$from) {
    if (-not (Test-Path $log)) { return "" }
    $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    try { $fs.Seek($from, 'Begin') | Out-Null; (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
}

# Starts the exe like Explorer would (CreateProcess, so a refused start surfaces as an error code instead of a dialog)
# and waits until this version reports a start with identity. The first start of a fresh install may register the
# package and restart itself once; the setup of an update may have started the app already.
function Assert-Starts([string]$version, [long]$from) {
    Write-Output "-- start $version"
    if (-not (Get-Process DynamicBay -ErrorAction SilentlyContinue)) {
        $psi = New-Object Diagnostics.ProcessStartInfo $exe
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = Split-Path $exe
        try { [Diagnostics.Process]::Start($psi) | Out-Null }
        catch { throw "DynamicBay $version does not start: $($_.Exception.InnerException.NativeErrorCode) $($_.Exception.InnerException.Message)" }
    }
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        $text = NewLog $from
        if ($text -match "DynamicBay $([regex]::Escape($version)) starting \(identity: True\)") {
            Start-Sleep -Seconds 5
            if (-not (Get-Process DynamicBay -ErrorAction SilentlyContinue)) { throw "DynamicBay $version exited right after the start" }
            Write-Output "   ok: $version runs with identity"
            return
        }
        Start-Sleep -Seconds 1
    }
    Write-Output (NewLog $from)
    throw "DynamicBay $version did not report a start with identity within 90 s"
}

# Fresh state: nothing installed, no identity package, no settings
Stop-App
Get-AppxPackage DynamicBay | Remove-AppxPackage -ErrorAction SilentlyContinue
Remove-Item "$env:APPDATA\DynamicBay" -Recurse -Force -ErrorAction SilentlyContinue

$first = $true
foreach ($v in $OldVersions) {
    $setup = "$Work\DynamicBay-Setup-$v.exe"
    if (-not (Test-Path $setup)) {
        gh release download "v$v" -R $Repo -p "DynamicBay-Setup-$v.exe" -D $Work --clobber
        if ($LASTEXITCODE -ne 0) { throw "Download of v$v failed" }
    }
    $from = LogLength
    if ($first) { Install $setup $v } else { Install $setup $v -Update }
    Assert-Starts $v $from
    Stop-App
    $first = $false
}

# Version from the file name (DynamicBay-Setup-1.2.3.exe or -1.2.3-beta.1.exe)
$newVersion = [regex]::Match((Split-Path $NewInstaller -Leaf), '(\d+\.\d+\.\d+)').Groups[1].Value
if (-not $newVersion) { throw "No version in the installer name $NewInstaller" }

$from = LogLength
Install $NewInstaller $newVersion -Update:($OldVersions.Count -gt 0)
Assert-Starts $newVersion $from

$pkg = Get-AppxPackage DynamicBay
Write-Output "-- identity package: $($pkg.PackageFullName)"
if (-not $pkg -or $pkg.Version -ne "$newVersion.0") { throw "Identity package is $($pkg.Version), expected $newVersion.0" }
Stop-App
Write-Output "Update smoke test passed: $($OldVersions -join ' -> ') -> $newVersion"

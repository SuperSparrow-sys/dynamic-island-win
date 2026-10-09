# Captures design screenshots of every island state/orientation (see docs/DEVELOPMENT.md).
# Usage: powershell -File tools\snapshot.ps1 [-Out <dir>] [-Configuration Debug]
param(
    [string]$Out = "$PSScriptRoot\..\artifacts\snapshots",
    [string]$Configuration = "Debug"
)
$exe = Get-ChildItem "$PSScriptRoot\..\src\DynamicBay\bin\$Configuration" -Recurse -Filter DynamicBay.exe | Select-Object -First 1
if (-not $exe) { throw "Build first: dotnet build src\DynamicBay -c $Configuration" }
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$p = Start-Process $exe.FullName -ArgumentList "--snapshot", "`"$Out`"" -PassThru
if (-not $p.WaitForExit(180000)) { $p.Kill(); throw "snapshot run timed out" }
Get-ChildItem $Out -Name

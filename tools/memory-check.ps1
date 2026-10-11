# Memory check (see docs/performance/README.md): runs the snapshot leak mode several times and prints the medians.
# Usage: powershell -File tools\memory-check.ps1 [-Runs 3] [-Rounds 6] [-Configuration Release] [-Out <dir>]
param(
    [int]$Runs = 3,
    [int]$Rounds = 6,
    [string]$Configuration = "Release",
    [string]$Out = "$PSScriptRoot\..\artifacts\memory"
)
$exe = Get-ChildItem "$PSScriptRoot\..\src\DynamicBay\bin\$Configuration" -Recurse -Filter DynamicBay.exe | Select-Object -First 1
if (-not $exe) { throw "Build first: dotnet build src\DynamicBay -c $Configuration" }
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$env:DYNAMICBAY_SNAPSHOT = "leak"
$env:DYNAMICBAY_LEAK_ROUNDS = "$Rounds"
$results = @()
for ($i = 1; $i -le $Runs; $i++) {
    $dir = Join-Path $Out "run-$i"
    $p = Start-Process $exe.FullName -ArgumentList "--snapshot", "`"$dir`"" -PassThru
    if (-not $p.WaitForExit(600000)) { $p.Kill(); throw "leak run $i timed out" }
    $json = Join-Path $dir "memory.json"
    if (-not (Test-Path $json)) { throw "run $i wrote no memory.json (see the log)" }
    $r = Get-Content $json -Raw | ConvertFrom-Json
    $results += $r
    "run {0}: managed {1} -> {2} MB, working set {3} -> {4} MB, alive {5}" -f $i, $r.baseline.ManagedMb, $r.final.ManagedMb,
        $r.baseline.WorkingSetMb, $r.final.WorkingSetMb, ($r.aliveAfterGc | ConvertTo-Json -Compress)
}
function Median($values) { $s = @($values | Sort-Object); $s[[int][Math]::Floor($s.Count / 2)] }
$summary = [ordered]@{
    runs = $Runs
    rounds = $Rounds
    baselineManagedMb = Median ($results | ForEach-Object { $_.baseline.ManagedMb })
    finalManagedMb = Median ($results | ForEach-Object { $_.final.ManagedMb })
    baselineWorkingSetMb = Median ($results | ForEach-Object { $_.baseline.WorkingSetMb })
    finalWorkingSetMb = Median ($results | ForEach-Object { $_.final.WorkingSetMb })
    finalPrivateMb = Median ($results | ForEach-Object { $_.final.PrivateMb })
    managedGrowthMb = Median ($results | ForEach-Object { $_.managedGrowthMb })
    workingSetGrowthMb = Median ($results | ForEach-Object { $_.workingSetGrowthMb })
}
$summary | ConvertTo-Json | Set-Content (Join-Path $Out "summary.json") -Encoding utf8
[pscustomobject]$summary | Format-List

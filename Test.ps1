# Runs every LAICA check. Build first:  ./Build.ps1 -Development
param([string]$NodePath)
$ErrorActionPreference = 'Stop'
if (!$NodePath) { $NodePath = (Get-Command node.exe -ErrorAction Stop).Source }
$failed = @()
foreach ($name in 'BackendTests', 'HarnessTests', 'ChannelTests', 'RemoteTests', 'ObserverWorkTests') {
    $exe = Join-Path $PSScriptRoot "checks/$name.exe"
    if (!(Test-Path $exe)) { throw "$name.exe is missing. Run ./Build.ps1 -Development first." }
    Write-Host "== $name"
    Push-Location $PSScriptRoot; try { & $exe; if ($LASTEXITCODE -ne 0) { $failed += $name } } finally { Pop-Location }
}
Write-Host '== frontend tests'
Push-Location (Join-Path $PSScriptRoot 'frontend')
try { $tests = @(Get-ChildItem tests -Filter *.test.mjs | ForEach-Object { "tests/$($_.Name)" }); & $NodePath --test @tests; if ($LASTEXITCODE -ne 0) { $failed += 'frontend' } } finally { Pop-Location }
Write-Host '== signing safeguards'
& (Join-Path $PSScriptRoot 'checks/SigningTests.ps1')
Write-Host '== installer'
& (Join-Path $PSScriptRoot 'checks/SetupTests.ps1')
if ($failed.Count) { throw ('Failed: ' + ($failed -join ', ')) }
Write-Output 'All LAICA checks passed.'
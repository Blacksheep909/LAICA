# Packages the current build, installs it silently into a temporary folder, checks the files, upgrades over it (user data must survive) and uninstalls.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'tools/Package.ps1') | Out-Null
$version = ([IO.File]::ReadAllText((Join-Path $root 'VERSION'))).Trim()
$setup = Join-Path $root "dist/LAICA-Setup-$version.exe"
$target = Join-Path ([IO.Path]::GetTempPath()) ('laica-setup-test-' + [guid]::NewGuid().ToString('N'))
try {
    $p = Start-Process $setup -ArgumentList '/S', '/NOINTEGRATION', "/D=$target" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Setup exited with $($p.ExitCode)." }
    foreach ($f in 'LAICA.exe', 'LAICA.Bridge.exe', 'ui/index.html', 'Uninstall.exe', 'release.json') { if (!(Test-Path (Join-Path $target $f))) { throw "Missing after install: $f" } }
    if ((Get-Item (Join-Path $target 'LAICA.exe')).VersionInfo.ProductVersion -ne $version) { throw 'Installed version does not match.' }
    New-Item -ItemType Directory (Join-Path $target 'harness') | Out-Null
    Set-Content (Join-Path $target 'harness/keep.json') '{}'
    $p = Start-Process $setup -ArgumentList '/S', '/NOINTEGRATION', "/D=$target" -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path (Join-Path $target 'harness/keep.json'))) { throw 'Upgrading must keep user data.' }
    $p = Start-Process (Join-Path $target 'Uninstall.exe') -ArgumentList '/uninstall', '/S', '/NOINTEGRATION', "/D=$target" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Uninstall exited with $($p.ExitCode)." }
    if (Test-Path (Join-Path $target 'LAICA.exe')) { throw 'Uninstall left the program behind.' }
    if (!(Test-Path (Join-Path $target 'harness/keep.json'))) { throw 'Uninstall removed user data.' }
    Write-Output 'Setup checks passed.'
}
finally {
    Start-Sleep -Seconds 4
    if (Test-Path $target) { Get-ChildItem $target -Recurse -Force -ErrorAction SilentlyContinue | Out-Null; cmd /c rd /s /q "\\?\$target" }
}

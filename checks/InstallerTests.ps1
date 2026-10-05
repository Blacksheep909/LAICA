$ErrorActionPreference = 'Stop'

$publicRoot = Split-Path $PSScriptRoot -Parent
$buildRoot = Join-Path $publicRoot 'build'
$installScript = Join-Path $publicRoot 'Install.ps1'
$signingScript = Join-Path $publicRoot 'Signing.ps1'
foreach ($required in @($buildRoot,$installScript,$signingScript)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Installer test prerequisite is missing: $required" }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('laica-installer-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $testRoot 'package'
$targetRoot = Join-Path $testRoot 'target'
$stateRoot = Join-Path $testRoot 'state'
New-Item -ItemType Directory -Path $testRoot | Out-Null
$originalLocalAppData = $env:LOCALAPPDATA
try {
    New-Item -ItemType Directory -Path $packageRoot,$targetRoot,$stateRoot | Out-Null
    Copy-Item -LiteralPath $buildRoot -Destination (Join-Path $packageRoot 'build') -Recurse
    Copy-Item -LiteralPath $installScript,$signingScript -Destination $packageRoot
    $fixtureInstaller = Join-Path $packageRoot 'Install.ps1'
    $payloadRoot = Join-Path $packageRoot 'build'
    $release = Get-Content -LiteralPath (Join-Path $payloadRoot 'release.json') -Raw | ConvertFrom-Json
    $target = Join-Path $targetRoot 'LAICA'
    $env:LOCALAPPDATA = $stateRoot

    function Get-ManagedSnapshot {
        param([string]$Root)
        $snapshot = [ordered]@{}
        if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return $snapshot }
        foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName) {
            $relative = $file.FullName.Substring($Root.Length + 1).Replace('\','/')
            $snapshot[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
        return $snapshot
    }

    function Assert-SnapshotEqual {
        param($Expected,$Actual,[string]$Message)
        $expectedJson = ConvertTo-Json -InputObject $Expected -Depth 10 -Compress
        $actualJson = ConvertTo-Json -InputObject $Actual -Depth 10 -Compress
        if ($expectedJson -cne $actualJson) { throw "Installer test failed: $Message" }
    }

    function Invoke-FixtureInstall {
        param([switch]$Development)
        $arguments = @{ InstallDirectory = $target; NoShortcuts = $true }
        if ($Development) { $arguments.Development = $true }
        & $fixtureInstaller @arguments
    }

    function Get-InstallMarker {
        $markerPath = Join-Path $target '.laica-install.json'
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw 'Installer did not create .laica-install.json.' }
        return @{ Path = $markerPath; Data = (Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json) }
    }

    function Assert-InstalledManifest {
        $marker = Get-InstallMarker
        if ($marker.Data.Signed -ne $false) { throw 'Development install marker must say Signed=false.' }
        $expected = @($release.Files) + @(@{ Path = 'release.json'; Sha256 = (Get-FileHash -LiteralPath (Join-Path $payloadRoot 'release.json') -Algorithm SHA256).Hash })
        $actual = @($marker.Data.Files)
        if ($actual.Count -ne $expected.Count) { throw "Install marker lists $($actual.Count) files; expected $($expected.Count)." }
        foreach ($entry in $expected) {
            $relative = ([string]$entry.Path).Replace('\','/')
            $record = @($actual | Where-Object { ([string]$_.Path).Replace('\','/') -ceq $relative })
            if ($record.Count -ne 1) { throw "Install marker must contain exactly one entry for $relative." }
            $installedFile = Join-Path $target ($relative.Replace('/','\'))
            if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf)) { throw "Installed file is missing: $relative" }
            $hash = (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash
            if ($hash -cne [string]$entry.Sha256 -or $hash -cne [string]$record[0].Sha256) { throw "Installed hash mismatch: $relative" }
        }
        return $marker
    }

    Invoke-FixtureInstall -Development
    $firstMarker = Assert-InstalledManifest
    $retiredPath = Join-Path $target 'old-version.txt'
    [IO.File]::WriteAllText($retiredPath,'managed by prior LAICA version',[Text.Encoding]::UTF8)
    $retiredHash = (Get-FileHash -LiteralPath $retiredPath -Algorithm SHA256).Hash
    $markerData = Get-Content -LiteralPath $firstMarker.Path -Raw | ConvertFrom-Json
    $markerData.Files = @($markerData.Files) + @(@{ Path = 'old-version.txt'; Sha256 = $retiredHash })
    $markerData | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $firstMarker.Path -Encoding UTF8
    $notesPath = Join-Path $target 'notes.txt'
    [IO.File]::WriteAllText($notesPath,'user-owned notes',[Text.Encoding]::UTF8)
    $backupDirectory = Join-Path $stateRoot 'LAICA/backups'
    $backupsBefore = if (Test-Path -LiteralPath $backupDirectory) { @(Get-ChildItem -LiteralPath $backupDirectory -Filter '*.zip' -File) } else { @() }
    Invoke-FixtureInstall -Development
    if (Test-Path -LiteralPath $retiredPath) { throw 'Upgrade retained a retired file listed by the previous install marker.' }
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf) -or [IO.File]::ReadAllText($notesPath) -ne 'user-owned notes') { throw 'Upgrade did not preserve the unmanaged notes.txt file.' }
    $upgradeMarker = Assert-InstalledManifest
    if (@($upgradeMarker.Data.Files | Where-Object { $_.Path -ceq 'old-version.txt' }).Count) { throw 'Upgrade marker still claims ownership of the retired file.' }
    $backupsAfter = @(Get-ChildItem -LiteralPath $backupDirectory -Filter '*.zip' -File -ErrorAction SilentlyContinue)
    $oldBackupPaths = @($backupsBefore | ForEach-Object { $_.FullName })
    $newBackups = @($backupsAfter | Where-Object { $oldBackupPaths -notcontains $_.FullName })
    if (-not $newBackups.Count) { throw 'Upgrade did not create a program backup zip.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($backup in $newBackups) {
        $null = Get-FileHash -LiteralPath $backup.FullName -Algorithm SHA256
        $archive = [IO.Compression.ZipFile]::OpenRead($backup.FullName)
        try {
            $retiredEntry = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)old-version\.txt$' })
            if ($retiredEntry.Count -ne 1) { throw 'Upgrade backup does not contain exactly one copy of the prior managed file.' }
            $stream = $retiredEntry[0].Open()
            try {
                $sha256 = [Security.Cryptography.SHA256]::Create()
                try {
                    $archivedHash = ([BitConverter]::ToString($sha256.ComputeHash($stream)) -replace '-','')
                    if ($archivedHash -cne $retiredHash) { throw 'Upgrade backup has corrupt prior managed-file content.' }
                }
                finally { $sha256.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        finally { $archive.Dispose() }
    }

    $installedBaseline = Get-ManagedSnapshot -Root $target

    # Force a mid-install collision after an earlier new file has been copied.
    $fixtureReleasePath = Join-Path $payloadRoot 'release.json'
    $originalReleaseBytes = [IO.File]::ReadAllBytes($fixtureReleasePath)
    $rollbackNewFile = Join-Path $payloadRoot 'new-file.txt'
    $rollbackNotesSource = Join-Path $payloadRoot 'notes.txt'
    try {
        [IO.File]::WriteAllText($rollbackNewFile,'created before collision',[Text.Encoding]::UTF8)
        [IO.File]::WriteAllText($rollbackNotesSource,'release file that collides with user notes',[Text.Encoding]::UTF8)
        $rollbackManifest = [IO.File]::ReadAllText($fixtureReleasePath) | ConvertFrom-Json
        $newFileEntry = @{ Path = 'new-file.txt'; Sha256 = (Get-FileHash -LiteralPath $rollbackNewFile -Algorithm SHA256).Hash }
        $notesEntry = @{ Path = 'notes.txt'; Sha256 = (Get-FileHash -LiteralPath $rollbackNotesSource -Algorithm SHA256).Hash }
        $rollbackManifest.Files = @($newFileEntry) + @($rollbackManifest.Files) + @($notesEntry)
        $rollbackManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $fixtureReleasePath -Encoding UTF8

        $rejected = $false
        try { Invoke-FixtureInstall -Development } catch { $rejected = $true }
        if (-not $rejected) { throw 'Installer accepted a release file colliding with unmanaged notes.txt.' }
        Assert-SnapshotEqual $installedBaseline (Get-ManagedSnapshot -Root $target) 'mid-install collision rollback changed installed hashes'
        if (Test-Path -LiteralPath (Join-Path $target 'new-file.txt')) { throw 'Mid-install rollback left the newly created file behind.' }
        if ([IO.File]::ReadAllText($notesPath) -ne 'user-owned notes') { throw 'Mid-install rollback changed unmanaged notes.txt.' }
    }
    finally {
        [IO.File]::WriteAllBytes($fixtureReleasePath,$originalReleaseBytes)
        Remove-Item -LiteralPath $rollbackNewFile,$rollbackNotesSource -Force -ErrorAction SilentlyContinue
    }

    $extraSource = Join-Path $payloadRoot 'unlisted-extra.txt'
    [IO.File]::WriteAllText($extraSource,'not in release manifest',[Text.Encoding]::UTF8)
    try {
        $rejected = $false
        try { Invoke-FixtureInstall -Development } catch { $rejected = $true }
        if (-not $rejected) { throw 'Installer accepted an unlisted source file.' }
        Assert-SnapshotEqual $installedBaseline (Get-ManagedSnapshot -Root $target) 'unlisted source rejection changed installed hashes'
    }
    finally { Remove-Item -LiteralPath $extraSource -Force }

    $tamperedPath = Join-Path $payloadRoot ([string]$release.Files[0].Path).Replace('/','\')
    $originalPayloadBytes = [IO.File]::ReadAllBytes($tamperedPath)
    try {
        [IO.File]::AppendAllText($tamperedPath,'tampered')
        $rejected = $false
        try { Invoke-FixtureInstall -Development } catch { $rejected = $true }
        if (-not $rejected) { throw 'Installer accepted a tampered manifested source file.' }
        Assert-SnapshotEqual $installedBaseline (Get-ManagedSnapshot -Root $target) 'tampered source rejection changed installed hashes'
    }
    finally { [IO.File]::WriteAllBytes($tamperedPath,$originalPayloadBytes) }

    $beforeUnsignedAttempt = Get-ManagedSnapshot -Root $target
    $rejected = $false
    try { Invoke-FixtureInstall } catch { $rejected = $true }
    if (-not $rejected) { throw 'Default install accepted an unsigned development payload.' }
    Assert-SnapshotEqual $beforeUnsignedAttempt (Get-ManagedSnapshot -Root $target) 'unsigned default rejection changed installed hashes'

    Write-Output 'InstallerTests passed: development install hashes and marker, managed retirement, unmanaged preservation, verified backup, rollback after a mid-install collision, manifest rejection without mutation, tamper rejection without mutation, and unsigned default rejection.'
}
finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot).TrimEnd('\')
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolvedTestRoot.StartsWith($resolvedTemp,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected installer-test cleanup path.' }
        if ([IO.Path]::GetFileName($resolvedTestRoot) -notlike 'laica-installer-*') { throw 'Installer-test cleanup root does not match its fixture name.' }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

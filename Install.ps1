param(
    [switch]$Development,
    [string]$InstallDirectory = (Join-Path (Join-Path $env:LOCALAPPDATA 'Programs') 'LAICA'),
    [switch]$NoShortcuts
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

function Get-LaicaFullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Test-LaicaPathWithin([string]$Path, [string]$Root) {
    $fullPath = Get-LaicaFullPath $Path
    $fullRoot = Get-LaicaFullPath $Root
    return $fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-LaicaNoReparse([string]$Path, [switch]$Recursive) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked or reparse path is not allowed: $Path" }
    if ($Recursive -and $item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked or reparse path is not allowed: $($child.FullName)" }
        }
    }
}

function Assert-LaicaSafeRelativePath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.Contains('\') -or
        $RelativePath.StartsWith('/') -or $RelativePath -match '^[A-Za-z]:' -or
        $RelativePath -match '(^|/)\.($|/)|(^|/)\.\.($|/)' -or $RelativePath.Contains(':')) {
        throw "Unsafe release path: '$RelativePath'"
    }
    foreach ($part in $RelativePath.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)') {
            throw "Unsafe release path component in '$RelativePath'"
        }
    }
}

function Get-LaicaSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToUpperInvariant()
}

function Get-LaicaTreeFiles([string]$Root) {
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName.Substring((Get-LaicaFullPath $Root).Length + 1).Replace('\','/'); FullName = $_.FullName }
    })
}

function Save-LaicaAtomicText([string]$Path, [string]$Text) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, $Text, (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporary, $Path, [NullString]::Value, $true)
        } else {
            [IO.File]::Move($temporary, $Path)
        }
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function New-LaicaVerifiedBackup([string]$Target, [string]$BackupPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($BackupPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in Get-LaicaTreeFiles $Target) {
                $entry = $archive.CreateEntry($file.Path, [IO.Compression.CompressionLevel]::Optimal)
                $input = [IO.File]::OpenRead($file.FullName)
                try { $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }

    $archive = [IO.Compression.ZipFile]::OpenRead($BackupPath)
    try {
        $original = @{}
        foreach ($file in Get-LaicaTreeFiles $Target) { $original[$file.Path.ToLowerInvariant()] = (Get-LaicaSha256 $file.FullName) }
        if ($archive.Entries.Count -ne $original.Count) { throw 'Backup archive inventory did not match the target.' }
        foreach ($entry in $archive.Entries) {
            Assert-LaicaSafeRelativePath $entry.FullName
            $key = $entry.FullName.ToLowerInvariant()
            if (-not $original.ContainsKey($key)) { throw "Unexpected file in backup archive: $($entry.FullName)" }
            $verifyStream = $entry.Open()
            try { $hash2 = [Security.Cryptography.SHA256]::Create(); try { $actual = ([BitConverter]::ToString($hash2.ComputeHash($verifyStream)) -replace '-','') } finally { $hash2.Dispose() } } finally { $verifyStream.Dispose() }
            if ($actual -ne $original[$key]) { throw "Backup archive hash mismatch for '$($entry.FullName)'." }
        }
    } finally { $archive.Dispose() }
}

function Restore-LaicaBackup([string]$Target, [string]$BackupPath) {
    $archive = [IO.Compression.ZipFile]::OpenRead($BackupPath)
    try {
        foreach ($entry in $archive.Entries) {
            Assert-LaicaSafeRelativePath $entry.FullName
            $destination = Join-Path $Target ($entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar))
            if (-not (Test-LaicaPathWithin $destination $Target) -and (Get-LaicaFullPath $destination) -ne (Get-LaicaFullPath $Target)) { throw 'Backup restore path escaped the target.' }
            $entryStream = $entry.Open()
            try {
                $hasher = [Security.Cryptography.SHA256]::Create()
                try { $archivedHash = ([BitConverter]::ToString($hasher.ComputeHash($entryStream)) -replace '-','') } finally { $hasher.Dispose() }
            } finally { $entryStream.Dispose() }
            if ((Test-Path -LiteralPath $destination -PathType Leaf) -and (Get-LaicaSha256 $destination) -eq $archivedHash) { continue }
            $parent = Split-Path -Parent $destination
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
            $input = $entry.Open()
            try { $output = [IO.File]::Open($destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None); try { $input.CopyTo($output) } finally { $output.Dispose() } } finally { $input.Dispose() }
        }
    } finally { $archive.Dispose() }
}

$source = Get-LaicaFullPath (Join-Path $PSScriptRoot 'build')
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw 'Build output was not found. Run Build.ps1 first.' }
$target = Get-LaicaFullPath $InstallDirectory
if ([IO.Path]::GetFileName($target) -ine 'LAICA') { throw 'InstallDirectory must name a dedicated LAICA directory.' }
if ((Get-LaicaFullPath $source) -eq $target -or (Test-LaicaPathWithin $source $target) -or (Test-LaicaPathWithin $target $source)) { throw 'The installation target must be separate from the source build directory.' }

Assert-LaicaNoReparse $source -Recursive
$manifestPath = Join-Path $source 'release.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Build release.json is missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not $manifest.BuildComplete -or -not $manifest.Version -or $null -eq $manifest.Files) { throw 'Release metadata is incomplete.' }
if (-not $Development -and -not $manifest.ReleaseSigned) { throw 'Unsigned builds require the explicit -Development switch.' }

$releaseEntries = @{}
foreach ($entry in @($manifest.Files)) {
    Assert-LaicaSafeRelativePath ([string]$entry.Path)
    if ([string]$entry.Path -ieq 'release.json' -or [string]$entry.Path -ieq '.laica-install.json') { throw "Reserved release path: $($entry.Path)" }
    if ([string]$entry.Sha256 -cnotmatch '^[0-9A-F]{64}$') { throw "Invalid uppercase SHA-256 for '$($entry.Path)'." }
    $key = ([string]$entry.Path).ToLowerInvariant()
    if ($releaseEntries.ContainsKey($key)) { throw "Duplicate release path: $($entry.Path)" }
    $releaseEntries[$key] = [pscustomobject]@{ Path = [string]$entry.Path; Sha256 = [string]$entry.Sha256 }
    $file = Join-Path $source ([string]$entry.Path.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or -not (Test-LaicaPathWithin $file $source)) { throw "Release file is missing or outside build output: $($entry.Path)" }
    if ((Get-LaicaSha256 $file) -cne $entry.Sha256) { throw "Release hash mismatch for '$($entry.Path)'." }
}
$sourceFiles = Get-LaicaTreeFiles $source
$sourceSet = @{}
foreach ($file in $sourceFiles) { $sourceSet[$file.Path.ToLowerInvariant()] = $true }
if ($sourceSet.Count -ne ($releaseEntries.Count + 1) -or -not $sourceSet.ContainsKey('release.json')) { throw 'Build output contains files outside the declared release inventory.' }
foreach ($key in $releaseEntries.Keys) { if (-not $sourceSet.ContainsKey($key)) { throw "Declared release file is absent: $($releaseEntries[$key].Path)" } }

if (-not $Development) {
    . (Join-Path $PSScriptRoot 'Signing.ps1')
    $signatureFiles = @('LAICA.exe','LAICA.Bridge.exe') | ForEach-Object { Join-Path $source $_ }
    $null = Assert-LaicaReleaseSignature -Path $signatureFiles
}

$targetParent = Split-Path -Parent $target
$probe = $targetParent
while ($probe) {
    if (Test-Path -LiteralPath $probe) { Assert-LaicaNoReparse $probe }
    $parent = Split-Path -Parent $probe
    if ($parent -eq $probe) { break }
    $probe = $parent
}
if (Test-Path -LiteralPath $target) {
    Assert-LaicaNoReparse $target -Recursive
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and (Test-LaicaPathWithin $_.Path $target) -and $_.ProcessName -in @('LAICA','LAICA.Bridge')
    })
    if ($running.Count) { throw 'Close LAICA and its bridge before updating this installation.' }
}

$markerPath = Join-Path $target '.laica-install.json'
$previous = $null
$targetFiles = @(if (Test-Path -LiteralPath $target) { Get-LaicaTreeFiles $target })
if ($targetFiles.Count -gt 0) {
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw 'The nonempty target is not a managed LAICA installation; refusing to update it.' }
    $previous = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
    if ($null -eq $previous.Files) { throw 'The previous installation marker has no managed file inventory.' }
}
$oldManaged = @{}
if ($previous) {
    foreach ($entry in @($previous.Files)) {
        Assert-LaicaSafeRelativePath ([string]$entry.Path)
        if ([string]$entry.Path -ieq '.laica-install.json' -or [string]$entry.Sha256 -cnotmatch '^[0-9A-F]{64}$') { throw 'The previous installation marker is invalid.' }
        $key = ([string]$entry.Path).ToLowerInvariant()
        if ($oldManaged.ContainsKey($key)) { throw 'The previous installation marker contains duplicate paths.' }
        $oldManaged[$key] = [pscustomobject]@{ Path = [string]$entry.Path; Sha256 = [string]$entry.Sha256 }
    }
}
$newKeys = @{}; foreach ($key in $releaseEntries.Keys) { $newKeys[$key] = $true }; $newKeys['release.json'] = $true
$obsolete = @($oldManaged.Keys | Where-Object { -not $newKeys.ContainsKey($_) })
foreach ($key in $obsolete) {
    $path = Join-Path $target ($oldManaged[$key].Path.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        if ((Get-LaicaSha256 $path) -cne $oldManaged[$key].Sha256) { throw "Managed obsolete file was modified; refusing to delete '$($oldManaged[$key].Path)'." }
    } elseif (Test-Path -LiteralPath $path) { throw "Managed obsolete path is not a file: $($oldManaged[$key].Path)" }
}

$backupPath = $null
$backups = Join-Path $env:LOCALAPPDATA 'LAICA/backups'
$targetExisted = Test-Path -LiteralPath $target
if ($targetExisted -and $targetFiles.Count -gt 0) {
    New-Item -ItemType Directory -Force -Path $backups | Out-Null
    Assert-LaicaNoReparse (Get-LaicaFullPath $backups)
    $backupPath = Join-Path $backups ((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N') + '.zip')
    New-LaicaVerifiedBackup $target $backupPath
}

$createdFiles = New-Object 'System.Collections.Generic.List[string]'
try {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($entry in @($manifest.Files)) {
        $relative = [string]$entry.Path
        $sourceFile = Join-Path $source ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        $destination = Join-Path $target ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if ((Test-Path -LiteralPath $destination) -and -not $oldManaged.ContainsKey($relative.ToLowerInvariant())) {
            throw "An unmanaged file occupies the release path '$relative'; refusing to overwrite it."
        }
        $parent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { $createdFiles.Add($destination) }
        Copy-Item -LiteralPath $sourceFile -Destination $destination -Force
        if ((Get-LaicaSha256 $destination) -cne [string]$entry.Sha256) { throw "Installed file hash mismatch for '$relative'." }
    }
    $releaseDestination = Join-Path $target 'release.json'
    if ((Test-Path -LiteralPath $releaseDestination) -and -not $oldManaged.ContainsKey('release.json')) {
        throw "An unmanaged file occupies the release path 'release.json'; refusing to overwrite it."
    }
    if (-not (Test-Path -LiteralPath $releaseDestination -PathType Leaf)) { $createdFiles.Add($releaseDestination) }
    Copy-Item -LiteralPath $manifestPath -Destination $releaseDestination -Force
    if ((Get-LaicaSha256 $releaseDestination) -cne (Get-LaicaSha256 $manifestPath)) { throw 'Installed release.json hash mismatch.' }
    foreach ($key in $obsolete) {
        $path = Join-Path $target ($oldManaged[$key].Path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    $managedFiles = @($manifest.Files | ForEach-Object { [pscustomobject]@{ Path = [string]$_.Path; Sha256 = [string]$_.Sha256 } })
    $managedFiles += [pscustomobject]@{ Path = 'release.json'; Sha256 = (Get-LaicaSha256 $releaseDestination) }
    $newMarker = [ordered]@{ Version = [string]$manifest.Version; Signed = [bool]$manifest.ReleaseSigned; Files = $managedFiles }
    Save-LaicaAtomicText $markerPath ($newMarker | ConvertTo-Json -Depth 5)
} catch {
    $failure = $_
    try {
        if ($backupPath) { Restore-LaicaBackup $target $backupPath }
        foreach ($file in $createdFiles) { if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force } }
        if (-not $targetExisted -and (Test-Path -LiteralPath $target)) {
            $remaining = @(Get-LaicaTreeFiles $target)
            if ($remaining.Count -eq 0) { Remove-Item -LiteralPath $target -Force }
        }
    } catch { throw "Installation failed: $($failure.Exception.Message) Rollback also failed: $($_.Exception.Message) Backup: $backupPath" }
    throw $failure
}

if (-not $NoShortcuts) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        foreach ($shortcutPath in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'LAICA.lnk'), (Join-Path ([Environment]::GetFolderPath('Programs')) 'LAICA.lnk'))) {
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = Join-Path $target 'LAICA.exe'
            $shortcut.WorkingDirectory = $target
            $shortcut.IconLocation = (Join-Path $target 'LAICA.exe') + ',0'
            $shortcut.Save()
        }
    } catch { Write-Warning "LAICA was installed, but shortcuts could not be created: $($_.Exception.Message)" }
}
Write-Output "Installed LAICA $($manifest.Version) to $target"

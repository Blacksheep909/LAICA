# Packages build/ into dist/LAICA-<version>-win-x64.zip and a self-extracting dist/LAICA-Setup-<version>.exe. Run Build.ps1 first.
# The output is unsigned unless you sign it yourself.
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$Version) { $Version = ([IO.File]::ReadAllText((Join-Path $root 'VERSION'))).Trim() }
$build = Join-Path $root 'build'
if (!(Test-Path (Join-Path $build 'LAICA.exe'))) { throw 'Run Build.ps1 first.' }
$dist = Join-Path $root 'dist'; New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "LAICA-$Version-win-x64.zip"; if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$files = Get-ChildItem -LiteralPath $build -Recurse -File | Where-Object { $_.Extension -ne '.pdb' }
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try { foreach ($f in $files) { $rel = $f.FullName.Substring($build.Length + 1).Replace('\', '/'); $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal) } } finally { $archive.Dispose() }
$obj = Join-Path $root 'obj'; New-Item -ItemType Directory -Force $obj | Out-Null
$vf = Join-Path $obj 'SetupVersion.cs'
[IO.File]::WriteAllText($vf, "using System.Reflection;[assembly:AssemblyVersion(`"$Version.0`")][assembly:AssemblyTitle(`"LAICA Setup`")][assembly:AssemblyProduct(`"LAICA`")]")
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$setup = Join-Path $dist "LAICA-Setup-$Version.exe"
& $csc /nologo /target:winexe /optimize+ /warnaserror+ "/out:$setup" "/win32icon:$(Join-Path $root 'core/LAICA.ico')" "/resource:$zip,payload.zip" /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Core.dll (Join-Path $root 'tools/Setup.cs') $vf
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
Get-FileHash $zip, $setup -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLower())  $(Split-Path $_.Path -Leaf)" } | Set-Content (Join-Path $dist "SHA256SUMS-$Version.txt") -Encoding ascii
Get-ChildItem $dist | Select-Object Name, Length

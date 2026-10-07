param([switch]$Development,[string]$NodePath,[string]$PnpmPath,[string]$CertificateThumbprint,[ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStore='CurrentUser',[string]$TimestampServer='http://timestamp.digicert.com',[string]$SigntoolPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Signing.ps1')
if(!$Development -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)){throw 'Release signing requires an installed trusted code-signing certificate. Supply -CertificateThumbprint; see docs/releases.md.'}
if(!$Development){$null=Get-LaicaSigningCertificate -Thumbprint $CertificateThumbprint -CertificateStore $CertificateStore}
if(!$NodePath){$NodePath=(Get-Command node.exe -ErrorAction Stop).Source}
$glassNode=$NodePath
$glassFrontend=Join-Path $PSScriptRoot 'frontend'
if(!(Test-Path -LiteralPath (Join-Path $glassFrontend 'node_modules/typescript/bin/tsc'))){
    if(!$PnpmPath){$PnpmPath=(Get-Command pnpm.cmd -ErrorAction Stop).Source}
    function Invoke-GlassPnpm{param([string[]]$A);if($PnpmPath -match '\.(cjs|js)$'){& $glassNode $PnpmPath @A}else{& $PnpmPath @A}}
    Push-Location $glassFrontend
    try{Invoke-GlassPnpm @('install','--ignore-workspace','--ignore-scripts','--frozen-lockfile');if($LASTEXITCODE -ne 0){throw 'Pinned frontend dependency restore failed.'}}finally{Pop-Location}
}
$glassVersion=([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'VERSION'))).Trim()
if($glassVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'VERSION must be MAJOR.MINOR.PATCH.'}
$glassObj=Join-Path $PSScriptRoot 'obj';New-Item -ItemType Directory -Force $glassObj|Out-Null
$glassVersionFile=Join-Path $glassObj 'Version.cs'
[IO.File]::WriteAllText($glassVersionFile,"using System.Reflection;[assembly:AssemblyFileVersion(`"$glassVersion.0`")][assembly:AssemblyInformationalVersion(`"$glassVersion`")]namespace Laica{public static class AppVersion{public const string Value=`"$glassVersion`";}}")
$glassPackage=Join-Path $PSScriptRoot 'frontend/package.json';[IO.File]::WriteAllText($glassPackage,([regex]::Replace([IO.File]::ReadAllText($glassPackage),'(?m)^(\s*"version":\s*")[^"]+','${1}'+$glassVersion)))
$glassOutput=Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force $glassOutput | Out-Null
@{Version=$glassVersion;ReleaseSigned=$false;BuildComplete=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $glassOutput 'release.json')
$glassSdk=Join-Path $PSScriptRoot 'vendor/webview2'
$glassCore=Join-Path $PSScriptRoot 'core'
$glassCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$glassSources=@('Laica.Runtime.cs','Laica.Services.cs','Laica.Profiles.cs','Laica.Observer.cs') | ForEach-Object { Join-Path $glassCore $_ }
$glassRefs=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Core.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Xml.Linq.dll','/r:System.Xml.dll',"/r:$env:WINDIR/Microsoft.NET/assembly/GAC_MSIL/System.Speech/v4.0_4.0.0.0__31bf3856ad364e35/System.Speech.dll","/r:$(Join-Path $glassSdk 'lib/net462/Microsoft.Web.WebView2.Core.dll')","/r:$(Join-Path $glassSdk 'lib/net462/Microsoft.Web.WebView2.WinForms.dll')")
& $glassCompiler /nologo /target:winexe /platform:x64 /main:Laica.GlassWorkspace.Program /optimize+ /warnaserror+ "/out:$(Join-Path $glassOutput 'LAICA.exe')" "/win32icon:$(Join-Path $glassCore 'LAICA.ico')" $glassRefs $glassSources $glassVersionFile (Join-Path $PSScriptRoot 'host/WorkspaceBackend.cs') (Join-Path $PSScriptRoot 'host/HarnessManager.cs') (Join-Path $PSScriptRoot 'host/GitTools.cs') (Join-Path $PSScriptRoot 'host/BuiltinAgent.cs') (Join-Path $PSScriptRoot 'host/Teams.cs') (Join-Path $PSScriptRoot 'host/Usage.cs') (Join-Path $PSScriptRoot 'host/Pause.cs') (Join-Path $PSScriptRoot 'host/Attach.cs') (Join-Path $PSScriptRoot 'host/Plugins.cs') (Join-Path $PSScriptRoot 'host/PlanUsage.cs') (Join-Path $PSScriptRoot 'host/Analytics.cs') (Join-Path $PSScriptRoot 'host/Handoff.cs') (Join-Path $PSScriptRoot 'host/CoAuthor.cs') (Join-Path $PSScriptRoot 'host/Extensions.cs') (Join-Path $PSScriptRoot 'host/Channels.cs') (Join-Path $PSScriptRoot 'host/History.cs') (Join-Path $PSScriptRoot 'host/CliNodes.cs') (Join-Path $PSScriptRoot 'host/Workflows.cs') (Join-Path $PSScriptRoot 'host/RemoteServer.cs') (Join-Path $PSScriptRoot 'host/GlassHost.cs') (Join-Path $PSScriptRoot 'host/Dictation.cs')
if($LASTEXITCODE -ne 0){throw 'Glass host compilation failed.'}
$glassLegacyGui=[IO.Path]::GetFullPath((Join-Path $glassOutput 'LAICA-v05.exe'))
$glassExpectedLegacyGui=[IO.Path]::GetFullPath((Join-Path (Join-Path $PSScriptRoot 'build') 'LAICA-v05.exe'))
if($glassLegacyGui -ne $glassExpectedLegacyGui -or [IO.Path]::GetDirectoryName($glassLegacyGui) -ne [IO.Path]::GetFullPath($glassOutput)){throw 'Unexpected legacy GUI output path.'}
if(Test-Path -LiteralPath $glassLegacyGui){Remove-Item -LiteralPath $glassLegacyGui -Force}
@('Core','WinForms') | ForEach-Object {Copy-Item -LiteralPath (Join-Path $glassSdk "lib/net462/Microsoft.Web.WebView2.$_.dll") -Destination $glassOutput -Force}
Copy-Item -LiteralPath (Join-Path $glassSdk 'runtimes/win-x64/native/WebView2Loader.dll') -Destination $glassOutput -Force
& $glassCompiler /nologo /target:exe /main:Laica.BridgeHost /optimize+ /warnaserror+ "/out:$(Join-Path $glassOutput 'LAICA.Bridge.exe')" /r:System.Web.Extensions.dll /r:System.Core.dll (Join-Path $glassCore 'Laica.Bridge.cs') (Join-Path $glassCore 'Laica.Observer.cs')
if($LASTEXITCODE -ne 0){throw 'Read-only Codex bridge compilation failed.'}
if(!$Development){Invoke-LaicaReleaseSigning -Path @((Join-Path $glassOutput 'LAICA.exe'),(Join-Path $glassOutput 'LAICA.Bridge.exe')) -Thumbprint $CertificateThumbprint -CertificateStore $CertificateStore -TimestampServer $TimestampServer -SigntoolPath $SigntoolPath}
& $glassCompiler /nologo /target:exe /main:Laica.BackendTests /warnaserror+ "/out:$(Join-Path $PSScriptRoot 'checks/BackendTests.exe')" /r:System.Web.Extensions.dll /r:System.Core.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Xml.Linq.dll /r:System.Xml.dll $glassSources $glassVersionFile (Join-Path $PSScriptRoot 'host/WorkspaceBackend.cs') (Join-Path $PSScriptRoot 'host/HarnessManager.cs') (Join-Path $PSScriptRoot 'host/GitTools.cs') (Join-Path $PSScriptRoot 'host/BuiltinAgent.cs') (Join-Path $PSScriptRoot 'host/Teams.cs') (Join-Path $PSScriptRoot 'host/Usage.cs') (Join-Path $PSScriptRoot 'host/Pause.cs') (Join-Path $PSScriptRoot 'host/Attach.cs') (Join-Path $PSScriptRoot 'host/Plugins.cs') (Join-Path $PSScriptRoot 'host/PlanUsage.cs') (Join-Path $PSScriptRoot 'host/Analytics.cs') (Join-Path $PSScriptRoot 'host/Handoff.cs') (Join-Path $PSScriptRoot 'host/CoAuthor.cs') (Join-Path $PSScriptRoot 'host/Extensions.cs') (Join-Path $PSScriptRoot 'host/Channels.cs') (Join-Path $PSScriptRoot 'host/History.cs') (Join-Path $PSScriptRoot 'host/CliNodes.cs') (Join-Path $PSScriptRoot 'host/Workflows.cs') (Join-Path $PSScriptRoot 'checks/BackendTests.cs')
if($LASTEXITCODE -ne 0){throw 'Backend check compilation failed.'}
& $glassCompiler /nologo /target:exe /main:ObserverWorkTests /warnaserror+ "/out:$(Join-Path $PSScriptRoot 'checks/ObserverWorkTests.exe')" /r:System.Web.Extensions.dll /r:System.Core.dll (Join-Path $glassCore 'Laica.Observer.cs') (Join-Path $PSScriptRoot 'checks/ObserverWorkTests.cs')
if($LASTEXITCODE -ne 0){throw 'Recorded work check compilation failed.'}
& $glassCompiler /nologo /target:exe /main:HarnessTests /warnaserror+ "/out:$(Join-Path $PSScriptRoot 'checks/HarnessTests.exe')" /r:System.Web.Extensions.dll /r:System.Core.dll /r:System.Security.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Xml.Linq.dll /r:System.Xml.dll $glassSources $glassVersionFile (Join-Path $PSScriptRoot 'host/HarnessManager.cs') (Join-Path $PSScriptRoot 'host/GitTools.cs') (Join-Path $PSScriptRoot 'host/BuiltinAgent.cs') (Join-Path $PSScriptRoot 'host/Teams.cs') (Join-Path $PSScriptRoot 'host/Usage.cs') (Join-Path $PSScriptRoot 'host/Pause.cs') (Join-Path $PSScriptRoot 'host/Attach.cs') (Join-Path $PSScriptRoot 'host/Plugins.cs') (Join-Path $PSScriptRoot 'host/PlanUsage.cs') (Join-Path $PSScriptRoot 'host/Analytics.cs') (Join-Path $PSScriptRoot 'host/Handoff.cs') (Join-Path $PSScriptRoot 'host/CoAuthor.cs') (Join-Path $PSScriptRoot 'host/Extensions.cs') (Join-Path $PSScriptRoot 'host/Channels.cs') (Join-Path $PSScriptRoot 'host/History.cs') (Join-Path $PSScriptRoot 'host/CliNodes.cs') (Join-Path $PSScriptRoot 'host/Workflows.cs') (Join-Path $PSScriptRoot 'checks/HarnessTests.cs')
if($LASTEXITCODE -ne 0){throw 'Harness check compilation failed.'}
$glassHarnessSet=@('host/HarnessManager.cs','host/GitTools.cs','host/BuiltinAgent.cs','host/Teams.cs','host/Usage.cs','host/Pause.cs','host/Attach.cs','host/Plugins.cs','host/PlanUsage.cs','host/Analytics.cs','host/Handoff.cs','host/CoAuthor.cs','host/Extensions.cs','host/Channels.cs','host/History.cs','host/CliNodes.cs','host/Workflows.cs')|ForEach-Object{Join-Path $PSScriptRoot $_}
$glassTestRefs=@('/r:System.Web.Extensions.dll','/r:System.Core.dll','/r:System.Security.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll','/r:System.Xml.Linq.dll','/r:System.Xml.dll')
& $glassCompiler /nologo /target:exe /main:ChannelTests /warnaserror+ "/out:$(Join-Path $PSScriptRoot 'checks/ChannelTests.exe')" $glassTestRefs $glassSources $glassVersionFile $glassHarnessSet (Join-Path $PSScriptRoot 'checks/ChannelTests.cs')
if($LASTEXITCODE -ne 0){throw 'Channel check compilation failed.'}
& $glassCompiler /nologo /target:exe /main:RemoteTests /warnaserror+ "/out:$(Join-Path $PSScriptRoot 'checks/RemoteTests.exe')" $glassTestRefs $glassSources $glassVersionFile (Join-Path $PSScriptRoot 'host/WorkspaceBackend.cs') $glassHarnessSet (Join-Path $PSScriptRoot 'host/RemoteServer.cs') (Join-Path $PSScriptRoot 'checks/RemoteTests.cs')
if($LASTEXITCODE -ne 0){throw 'Remote check compilation failed.'}
$glassFrontend=Join-Path $PSScriptRoot 'frontend'
Push-Location $glassFrontend
try {
    & $glassNode node_modules/typescript/bin/tsc --noEmit
    if($LASTEXITCODE -ne 0){throw 'Frontend type check failed.'}
    & $glassNode node_modules/vite/bin/vite.js build --base ./ --logLevel error
    if($LASTEXITCODE -ne 0){throw 'Frontend build failed.'}
} finally {Pop-Location}
$glassUi=Join-Path $glassOutput 'ui'
if(Test-Path -LiteralPath $glassUi){
    $glassResolved=[IO.Path]::GetFullPath($glassUi)
    $glassExpected=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build/ui'))
    if($glassResolved -ne $glassExpected){throw 'Unexpected interface output path.'}
    Remove-Item -LiteralPath $glassResolved -Recurse -Force
}
New-Item -ItemType Directory -Force $glassUi | Out-Null
Copy-Item -Path (Join-Path $glassFrontend 'dist/*') -Destination $glassUi -Recurse -Force
Copy-Item -LiteralPath (Join-Path $glassCore 'LAICA.ico') -Destination $glassUi -Force
$glassIndex=Join-Path $glassUi 'index.html'
$glassPolicy="<meta http-equiv=""Content-Security-Policy"" content=""default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; font-src 'self' data:; frame-src blob:; object-src 'none'; base-uri 'self'; form-action 'none'"">"
[IO.File]::WriteAllText($glassIndex,([IO.File]::ReadAllText($glassIndex).Replace('<head>','<head>'+$glassPolicy)))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'host/Laica.Mode.ps1') -Destination $glassOutput -Force
$glassNotices=Join-Path $glassOutput 'licenses'
New-Item -ItemType Directory -Force $glassNotices | Out-Null
Copy-Item -LiteralPath (Join-Path $glassSdk 'LICENSE.txt') -Destination (Join-Path $glassNotices 'WebView2.txt') -Force
foreach($glassPackage in @('open-glass-ui','react','react-dom','lucide-react')){Copy-Item -LiteralPath (Join-Path $glassFrontend "node_modules/$glassPackage/LICENSE") -Destination (Join-Path $glassNotices "$glassPackage.txt") -Force}
if(Test-Path -LiteralPath (Join-Path $glassOutput 'BackendTests.exe')){Remove-Item -LiteralPath (Join-Path $glassOutput 'BackendTests.exe') -Force}
$glassBytes=(Get-ChildItem -LiteralPath $glassOutput -File -Recurse | Measure-Object -Property Length -Sum).Sum
if($glassBytes -gt 10000000){throw "Program payload exceeds 10 MB: $glassBytes"}
Get-Item -LiteralPath (Join-Path $glassOutput 'LAICA.exe') | Select-Object FullName,Length
Write-Output "Program payload: $glassBytes bytes (shared WebView2 runtime excluded)."
@{Version=$glassVersion;ReleaseSigned=(!$Development);BuildComplete=$true;BuiltUtc=[DateTime]::UtcNow.ToString('o');PayloadBytes=$glassBytes}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $glassOutput 'release.json')

$glassFinalBytes=(Get-ChildItem -LiteralPath $glassOutput -File -Recurse|Measure-Object Length -Sum).Sum
if($glassFinalBytes -gt 10000000){throw 'The complete runtime payload exceeds 10 MB.'}

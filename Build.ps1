param([switch]$Development,[string]$NodePath,[string]$PnpmPath,[string]$CertificateThumbprint,[ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStore='CurrentUser',[string]$TimestampServer='http://timestamp.digicert.com',[string]$SigntoolPath)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Signing.ps1')
if(!$Development){if(!$CertificateThumbprint){throw 'A release requires a trusted signing certificate. For a local unsigned build use -Development.'};$null=Get-LaicaSigningCertificate -Thumbprint $CertificateThumbprint -CertificateStore $CertificateStore}
if(!$NodePath){$NodePath=(Get-Command node.exe -ErrorAction Stop).Source}
if(!$PnpmPath){$PnpmPath=(Get-Command pnpm.cmd -ErrorAction Stop).Source}
function Invoke-LaicaPnpm([string[]]$LaicaArguments){if($PnpmPath -match '\.(cjs|js)$'){& $NodePath $PnpmPath @LaicaArguments}else{& $PnpmPath @LaicaArguments};if($LASTEXITCODE -ne 0){throw 'pnpm command failed.'}}
$laicaFrontend=Join-Path $PSScriptRoot 'frontend'
Push-Location $laicaFrontend
try{Invoke-LaicaPnpm -LaicaArguments @('install','--ignore-workspace','--ignore-scripts','--frozen-lockfile');Invoke-LaicaPnpm -LaicaArguments @('build')}finally{Pop-Location}
$laicaSdk=Join-Path $PSScriptRoot 'vendor/webview2'
$laicaManifest=Get-Content -LiteralPath (Join-Path $laicaSdk 'manifest.json') -Raw|ConvertFrom-Json
foreach($laicaFile in $laicaManifest.Files){if((Get-FileHash -LiteralPath (Join-Path $laicaSdk $laicaFile.File)).Hash -ne $laicaFile.Sha256){throw 'Pinned WebView2 SDK hash mismatch.'}}
$laicaOutput=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'build'))
if([IO.Path]::GetDirectoryName($laicaOutput) -ne [IO.Path]::GetFullPath($PSScriptRoot)){throw 'Unexpected generated build path.'}
if(Test-Path -LiteralPath $laicaOutput){
    $laicaItems=@(Get-Item -LiteralPath $laicaOutput)+@(Get-ChildItem -LiteralPath $laicaOutput -Recurse -Force)
    if(@($laicaItems|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Generated build contains a linked path.'}
    if(@(Get-Process -ErrorAction SilentlyContinue|Where-Object {$_.Path -and $_.Path.StartsWith($laicaOutput+'\',[StringComparison]::OrdinalIgnoreCase)}).Count){throw 'Exit the running build and its bridge before rebuilding.'}
    Remove-Item -LiteralPath $laicaOutput -Recurse -Force
}
New-Item -ItemType Directory -Force $laicaOutput|Out-Null
@{Version='0.7.0';BuildComplete=$false;ReleaseSigned=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $laicaOutput 'release.json')
$laicaCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if(!(Test-Path -LiteralPath $laicaCompiler)){throw '.NET Framework C# compiler is required. See README.md.'}
$laicaCore=Join-Path $PSScriptRoot 'core'
$laicaSources=@('Laica.Runtime.cs','Laica.Services.cs','Laica.Profiles.cs','Laica.Observer.cs','Laica.Paths.cs')|ForEach-Object{Join-Path $laicaCore $_}
$laicaRefs=@('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Core.dll','/r:System.Security.dll',('/r:'+(Join-Path $laicaSdk 'lib/net462/Microsoft.Web.WebView2.Core.dll')),('/r:'+(Join-Path $laicaSdk 'lib/net462/Microsoft.Web.WebView2.WinForms.dll')))
& $laicaCompiler /nologo /target:winexe /platform:x64 /main:Laica.GlassWorkspace.Program /optimize+ /warnaserror+ ('/out:'+(Join-Path $laicaOutput 'LAICA.exe')) ('/win32icon:'+(Join-Path $laicaCore 'LAICA.ico')) $laicaRefs $laicaSources (Join-Path $PSScriptRoot 'host/WorkspaceBackend.cs') (Join-Path $PSScriptRoot 'host/GlassHost.cs')
if($LASTEXITCODE -ne 0){throw 'Desktop host compilation failed.'}
& $laicaCompiler /nologo /target:exe /main:Laica.BridgeHost /optimize+ /warnaserror+ ('/out:'+(Join-Path $laicaOutput 'LAICA.Bridge.exe')) /r:System.Web.Extensions.dll /r:System.Core.dll (Join-Path $laicaCore 'Laica.Bridge.cs') (Join-Path $laicaCore 'Laica.Observer.cs') (Join-Path $laicaCore 'Laica.Paths.cs')
if($LASTEXITCODE -ne 0){throw 'MCP bridge compilation failed.'}
foreach($laicaAssembly in @('Core','WinForms')){Copy-Item -LiteralPath (Join-Path $laicaSdk ('lib/net462/Microsoft.Web.WebView2.'+$laicaAssembly+'.dll')) -Destination $laicaOutput -Force}
Copy-Item -LiteralPath (Join-Path $laicaSdk 'runtimes/win-x64/native/WebView2Loader.dll') -Destination $laicaOutput -Force
Copy-Item -LiteralPath (Join-Path $laicaCore 'LAICA.ico') -Destination $laicaOutput -Force
$laicaUi=Join-Path $laicaOutput 'ui'
if(Test-Path -LiteralPath $laicaUi){$laicaChecked=[IO.Path]::GetFullPath($laicaUi);if([IO.Path]::GetDirectoryName($laicaChecked) -ne [IO.Path]::GetFullPath($laicaOutput)){throw 'Unexpected generated UI path.'};Remove-Item -LiteralPath $laicaChecked -Recurse -Force}
Copy-Item -LiteralPath (Join-Path $laicaFrontend 'dist') -Destination $laicaUi -Recurse
Copy-Item -LiteralPath (Join-Path $laicaCore 'LAICA.ico') -Destination $laicaUi -Force
$laicaIndex=Join-Path $laicaUi 'index.html'
$laicaPolicy='<meta http-equiv="Content-Security-Policy" content="default-src ''self''; script-src ''self''; style-src ''self'' ''unsafe-inline''; img-src ''self'' data:; connect-src ''self''; font-src ''self'' data:; object-src ''none''; base-uri ''self''; form-action ''none''">'
[IO.File]::WriteAllText($laicaIndex,([IO.File]::ReadAllText($laicaIndex).Replace('<head>','<head>'+$laicaPolicy)))
$laicaNotices=Join-Path $laicaOutput 'licenses';New-Item -ItemType Directory -Force $laicaNotices|Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $laicaNotices -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.md') -Destination $laicaNotices -Force
Copy-Item -LiteralPath (Join-Path $laicaSdk 'LICENSE.txt') -Destination (Join-Path $laicaNotices 'WebView2.txt') -Force
foreach($laicaPackage in @('open-glass-ui','react','react-dom','lucide-react')){Copy-Item -LiteralPath (Join-Path $laicaFrontend ('node_modules/'+$laicaPackage+'/LICENSE')) -Destination (Join-Path $laicaNotices ($laicaPackage+'.txt')) -Force}
if(!$Development){Invoke-LaicaReleaseSigning -Path @((Join-Path $laicaOutput 'LAICA.exe'),(Join-Path $laicaOutput 'LAICA.Bridge.exe')) -Thumbprint $CertificateThumbprint -CertificateStore $CertificateStore -TimestampServer $TimestampServer -SigntoolPath $SigntoolPath}
if(@(Get-ChildItem -LiteralPath (Join-Path $laicaUi 'assets') -File|Select-String -Pattern 'example-chat|Example data|Make agent work clear:').Count){throw 'Development Activity fixtures leaked into the production UI.'}
$laicaBytes=(Get-ChildItem -LiteralPath $laicaOutput -File -Recurse|Measure-Object Length -Sum).Sum
if($laicaBytes -gt 10000000){throw 'Runtime payload exceeds 10 MB, excluding shared WebView2 Runtime.'}
$laicaPayload=@(Get-ChildItem -LiteralPath $laicaOutput -File -Recurse|Where-Object {$_.Name -ne 'release.json'}|ForEach-Object {@{Path=$_.FullName.Substring($laicaOutput.Length+1).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
@{Version='0.7.0';BuildComplete=$true;ReleaseSigned=(!$Development);BuiltUtc=[DateTime]::UtcNow.ToString('o');PayloadBytes=$laicaBytes;Files=$laicaPayload}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $laicaOutput 'release.json')
Write-Output ('Built LAICA 0.7.0; signed='+(!$Development)+'; payload bytes='+$laicaBytes)

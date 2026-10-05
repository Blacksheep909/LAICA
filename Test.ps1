param([string]$NodePath)
$ErrorActionPreference='Stop'
if(!$NodePath){$NodePath=(Get-Command node.exe -ErrorAction Stop).Source}
$laicaCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$laicaCore=Join-Path $PSScriptRoot 'core';$laicaTests=Join-Path $PSScriptRoot 'checks';$laicaOutput=Join-Path $laicaTests 'bin'
New-Item -ItemType Directory -Force $laicaOutput|Out-Null
$laicaSources=@('Laica.Runtime.cs','Laica.Services.cs','Laica.Profiles.cs','Laica.Observer.cs','Laica.Paths.cs')|ForEach-Object{Join-Path $laicaCore $_}
& $laicaCompiler /nologo /target:exe /main:Laica.BackendTests /warnaserror+ ('/out:'+(Join-Path $laicaOutput 'BackendTests.exe')) /r:System.Web.Extensions.dll /r:System.Core.dll /r:System.Security.dll $laicaSources (Join-Path $PSScriptRoot 'host/WorkspaceBackend.cs') (Join-Path $laicaTests 'BackendTests.cs')
if($LASTEXITCODE -ne 0){throw 'Backend test compilation failed.'}
Push-Location $laicaOutput
try{& ./BackendTests.exe;if($LASTEXITCODE -ne 0){throw 'Backend tests failed.'}}finally{Pop-Location}
foreach($laicaName in @('ObserverWorkTests','ObserverTests','ObserverFeedbackTests')){
    $laicaExe=Join-Path $laicaOutput ($laicaName+'.exe')
    & $laicaCompiler /nologo /target:exe ('/main:'+$laicaName) /warnaserror+ ('/out:'+$laicaExe) /r:System.Web.Extensions.dll /r:System.Core.dll (Join-Path $laicaCore 'Laica.Observer.cs') (Join-Path $laicaTests ($laicaName+'.cs'))
    if($LASTEXITCODE -ne 0){throw ($laicaName+' compilation failed.')}; & $laicaExe;if($LASTEXITCODE -ne 0){throw ($laicaName+' failed.')}
}
foreach($laicaName in @('ProfileTests','ServiceTests')){
    $laicaExe=Join-Path $laicaOutput ($laicaName+'.exe')
    $laicaMain=if($laicaName -eq 'ServiceTests'){'Laica.ServiceTests'}else{'ProfileTests'}
    & $laicaCompiler /nologo /target:exe ('/main:'+$laicaMain) /warnaserror+ ('/out:'+$laicaExe) /r:System.Web.Extensions.dll /r:System.Core.dll /r:System.Security.dll $laicaSources (Join-Path $laicaTests ($laicaName+'.cs'))
    if($LASTEXITCODE -ne 0){throw ($laicaName+' compilation failed.')}; & $laicaExe;if($LASTEXITCODE -ne 0){throw ($laicaName+' failed.')}
}
$laicaBridgeTests=Join-Path $laicaOutput 'BridgeTests.exe'
& $laicaCompiler /nologo /target:exe /main:BridgeTests /warnaserror+ ('/out:'+$laicaBridgeTests) /r:System.Web.Extensions.dll /r:System.Core.dll (Join-Path $laicaTests 'BridgeTests.cs')
if($LASTEXITCODE -ne 0){throw 'Bridge test compilation failed.'}; & $laicaBridgeTests (Join-Path $PSScriptRoot 'build/LAICA.Bridge.exe');if($LASTEXITCODE -ne 0){throw 'Bridge tests failed.'}
Push-Location (Join-Path $PSScriptRoot 'frontend')
try{& $NodePath --test tests/*.test.mjs;if($LASTEXITCODE -ne 0){throw 'Frontend tests failed.'}}finally{Pop-Location}
& (Join-Path $laicaTests 'SigningTests.ps1')
& (Join-Path $laicaTests 'InstallerTests.ps1')
Write-Output 'All LAICA checks passed.'

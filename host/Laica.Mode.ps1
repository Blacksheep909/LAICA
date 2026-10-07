param([ValidateSet('multi','solo')][string]$Mode,[Parameter(Mandatory=$true)][string]$ResultFile)
$ErrorActionPreference='Stop'
try {
    $laicaHome=if($env:CODEX_HOME){$env:CODEX_HOME}else{Join-Path $env:USERPROFILE '.codex'}
    . (Join-Path $laicaHome 'mode-ui/CodexMode.Core.ps1')
    $laicaResult=Invoke-CodexModeApplyAndRestart -Mode $Mode
    $laicaResponse=@{Success=$true;Mode=$laicaResult.Mode;ProcessId=$laicaResult.ProcessId}
} catch { $laicaResponse=@{Success=$false;Error=$_.Exception.Message} }
[IO.File]::WriteAllText($ResultFile,($laicaResponse|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
if($laicaResponse.Success){exit 0}else{exit 1}

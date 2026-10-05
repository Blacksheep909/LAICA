$ErrorActionPreference = 'Stop'

$signingScript = Join-Path (Split-Path $PSScriptRoot -Parent) 'Signing.ps1'
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile($signingScript,[ref]$tokens,[ref]$parseErrors) | Out-Null
if ($parseErrors.Count -gt 0) { throw "Signing.ps1 parse failed: $($parseErrors[0].Message)" }
. $signingScript

function Assert-Throws {
    param([scriptblock]$Action,[string]$Message)
    $didThrow = $false
    try { & $Action } catch { $didThrow = $true }
    if (-not $didThrow) { throw "Signing test failed: expected rejection: $Message" }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('laica-signing-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Framework C# compiler is required to create the unsigned PE fixture: $compiler" }
    $source = Join-Path $testRoot 'UnsignedFixture.cs'
    $fixture = Join-Path $testRoot 'UnsignedFixture.exe'
    [IO.File]::WriteAllText($source,'internal static class UnsignedFixture { private static void Main() {} }',[Text.Encoding]::UTF8)
    & $compiler /nologo /target:exe "/out:$fixture" $source
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'Could not compile the unsigned executable signing fixture.' }

    $unsigned = Get-AuthenticodeSignature -LiteralPath $fixture
    if ($unsigned.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) { throw "Expected an unsigned PE fixture, got $($unsigned.Status)." }
    Assert-Throws { Assert-LaicaReleaseSignature -Path $fixture } 'unsigned executable must be rejected'

    $before = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
    $storeCountBefore = @(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' -ErrorAction Stop).Count
    Assert-Throws { Invoke-LaicaReleaseSigning -Path $fixture -Thumbprint 'malformed' } 'malformed thumbprint must be rejected during preflight'
    Assert-Throws { Invoke-LaicaReleaseSigning -Path $fixture -Thumbprint ('0' * 40) } 'missing certificate must be rejected during preflight'
    $after = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
    $storeCountAfter = @(Get-ChildItem -LiteralPath 'Cert:\CurrentUser\My' -ErrorAction Stop).Count
    if ($before -ne $after) { throw 'Preflight rejection modified the unsigned release payload.' }
    if ($storeCountBefore -ne $storeCountAfter) { throw 'Signing preflight unexpectedly changed the certificate store.' }
    Write-Output 'SigningTests passed: parser, unsigned release rejection, malformed/missing certificate preflight, payload immutability, and no certificate installation.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot=[IO.Path]::GetFullPath($testRoot)
        $resolvedTemp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
        if(!$resolvedTestRoot.StartsWith($resolvedTemp,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected signing-test cleanup path.'}
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

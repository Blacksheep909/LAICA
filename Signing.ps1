function Get-LaicaSigningCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Thumbprint,
        [ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStore='CurrentUser'
    )

    if ($Thumbprint -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'A signing certificate thumbprint must contain exactly 40 hexadecimal characters.'
    }
    $storePath = 'Cert:\{0}\My' -f $CertificateStore
    $wanted = $Thumbprint.ToUpperInvariant()
    $matches = @(Get-ChildItem -LiteralPath $storePath -ErrorAction Stop | Where-Object { $_.Thumbprint -and $_.Thumbprint.ToUpperInvariant() -eq $wanted })
    if ($matches.Count -ne 1) { throw "Signing certificate $wanted was not found in $storePath." }
    $certificate = $matches[0]
    if (-not $certificate.HasPrivateKey) { throw "Signing certificate $wanted has no private key." }
    $now = Get-Date
    if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) { throw "Signing certificate $wanted is outside its current validity period." }
    if ([string]::Equals($certificate.Subject,$certificate.Issuer,[StringComparison]::OrdinalIgnoreCase)) { throw "Self-signed signing certificate $wanted is not accepted." }

    $hasCodeSigning = $false
    foreach ($extension in $certificate.Extensions) {
        if ($extension.Oid.Value -eq '2.5.29.37') {
            $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$extension
            foreach ($oid in $eku.EnhancedKeyUsages) { if ($oid.Value -eq '1.3.6.1.5.5.7.3.3') { $hasCodeSigning = $true } }
        }
    }
    if (-not $hasCodeSigning) { throw "Signing certificate $wanted does not include the Code Signing EKU." }

    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
        $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
        if (-not $chain.Build($certificate)) {
            $chainErrors = @($chain.ChainStatus | ForEach-Object { '{0}: {1}' -f $_.Status,$_.StatusInformation.Trim() }) -join '; '
            throw "Signing certificate $wanted did not build a trusted code-signing chain with online revocation checking: $chainErrors"
        }
    }
    finally { $chain.Dispose() }
    return $certificate
}

function Assert-LaicaReleaseSignature {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string[]]$Path,
        [string]$ExpectedThumbprint
    )
    if ($ExpectedThumbprint -and $ExpectedThumbprint -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'ExpectedThumbprint must contain exactly 40 hexadecimal characters.'
    }
    $expected = if ($ExpectedThumbprint) { $ExpectedThumbprint.ToUpperInvariant() } else { $null }
    $verified = @()
    foreach ($file in $Path) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release file was not found: $file" }
        $signature = Get-AuthenticodeSignature -LiteralPath $file -ErrorAction Stop
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            throw "Release signature validation failed for '$file': $($signature.Status) $($signature.StatusMessage)"
        }
        if (-not $signature.SignerCertificate) { throw "Release file '$file' has no signer certificate." }
        if (-not $signature.TimeStamperCertificate) { throw "Release file '$file' has no validated timestamp certificate." }
        if ([string]::Equals($signature.SignerCertificate.Subject,$signature.SignerCertificate.Issuer,[StringComparison]::OrdinalIgnoreCase)) {
            throw "Release file '$file' was signed by a self-signed certificate."
        }
        $hasCodeSigning = $false
        foreach ($extension in $signature.SignerCertificate.Extensions) {
            if ($extension.Oid.Value -eq '2.5.29.37') {
                $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]$extension
                foreach ($oid in $eku.EnhancedKeyUsages) { if ($oid.Value -eq '1.3.6.1.5.5.7.3.3') { $hasCodeSigning = $true } }
            }
        }
        if (-not $hasCodeSigning) { throw "Release signer for '$file' lacks the Code Signing EKU." }
        if ($expected -and $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $expected) {
            throw "Release file '$file' was signed by $($signature.SignerCertificate.Thumbprint), expected $expected."
        }
        # Get-AuthenticodeSignature reports Valid only when Windows validates the embedded signature and timestamp.
        $verified += $signature
    }
    return $verified
}

function Invoke-LaicaReleaseSigning {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string[]]$Path,
        [Parameter(Mandatory=$true)][string]$Thumbprint,
        [ValidateSet('CurrentUser','LocalMachine')][string]$CertificateStore='CurrentUser',
        [string]$TimestampServer='http://timestamp.digicert.com',
        [string]$SigntoolPath
    )

    # Resolve every precondition before invoking either signer, so invalid inputs cannot modify a release file.
    $certificate = Get-LaicaSigningCertificate -Thumbprint $Thumbprint -CertificateStore $CertificateStore
    foreach ($file in $Path) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release file was not found: $file" } }
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampServer,[UriKind]::Absolute,[ref]$timestampUri) -or
        $timestampUri.Scheme -notin @('http','https') -or $timestampUri.UserInfo) {
        throw 'TimestampServer must be an absolute HTTP or HTTPS URI without embedded credentials.'
    }

    if ($SigntoolPath) {
        if (-not (Test-Path -LiteralPath $SigntoolPath -PathType Leaf)) { throw "SignTool was not found: $SigntoolPath" }
        $common = @('/sha1',$Thumbprint,'/s','My')
        if ($CertificateStore -eq 'LocalMachine') { $common += '/sm' }
        $signArgs = @('sign') + $common + @('/fd','SHA256','/tr',$TimestampServer,'/td','SHA256') + $Path
        & $SigntoolPath @signArgs
        if ($LASTEXITCODE -ne 0) { throw "SignTool signing failed with exit code $LASTEXITCODE." }
        foreach ($file in $Path) {
            & $SigntoolPath verify /pa /all $file
            if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed for '$file' with exit code $LASTEXITCODE." }
        }
    }
    else {
        if ($timestampUri.Scheme -ne 'http') { throw 'PowerShell timestamping requires HTTP. Use SignTool for an HTTPS RFC 3161 endpoint.' }
        foreach ($file in $Path) {
            $result = Set-AuthenticodeSignature -LiteralPath $file -Certificate $certificate -HashAlgorithm SHA256 -IncludeChain NotRoot -TimestampServer $TimestampServer -ErrorAction Stop
            if ($result.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
                throw "PowerShell signing failed for '$file': $($result.Status) $($result.StatusMessage)"
            }
        }
    }
    return Assert-LaicaReleaseSignature -Path $Path -ExpectedThumbprint $Thumbprint
}

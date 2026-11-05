<#
Build the PengchuanTray installer from setup.iss.

Version policy: PengchuanTray.csproj <Version> is the SINGLE source of truth.
This script reads it and passes /DMyAppVersion=x.y.z to ISCC, so the installer
can never drift from the exe it packages. setup.iss will refuse to compile if
the define is missing.

After compiling it signs the setup exe with the local self-signed test
certificate (same one sign-selfsigned.ps1 uses). Replace that step with
sign-with-artifact-signing.ps1 once a CA certificate is available.

WHY THIS FILE IS PURE ASCII:
  Windows PowerShell 5.1 decodes a .ps1 without a BOM using the system ANSI
  code page. Non-ASCII text gets mangled and can break quote parsing.

Usage (standalone PowerShell window):
  powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$IsccPath,
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'

# $MyInvocation is not available while binding parameter defaults,
# so resolve the repo root in the body instead.
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
}
if (-not $RepoRoot) {
    throw 'Could not resolve repository root. Pass -RepoRoot explicitly.'
}

# When Windows PowerShell 5.1 is launched as a CHILD of PowerShell 7 (for
# example from a pwsh session or CI harness), it inherits PSModulePath with
# PowerShell 7's module directories. Loading Microsoft.PowerShell.Security
# then fails with "member already present" type-data errors, which silently
# removes BOTH the Cert: drive and Set-AuthenticodeSignature. Detect and drop
# the foreign entries so this script behaves the same wherever it is launched.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    $foreign = $env:PSModulePath -split ';' |
        Where-Object { $_ -and ($_ -match '\\PowerShell[/\\]7' -or $_ -match '\.store\\powershell' -or $_ -match 'PowerShell[/\\]Modules$') }
    # keep only the classic Windows PowerShell roots
    $clean = ($env:PSModulePath -split ';' | Where-Object {
        $_ -and $_ -notmatch '\\PowerShell[/\\]7' -and $_ -notmatch '\.store\\powershell'
    })
    if ($clean -and $clean.Count -gt 0) {
        $env:PSModulePath = ($clean -join ';')
    }
}

# ---- 1. single source of truth: csproj <Version> ----
$csproj = Join-Path $RepoRoot 'PengchuanTray.csproj'
if (-not (Test-Path $csproj)) {
    throw "PengchuanTray.csproj not found: $csproj"
}
$csprojText = [System.IO.File]::ReadAllText($csproj)
$versionMatch = [regex]::Match($csprojText, '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) {
    throw '<Version> not found in PengchuanTray.csproj'
}
$version = $versionMatch.Groups[1].Value.Trim()
Write-Host "[1/4] Version from csproj: $version"

# ---- 2. locate ISCC ----
if (-not $IsccPath) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    throw 'ISCC.exe not found. Install Inno Setup 6 first.'
}
Write-Host "[2/4] ISCC: $IsccPath"

# ---- 3. compile ----
$iss = Join-Path $RepoRoot 'installer\setup.iss'
if (-not (Test-Path $iss)) {
    throw "setup.iss not found: $iss"
}
$outDir = Join-Path $RepoRoot 'installer\output'
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}
# drop stale installers from previous versions so the folder never mixes versions
Get-ChildItem $outDir -Filter 'PengchuanTray-Setup-*.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "PengchuanTray-Setup-$version.exe" } |
    ForEach-Object {
        Write-Host "       Removing stale output: $($_.Name)"
        Remove-Item $_.FullName -Force
    }

$setupPath = Join-Path $outDir "PengchuanTray-Setup-$version.exe"
Write-Host "[3/4] Compiling..."
& $IsccPath "/DMyAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE"
}
if (-not (Test-Path $setupPath)) {
    throw "Expected output not found: $setupPath"
}
$setup = Get-Item $setupPath
Write-Host "       $($setup.FullName)  $([math]::Round($setup.Length / 1MB, 1)) MB"

# cross-check: installer version must equal packaged exe version
$exePath = Join-Path $RepoRoot 'PengchuanTray.exe'
if (-not (Test-Path $exePath)) {
    $exePath = 'D:\PengchuanTray\PengchuanTray.exe'
}
if (Test-Path $exePath) {
    $exeVersion = (Get-Item $exePath).VersionInfo.ProductVersion
    Write-Host "       csproj  <Version> : $version"
    Write-Host "       packaged exe ver  : $exeVersion"
    # tolerate the "+<githash>" suffix the SDK appends in CI builds
    if ($exeVersion -and -not $exeVersion.StartsWith($version)) {
        Write-Host '       WARNING: exe was not rebuilt from this csproj. Run dotnet publish first.'
    }
}

# ---- 4. sign ----
if ($SkipSign) {
    Write-Host '[4/4] Skipping sign (-SkipSign)'
}
else {
    Write-Host '[4/4] Signing setup with local test certificate...'
    # Use .NET X509Store instead of the Cert: PSDrive: in some non-interactive
    # hosts (powershell.exe -NoProfile launched as a child process) the Cert:
    # drive is not registered at all, and -ErrorAction SilentlyContinue hides
    # that, which looks like "certificate not found". X509Store always works.
    # The Code Signing EKU OID lives INSIDE the EKU extension (2.5.29.37),
    # it is not the extension's own Oid, so compare the formatted value.
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        [System.Security.Cryptography.X509Certificates.StoreName]::My,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $all = @($store.Certificates | Where-Object {
            $_.HasPrivateKey -and
            ($_.Extensions | Where-Object {
                $_.Oid.Value -eq '2.5.29.37' -and
                $_.Format($false) -match [regex]::Escape($codeSigningOid)
            })
        })
        $candidates = @($all | Sort-Object NotAfter -Descending)
    }
    finally {
        $store.Close()
    }
    if ($candidates.Count -eq 0) {
        throw 'No code-signing certificate in CurrentUser\My. Run sign-selfsigned.ps1 first.'
    }

    # Rule: sign the installer with the SAME certificate that signed the
    # packaged exe, so publisher branding is consistent between the two.
    # Only fall back to "newest trusted chain" if the exe is unsigned.
    $payloadSigner = $null
    $exeForSign = if (Test-Path (Join-Path $RepoRoot 'PengchuanTray.exe')) {
        Join-Path $RepoRoot 'PengchuanTray.exe'
    } elseif (Test-Path 'D:\PengchuanTray\PengchuanTray.exe') {
        'D:\PengchuanTray\PengchuanTray.exe'
    } else { $null }
    if ($exeForSign) {
        $exeSig = Get-AuthenticodeSignature -FilePath $exeForSign
        if ($exeSig.SignerCertificate) {
            $payloadSigner = $exeSig.SignerCertificate.Thumbprint
            Write-Host "       Payload exe signer: $payloadSigner"
        }
    }

    $cert = $null
    if ($payloadSigner) {
        $cert = $candidates | Where-Object { $_.Thumbprint -eq $payloadSigner } | Select-Object -First 1
        if (-not $cert) {
            Write-Host '       WARNING: that cert is no longer in the store; falling back.'
        }
    }
    if (-not $cert) {
        # Prefer a certificate whose chain already builds (its root is
        # installed), so the setup verifies as Valid on this machine.
        foreach ($candidate in $candidates) {
            $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
            $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            if ($chain.Build($candidate)) {
                $cert = $candidate
                break
            }
        }
    }
    if (-not $cert) {
        $cert = $candidates[0]
        Write-Host '       Note: no code-signing cert has a trusted root on this machine.'
        Write-Host '             Signature will verify as UnknownError elsewhere.'
    }
    Write-Host "       Using cert: $($cert.Thumbprint) (valid until $($cert.NotAfter))"
    $sig = Set-AuthenticodeSignature -FilePath $setupPath -Certificate $cert `
        -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256
    $verify = Get-AuthenticodeSignature -FilePath $setupPath
    Write-Host "       Status: $($verify.Status)"
    Write-Host "       Signer: $($verify.SignerCertificate.Subject)"
    if ($verify.Status -ne 'Valid') {
        Write-Host "       Warning: $($verify.StatusMessage)"
    }
}

Write-Host ''
Write-Host "Done: $setupPath"
Write-Host 'Reminder: self-signed only proves the packaging chain. For public'
Write-Host 'distribution replace with a CA certificate (see signing\azure\README.md).'

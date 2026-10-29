param(
  [string]$Path = '',
  [string]$Pfx = '',
  [string]$PasswordFile = '',
  [switch]$InstallCertToMachine
)

# Sign PengchuanTray binaries with an Authenticode certificate.
#
# Why this exists: an unsigned executable is reported by Windows Defender,
# SmartScreen and most enterprise EDR products (Sentinel One, CrowdStrike, 360,
# Huorong ...) as "unknown publisher / security risk" and gets blocked.
# A valid signature is the only thing that removes that warning.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\sign-tray.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\sign-tray.ps1 -Path dist\win-x64\PengchuanTray.exe
#
# Notes:
#   * Pure ASCII on purpose (Windows PowerShell 5.1 decodes non-BOM UTF-8 as GBK).
#   * No timestamping is available here (Set-AuthenticodeSignature has no
#     timestamp parameter), so the signature stays valid as long as the
#     certificate is valid. For public releases use SignPath or a commercial
#     certificate, both of which timestamp the signature.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $Path) {
  $candidates = @(
    (Join-Path $root 'bin\Release\net10.0-windows\win-x64\PengchuanTray.exe'),
    (Join-Path $root 'dist\win-x64\PengchuanTray.exe'),
    (Join-Path $root 'release\win-x64\PengchuanTray.exe')
  )
  $Path = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $Path) {
    throw "No PengchuanTray.exe found. Pass -Path explicitly."
  }
}

if (-not $Pfx) {
  $Pfx = Join-Path $root 'signing\tray-code-signing.pfx'
}
if (-not $PasswordFile) {
  $PasswordFile = Join-Path $root 'signing\cert-password.txt'
}
if (-not (Test-Path $Pfx)) {
  throw "PFX not found: $Pfx (run scripts\make-cert.ps1 first)"
}

$password = (Get-Content $PasswordFile -Raw).Trim()
$secure = ConvertTo-SecureString $password -AsPlainText -Force

# Make the certificate available for signing without importing it permanently.
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Pfx, $password, 'Exportable')
$sig = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -HashAlgorithm SHA256
if ($sig.Status -ne 'Valid') {
  throw "Signing failed: $($sig.Status) / $($sig.StatusMessage)"
}

if ($InstallCertToMachine) {
  $cerPath = [System.IO.Path]::ChangeExtension($Pfx, '.cer')
  if (Test-Path $cerPath) {
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
  }
}

$check = Get-AuthenticodeSignature -FilePath $Path
$hash = (Get-FileHash -Path $Path -Algorithm SHA256).Hash
Write-Host "Signed  : $Path"
Write-Host "Status  : $($check.Status)"
Write-Host "Signer  : $($check.SignerCertificate.Subject)"
Write-Host "SHA256  : $hash"

param(
  [string]$Name = 'Tiechuan Assistant',
  [string]$OutDir = '',
  [int]$Years = 10,
  [switch]$InstallToMachine
)

# Create a self-signed code-signing certificate for PengchuanTray.
#
# Purpose: let the vendor's own machine (and beta testers) install the tray
# without the antivirus reporting "unknown publisher / security risk".
# A self-signed certificate is NOT sufficient for public distribution -
# for end users use SignPath (free for open source) or a commercial OV/Ev cert.
#
# Notes:
#   * This file is intentionally pure ASCII: Windows PowerShell 5.1 decodes
#     non-BOM UTF-8 as GBK and then breaks quoting.
#   * Set-AuthenticodeSignature has no timestamping parameter, so the
#     signature is only valid while the certificate is valid. The default
#     10 year lifetime avoids that problem for internal use.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutDir) {
  $OutDir = Join-Path $root 'signing'
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$pwdFile = Join-Path $OutDir 'cert-password.txt'
$pwd = 'tiechuan-tray-signing'
if (Test-Path $pwdFile) {
  $pwd = (Get-Content $pwdFile -Raw).Trim()
}
else {
  Set-Content -Path $pwdFile -Value $pwd -Encoding ASCII -NoNewline
}

$cert = New-SelfSignedCertificate `
  -Type CodeSigningCert `
  -Subject "CN=$Name, O=ailinkfree, C=CN" `
  -CertStoreLocation Cert:\CurrentUser\My `
  -NotAfter (Get-Date).AddYears($Years) `
  -KeyUsage DigitalSignature `
  -KeyExportPolicy Exportable `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

$pfx = Join-Path $OutDir 'tray-code-signing.pfx'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pwd -AsPlainText -Force) | Out-Null
$cer = Join-Path $OutDir 'tray-code-signing.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

if ($InstallToMachine) {
  # Trust the publisher on this machine so Windows/Defender/EDR stops
  # treating the file as coming from an unknown publisher.
  $store = 'Cert:\LocalMachine\Root'
  Import-Certificate -FilePath $cer -CertStoreLocation $store | Out-Null
  $store = 'Cert:\LocalMachine\TrustedPublisher'
  Import-Certificate -FilePath $cer -CertStoreLocation $store | Out-Null
  Write-Host "Installed certificate into LocalMachine Root + TrustedPublisher (run as administrator)."
}
else {
  Write-Host "Certificate created but NOT trusted. Re-run with -InstallToMachine (administrator) to trust it locally."
}

Write-Host "Subject   : $($cert.Subject)"
Write-Host "Thumbprint: $($cert.Thumbprint)"
Write-Host "Expires   : $($cert.NotAfter)"
Write-Host "PFX       : $pfx"
Write-Host "Password  : stored in $pwdFile"

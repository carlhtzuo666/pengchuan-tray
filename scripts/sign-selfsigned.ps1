<#
PengchuanTray - self-signed packaging and signing utility.
For LOCAL / CONTROLLED-ENVIRONMENT verification of the packaging pipeline only.

NOTE: A self-signed certificate is trusted only on machines where the root
      certificate has been installed. Other computers will show
      "Unknown publisher". For public distribution use a CA-issued code
      signing certificate (Azure Artifact Signing / OV / EV).

WHY THIS FILE IS PURE ASCII:
  Windows PowerShell 5.1 decodes a .ps1 WITHOUT a BOM using the system ANSI
  code page (GBK on Chinese Windows). Non-ASCII text then gets mangled and can
  even break quote parsing. Keeping this file pure ASCII makes it
  encoding-independent, so it runs the same no matter how it was saved.

Usage (run in a standalone PowerShell window):
  powershell -NoProfile -ExecutionPolicy Bypass -File <path>\sign-selfsigned.ps1

Optional parameters:
  -SkipPublish   Skip the publish step and sign the existing exe.
  -Subject       Certificate subject. Default: CN=ailinkfree, O=ailinkfree, C=CN
  -Years         Certificate validity in years. Default: 3
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$Subject = 'CN=ailinkfree, O=ailinkfree, C=CN',
    [int]$Years = 3
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$signDir = 'D:\PengchuanTray\signing'
$publishDir = 'D:\PengchuanTray\publish'
$exePath = Join-Path $publishDir 'PengchuanTray.exe'
$pfxPath = Join-Path $signDir 'test-selfsigned.pfx'
$cerPath = Join-Path $signDir 'test-selfsigned.cer'
# The .pfx (private key backup) must never be committed, so its password is
# generated fresh at runtime instead of being hardcoded in the repository.
# Print it once at the end; regenerate the .pfx if you lose it (it only backs
# up a throwaway TEST certificate).
$pfxBytes = New-Object byte[] 16
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($pfxBytes)
$pfxPassword = -join ($pfxBytes | ForEach-Object { '{0:x2}' -f $_ })

# Only the .NET runtime is installed system-wide on this machine; the SDK lives
# under the user profile, so prefer that one and fall back to PATH.
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = 'dotnet'
}

foreach ($dir in @($signDir, $publishDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

Write-Host '[1/5] Preparing self-signed code signing certificate (Code Signing EKU)...'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $Subject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if ($null -eq $cert) {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -FriendlyName 'PengchuanTray self-signed (test only)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears($Years) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
    Write-Host "      Created certificate: $($cert.Thumbprint)"
}
else {
    Write-Host "      Reusing certificate: $($cert.Thumbprint) (valid until $($cert.NotAfter))"
}
$secure = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secure | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

if (-not $SkipPublish) {
    Write-Host '[2/5] Publishing single-file self-contained build (win-x64, ~100 MB)...'
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }
    Push-Location $repoRoot
    try {
        & $dotnet publish -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=None `
            -o $publishDir
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed (exit $LASTEXITCODE)"
        }
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host '[2/5] Skipping publish (-SkipPublish)'
}

if (-not (Test-Path $exePath)) {
    throw "Target executable not found: $exePath"
}

Write-Host '[3/5] Signing (SHA256 + RFC3161 timestamp)...'
$result = Set-AuthenticodeSignature -FilePath $exePath -Certificate $cert `
    -TimestampServer 'http://timestamp.digicert.com' -HashAlgorithm SHA256
Write-Host "      Sign status: $($result.Status)"

Write-Host '[4/5] Installing root certificate into CurrentUser\Trusted Root...'
# Import-Certificate fails here with "UI is not allowed in this operation"
# (the security warning dialog is suppressed in this environment), certutil works.
& certutil -user -addstore Root $cerPath | Out-Null

Write-Host '[5/5] Verifying signature...'
$verify = Get-AuthenticodeSignature -FilePath $exePath
Write-Host "      Status  : $($verify.Status)"
Write-Host "      Signer  : $($verify.SignerCertificate.Subject)"
if ($null -ne $verify.TimeStamperCertificate) {
    Write-Host "      Timestamp: $($verify.TimeStamperCertificate.Subject)"
}
if ($verify.Status -ne 'Valid') {
    Write-Host "      Warning: $($verify.StatusMessage)"
}

Write-Host ''
Write-Host 'Done. Artifacts:'
Write-Host "  exe : $exePath"
Write-Host "  pfx : $pfxPath (password: $pfxPassword, test only, do not ship)"
Write-Host "  cer : $cerPath"

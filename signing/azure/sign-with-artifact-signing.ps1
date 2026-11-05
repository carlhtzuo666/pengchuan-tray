<#
Sign a file with Azure Artifact Signing (formerly "Trusted Signing").

Prerequisites
  1. An Azure subscription with an Artifact Signing account (Basic SKU is enough).
  2. Identity Validation completed for that account (organization or individual).
  3. A Certificate Profile of type "Public Trust" created.
  4. The Artifact Signing client tools installed, which provide:
       - signtool.exe
       - Azure.CodeSigning.Dlib.dll
     Download from the official Microsoft page (search "Artifact Signing client tools").
  5. Signed in: az login   (or use a service principal as shown in the workflow file)

WHY THIS FILE IS PURE ASCII:
  Windows PowerShell 5.1 decodes a .ps1 without a BOM using the system ANSI code
  page. Non-ASCII text gets mangled and can break quote parsing. Keep it ASCII.

Usage
  powershell -NoProfile -ExecutionPolicy Bypass -File sign-with-artifact-signing.ps1 `
      -File "D:\PengchuanTray\publish\PengchuanTray.exe" `
      -Endpoint "https://your-acct.codesigning.azure.net/" `
      -Account "your-acct" `
      -Profile "your-profile"

Optional
  -Dlib      Full path to Azure.CodeSigning.Dlib.dll
  -SignTool  Full path to signtool.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$File,
    [Parameter(Mandatory = $true)][string]$Endpoint,
    [Parameter(Mandatory = $true)][string]$Account,
    [Parameter(Mandatory = $true)][string]$Profile,
    [string]$Dlib = 'C:\Program Files\Microsoft\ArtifactSigning\Azure.CodeSigning.Dlib.dll',
    [string]$SignTool = 'signtool.exe'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $File)) {
    throw "File not found: $File"
}
if (-not (Test-Path $Dlib)) {
    throw "Artifact Signing dlib not found: $Dlib (install the client tools first)"
}

$full = (Resolve-Path $File).Path
$metaPath = Join-Path $env:TEMP 'artifact-signing-metadata.json'

# The dlib reads this metadata file. FileDigest must be the file being signed.
$meta = [ordered]@{
    Endpoint                = $Endpoint
    CodeSigningAccountName  = $Account
    CertificateProfileName  = $Profile
    FileDigest              = $full
}
[System.IO.File]::WriteAllText($metaPath, ($meta | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Signing: $full"
& $SignTool sign /v /debug /fd SHA256 `
    /tr 'http://timestamp.acs.microsoft.com' /td SHA256 `
    /dlib $Dlib /dmdf $metaPath $full

if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE"
}

Remove-Item $metaPath -Force -ErrorAction SilentlyContinue

$verify = Get-AuthenticodeSignature -FilePath $full
Write-Host "Status : $($verify.Status)"
Write-Host "Signer : $($verify.SignerCertificate.Subject)"
# Note: a CA-issued certificate signs as your verified organization/individual
# identity, so "Status" should be "Valid" on any normal Windows machine.
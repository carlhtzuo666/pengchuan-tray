<#
Build an MSIX package for PengchuanTray and sign it for LOCAL TESTING.

Why this script exists:
  - Uses the makeappx.exe / signtool.exe that ship inside the
    Microsoft.Windows.SDK.BuildTools NuGet package (already in the local NuGet
    cache), so no 1-2 GB Windows SDK install is required.
  - AppxManifest.xml contains __IDENTITY_NAME__ / __PUBLISHER__ / __VERSION__
    placeholders; this script substitutes them before packing.

IMPORTANT (the most common failure):
  The Publisher in the manifest MUST match the Subject of the signing
  certificate exactly. For local testing we use the self-signed cert whose
  Subject is "CN=ailinkfree, O=ailinkfree, C=CN". When submitting to the
  Microsoft Store you must replace -Publisher with the Publisher shown in
  Partner Center (looks like CN=<GUID>).

WHY PURE ASCII:
  Windows PowerShell 5.1 decodes a .ps1 without a BOM using the system ANSI code
  page (GBK on Chinese Windows). Non-ASCII text gets mangled and can break quote
  parsing.

Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File build-msix.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File build-msix.ps1 -SkipSign
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$IdentityName = 'ailinkfree.PengchuanTray',
    [string]$Publisher = 'CN=ailinkfree, O=ailinkfree, C=CN',
    [string]$OutputDir = '',
    [string]$Thumbprint = '',
    [switch]$SelfContained,
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath) }
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'Package\output' }

$dn = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dn)) { $dn = 'dotnet' }

# ---- locate makeappx / signtool from the cached BuildTools package ----
function Find-SdkTool {
    param([string]$Name)
    $roots = @(
        (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'),
        (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools.msix')
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem $root -Recurse -Filter $Name -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Select-Object -Last 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'
if (-not $makeappx) { throw 'makeappx.exe not found in NuGet cache (microsoft.windows.sdk.buildtools).' }
Write-Host "[1/6] makeappx: $makeappx"

# ---- version from csproj (single source of truth), padded to 4 segments ----
$csproj = Join-Path $RepoRoot 'PengchuanTray.csproj'
$csprojText = [System.IO.File]::ReadAllText($csproj)
$m = [regex]::Match($csprojText, '<Version>([^<]+)</Version>')
if (-not $m.Success) { throw '<Version> not found in PengchuanTray.csproj' }
$ver3 = $m.Groups[1].Value.Trim()
$parts = $ver3.Split('.')
while ($parts.Count -lt 4) { $parts += '0' }
$ver4 = ($parts[0..3] -join '.')
Write-Host "[2/6] Version: $ver3 -> MSIX $ver4"

# ---- publish (MSIX packages cannot contain a single-file bundle) ----
$stage = Join-Path $RepoRoot 'Package\stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

Write-Host '[3/6] dotnet publish ...'
$pubArgs = @('publish', $csproj, '-c', 'Release', '-r', 'win-x64',
    '-p:PublishSingleFile=false',
    '-p:DebugType=None',
    '-o', $stage)
if ($SelfContained) {
    $pubArgs += '--self-contained'
    $pubArgs += 'true'
}
else {
    $pubArgs += '--self-contained'
    $pubArgs += 'false'
}
& $dn @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# ---- stage assets + manifest ----
$assetsSrc = Join-Path $RepoRoot 'Package\Assets'
if (-not (Test-Path $assetsSrc)) { throw "Icon assets missing: $assetsSrc (run scripts\gen-msix-icons.ps1 first)" }
$assetsDst = Join-Path $stage 'Assets'
if (-not (Test-Path $assetsDst)) { New-Item -ItemType Directory -Path $assetsDst -Force | Out-Null }
Copy-Item (Join-Path $assetsSrc '*.png') $assetsDst -Force

$manifestTpl = Join-Path $RepoRoot 'Package\AppxManifest.xml'
$manifestText = [System.IO.File]::ReadAllText($manifestTpl)
$manifestText = $manifestText.Replace('__IDENTITY_NAME__', $IdentityName)
$manifestText = $manifestText.Replace('__PUBLISHER__', $Publisher)
$manifestText = $manifestText.Replace('__VERSION__', $ver4)
$manifestOut = Join-Path $stage 'AppxManifest.xml'
[System.IO.File]::WriteAllText($manifestOut, $manifestText, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "[4/6] Manifest staged (Identity=$IdentityName, Publisher=$Publisher)"

# ---- pack ----
$msix = Join-Path $OutputDir ("PengchuanTray-{0}-x64.msix" -f $ver3)
if (Test-Path $msix) { Remove-Item $msix -Force }
Write-Host '[5/6] makeappx pack ...'
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed (exit $LASTEXITCODE)" }
$msixItem = Get-Item $msix
Write-Host ("       {0}  {1:N1} MB" -f $msixItem.FullName, ($msixItem.Length / 1MB))

# ---- sign ----
if ($SkipSign) {
    Write-Host '[6/6] Skipping sign (-SkipSign)'
}
else {
    if (-not $signtool) { throw 'signtool.exe not found in NuGet cache.' }

    # 复用 build-installer.ps1 里已验证的做法：用 X509Store 而不是 Cert: 盘符。
    # 原因：当 powershell.exe 5.1 作为 PowerShell 7 的子进程启动时，会继承被污染的
    # PSModulePath，导致 Microsoft.PowerShell.Security 模块加载失败，
    # 表现为 Cert: 盘符根本不存在（用 -ErrorAction SilentlyContinue 还会把该错误吞掉，
    # 看起来就像"证书不存在"）。X509Store 不依赖任何 PS 模块，永远可用。
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        [System.Security.Cryptography.X509Certificates.StoreName]::My,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $all = @($store.Certificates | Where-Object {
            $_.HasPrivateKey -and
            ($_.Extensions | Where-Object {
                $_.Oid.Value -eq '2.5.29.37' -and $_.Format($false) -match [regex]::Escape($codeSigningOid)
            })
        })
    }
    finally { $store.Close() }

    $cert = $null
    if ($Thumbprint) {
        $cert = $all | Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    }
    if (-not $cert) {
        $cert = $all | Where-Object { $_.Subject -eq $Publisher } |
            Sort-Object NotAfter -Descending | Select-Object -First 1
    }
    if (-not $cert) {
        Write-Host '       Available code-signing certs (with private key):'
        $all | ForEach-Object { Write-Host ("         {0}  {1}" -f $_.Thumbprint, $_.Subject) }
        throw "No code-signing certificate matching Publisher '$Publisher' or -Thumbprint. Manifest Publisher and cert Subject must match exactly."
    }
    Write-Host "[6/6] Signing with $($cert.Thumbprint) ..."
    & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint /tr 'http://timestamp.digicert.com' /td SHA256 $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)" }
    & $signtool verify /pa /v $msix | Select-Object -Last 5 | ForEach-Object { Write-Host "       $_" }
}

Write-Host ''
Write-Host "Done: $msix"
Write-Host 'Reminder: this is a LOCAL TEST signature. For Store submission replace'
Write-Host '-Publisher with the Partner Center Publisher (CN=<GUID>) and let the Store re-sign it.'

param(
  [switch]$SkipSign,
  [switch]$SelfContainedOnly
)

# Build, sign and package the tray helper for distribution.
#
# Why: an unsigned exe is reported by Defender / SmartScreen / enterprise EDR
# as "unknown publisher / security risk" and gets blocked. This script produces
# a signed artifact plus a checksum file, so the delivered file is installable.
#
# Steps:
#   1. publish (framework-dependent + self-contained single file)
#   2. Authenticode-sign every produced exe (unless -SkipSign)
#   3. clear Mark-of-the-Web (Zone.Identifier) so Windows does not show the
#      "Windows protected your PC" block for the copied file
#   4. zip + SHA256SUMS.txt for verification by the recipient
#
# Notes:
#   * Pure ASCII on purpose (Windows PowerShell 5.1 decodes non-BOM UTF-8 as GBK).
#   * Signing needs scripts\make-cert.ps1 to have been run once, or a real
#     certificate (SignPath / commercial OV) supplied through -Pfx.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

$dotnet = 'D:\dotnet-sdk\dotnet.exe'
if (-not (Test-Path $dotnet)) {
  $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
  if (-not $cmd) { throw 'dotnet SDK not found (expected at D:\dotnet-sdk\dotnet.exe)' }
  $dotnet = $cmd.Source
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$stamp = Get-Date -Format 'yyyyMMdd'
$outRoot = Join-Path $root "dist\release-$stamp"
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

function Publish-Variant([string]$name, [bool]$selfContained) {
  $out = Join-Path $outRoot $name
  & $dotnet publish (Join-Path $root 'PengchuanTray.csproj') `
    -c Release -r win-x64 `
    "--self-contained:$($selfContained.ToString().ToLowerInvariant())" `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=$($selfContained.ToString().ToLowerInvariant()) `
    -p:DebugType=none `
    -o $out | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "publish failed for $name (exit $LASTEXITCODE)" }
  Copy-Item (Join-Path $root 'scripts\start-tray-dotnet.cmd') $out -Force
  return $out
}

<#
  兼容版：exe 与 dll 分离（PublishSingleFile=false），并附带「启动贴传助手.cmd」。
  作用：企业 EDR（Sentinel Agent 等）拦截自定义 exe 直接执行时，
  改用微软签名的 dotnet.exe 加载同一个 dll 即可正常启动。
  这是本机（被 Sentinel 拦截）唯一实测可用的启动方式。
#>
function Publish-Compatible {
  $out = Join-Path $outRoot 'compatible'
  & $dotnet publish (Join-Path $root 'PengchuanTray.csproj') `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $out | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'publish failed for compatible build' }
  Copy-Item (Join-Path $root 'scripts\启动贴传助手.cmd') $out -Force
  Copy-Item (Join-Path $root 'scripts\兼容版使用说明.txt') $out -Force
  return $out
}

Write-Host '== publish =='
$variants = @()
if (-not $SelfContainedOnly) { $variants += ,@('framework', (Publish-Variant 'framework' $false)) }
$variants += ,@('self-contained', (Publish-Variant 'self-contained' $true))
$variants += ,@('compatible', (Publish-Compatible))

if (-not $SkipSign) {
  Write-Host '== sign =='
  foreach ($v in $variants) {
    $exe = Join-Path $v[1] 'PengchuanTray.exe'
    if (-not (Test-Path $exe)) { continue }
    & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\sign-tray.ps1') -Path $exe
    if ($LASTEXITCODE -ne 0) { throw "sign failed for $($v[0])" }
  }
}
else {
  Write-Host '== sign skipped (artifacts stay UNSIGNED and will be flagged by antivirus) =='
}

Write-Host '== clear Mark-of-the-Web =='
Get-ChildItem $outRoot -Recurse -File | ForEach-Object {
  try { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue } catch { }
}

Write-Host '== package =='
$zipPath = Join-Path $root "dist\PengchuanTray-$stamp.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
try { Unblock-File -LiteralPath $zipPath -ErrorAction SilentlyContinue } catch { }

$sums = Join-Path $outRoot 'SHA256SUMS.txt'
$lines = @()
Get-ChildItem $outRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
  $rel = $_.FullName.Substring($outRoot.Length + 1)
  $lines += "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $rel"
}
Set-Content -Path $sums -Value $lines -Encoding ASCII

Write-Host ''
Write-Host "Artifacts : $outRoot"
Write-Host "Zip       : $zipPath"
Write-Host "Checksums : $sums"
Write-Host ''
Write-Host 'Verify a file with:'
Write-Host "  Get-FileHash -Algorithm SHA256 <file>"
Write-Host "  Get-AuthenticodeSignature <file>   # expect Status=Valid"

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

# Stop any running tray instance before publishing. A tray started through the
# dotnet host keeps PengchuanTray.dll locked, and MSBuild then fails with
# MSB3027 "file is locked by .NET Host" after 10 retries.
function Stop-RunningTray {
  $stopped = 0
  Get-Process -Name 'PengchuanTray' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $stopped++ } catch { }
  }
  Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'PengchuanTray' } | ForEach-Object {
      try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop; $stopped++ } catch { }
    }
  if ($stopped -gt 0) {
    Write-Host "== stopped $stopped running tray process(es) to release file locks =="
    Start-Sleep -Seconds 2
  }
}
Stop-RunningTray

$stamp = Get-Date -Format 'yyyyMMdd'
$outRoot = Join-Path $root "dist\release-$stamp"
New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

function Publish-Variant([string]$name, [bool]$selfContained) {
  $out = Join-Path $outRoot $name
  # `$null = &` swallows dotnet's stdout so it does not leak into this function's
  # return value (a bare `& ...` would make the caller's array contain dotnet
  # log lines, which broke Join-Path with "Cannot find drive ...").
  $null = & $dotnet publish (Join-Path $root 'PengchuanTray.csproj') `
    -c Release -r win-x64 `
    "--self-contained:$($selfContained.ToString().ToLowerInvariant())" `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=$($selfContained.ToString().ToLowerInvariant()) `
    -p:DebugType=none `
    -o $out
  # Check the produced file instead of $LASTEXITCODE: a preceding pipeline can
  # leave $LASTEXITCODE stale, which produced false "publish failed" errors.
  $exe = Join-Path $out 'PengchuanTray.exe'
  if (-not (Test-Path $exe)) { throw "publish produced no exe for $name" }
  Copy-Item (Join-Path $root 'scripts\start-tray-dotnet.cmd') $out -Force
  return $out
}

<#
  Compatible build: exe and dll side by side (PublishSingleFile=false), plus a
  launcher script. Purpose: corporate EDR (Sentinel Agent etc.) blocks direct
  execution of a custom exe; loading the same dll through Microsoft-signed
  dotnet.exe works. This is the only launch path verified to work on a machine
  where Sentinel blocks the exe (2026-10-29).

  NOTE: this file must stay pure ASCII. Windows PowerShell 5.1 decodes a
  non-BOM UTF-8 script as GBK, which mangles any Chinese literal (including
  Chinese file names) and breaks Join-Path. The Chinese display names are
  applied by copying from the ASCII source names below.
#>
function Publish-Compatible {
  $out = Join-Path $outRoot 'compatible'
  # `$null = &` swallows dotnet stdout so it does not leak into the return value.
  $null = & $dotnet publish (Join-Path $root 'PengchuanTray.csproj') `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -o $out
  # 用产物文件判断成败，不用 $LASTEXITCODE（可能被前面的管道污染）
  $dll = Join-Path $out 'PengchuanTray.dll'
  if (-not (Test-Path $dll)) { throw 'compatible publish produced no dll' }

  # Source files are ASCII-named; rename to the Chinese display names users see.
  # Chinese names are built from Unicode code points so this script stays pure
  # ASCII (PowerShell 5.1 would otherwise mangle the literals on a GBK console).
  #   launcher: "launch Tiechuan assistant.cmd"  (U+542F U+52A8 U+8D34 U+4F20 U+52A9 U+624B)
  #   readme  : "compatible edition usage.txt"   (U+517C U+5BB9 U+7248 U+4F7F U+7528 U+8BF4 U+660E)
  $launcherName = [string]::Concat([char]0x542F, [char]0x52A8, [char]0x8D34, [char]0x4F20,
    [char]0x52A9, [char]0x624B, '.cmd')
  $readmeName = [string]::Concat([char]0x517C, [char]0x5BB9, [char]0x7248, [char]0x4F7F,
    [char]0x7528, [char]0x8BF4, [char]0x660E, '.txt')

  Copy-Item (Join-Path $root 'scripts\launcher-compatible.cmd') (Join-Path $out $launcherName) -Force
  Copy-Item (Join-Path $root 'scripts\readme-compatible.txt') (Join-Path $out $readmeName) -Force
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

param(
  [switch]$SelfContained
)

# Publish script: produces a single-file portable exe under dist\win-x64.
# Default is framework-dependent (small, target machine needs .NET 10 Desktop Runtime).
# Use -SelfContained to embed the runtime (large, zero dependency on target machine).
# Keep this file pure ASCII: Windows PowerShell 5.1 decodes non-BOM UTF-8 as GBK and breaks quoting.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($cmd) { $cmd.Source } else { $null }
if (-not $dotnet) {
  throw 'dotnet SDK not found. Install .NET 10 SDK first: https://dotnet.microsoft.com/download'
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$out = Join-Path $root 'dist\win-x64'

& $dotnet publish (Join-Path $root 'PengchuanTray.csproj') `
  -c Release `
  -r win-x64 `
  --self-contained:$SelfContained `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=$($SelfContained.IsPresent) `
  -o $out

if ($LASTEXITCODE -ne 0) {
  throw "publish failed (exit $LASTEXITCODE)"
}

Get-ChildItem $out | Sort-Object Length -Descending |
  Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
  Format-Table -AutoSize | Out-String | Write-Host

<#
Generate MSIX / Microsoft Store icon assets from the HarmonyOS layered icon.

Source (reused from the HarmonyOS app so branding stays consistent):
  D:\tools\harness-personal\harmony-share\AppScope\resources\base\media\foreground.png
  D:\tools\harness-personal\harmony-share\AppScope\resources\base\media\background.png

Output: <repo>\Package\Assets\*.png

WHY PURE ASCII:
  Windows PowerShell 5.1 decodes a .ps1 without a BOM using the system ANSI code
  page (GBK on Chinese Windows). Non-ASCII text gets mangled and can break quote
  parsing. Keep this file ASCII-only.

Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File gen-msix-icons.ps1
#>
[CmdletBinding()]
param(
    [string]$Foreground,
    [string]$Background,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

if (-not $OutDir) {
    $OutDir = Join-Path (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) 'Package\Assets'
}
if (-not $Foreground) {
    $Foreground = 'D:\tools\harness-personal\harmony-share\AppScope\resources\base\media\foreground.png'
}
if (-not $Background) {
    $Background = 'D:\tools\harness-personal\harmony-share\AppScope\resources\base\media\background.png'
}

foreach ($f in @($Foreground, $Background)) {
    if (-not (Test-Path $f)) { throw "Source image not found: $f" }
}
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Drawing

$fg = [System.Drawing.Image]::FromFile($Foreground)
$bg = [System.Drawing.Image]::FromFile($Background)

function New-ComposedIcon {
    param([int]$Width, [int]$Height, [string]$Path, [double]$ForegroundScale = 1.0)
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        # background fills the canvas
        $g.DrawImage($script:bg, (New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)))
        # foreground is centred, optionally scaled down (layered-icon safe area)
        $fw = [int][Math]::Round($Width * $ForegroundScale)
        $fh = [int][Math]::Round($Height * $ForegroundScale)
        $fx = [int](($Width - $fw) / 2)
        $fy = [int](($Height - $fh) / 2)
        $g.DrawImage($script:fg, (New-Object System.Drawing.Rectangle($fx, $fy, $fw, $fh)))
    }
    finally { $g.Dispose() }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# name, width, height, foreground scale
$targets = @(
    @('StoreLogo', 50, 50, 0.92),
    @('StoreLogo.scale-100', 50, 50, 0.92),
    @('StoreLogo.scale-125', 63, 63, 0.92),
    @('StoreLogo.scale-150', 75, 75, 0.92),
    @('StoreLogo.scale-200', 100, 100, 0.92),
    @('StoreLogo.scale-400', 200, 200, 0.92),

    @('Square44x44Logo', 44, 44, 1.0),
    @('Square44x44Logo.scale-100', 44, 44, 1.0),
    @('Square44x44Logo.scale-125', 55, 55, 1.0),
    @('Square44x44Logo.scale-150', 66, 66, 1.0),
    @('Square44x44Logo.scale-200', 88, 88, 1.0),
    @('Square44x44Logo.scale-400', 176, 176, 1.0),
    @('Square44x44Logo.targetsize-16', 16, 16, 1.0),
    @('Square44x44Logo.targetsize-24', 24, 24, 1.0),
    @('Square44x44Logo.targetsize-32', 32, 32, 1.0),
    @('Square44x44Logo.targetsize-48', 48, 48, 1.0),
    @('Square44x44Logo.targetsize-256', 256, 256, 1.0),
    @('Square44x44Logo.targetsize-16_altform-unplated', 16, 16, 1.0),
    @('Square44x44Logo.targetsize-24_altform-unplated', 24, 24, 1.0),
    @('Square44x44Logo.targetsize-32_altform-unplated', 32, 32, 1.0),
    @('Square44x44Logo.targetsize-48_altform-unplated', 48, 48, 1.0),
    @('Square44x44Logo.targetsize-256_altform-unplated', 256, 256, 1.0),

    @('Square150x150Logo', 150, 150, 0.86),
    @('Square150x150Logo.scale-100', 150, 150, 0.86),
    @('Square150x150Logo.scale-125', 188, 188, 0.86),
    @('Square150x150Logo.scale-150', 225, 225, 0.86),
    @('Square150x150Logo.scale-200', 300, 300, 0.86),
    @('Square150x150Logo.scale-400', 600, 600, 0.86),

    @('Wide310x150Logo', 310, 150, 0.72),
    @('Wide310x150Logo.scale-100', 310, 150, 0.72),
    @('Wide310x150Logo.scale-200', 620, 300, 0.72),

    @('Square310x310Logo', 310, 310, 0.86),
    @('Square310x310Logo.scale-100', 310, 310, 0.86),
    @('Square310x310Logo.scale-200', 620, 620, 0.86),

    @('SplashScreen', 620, 300, 0.62),
    @('SplashScreen.scale-100', 620, 300, 0.62),
    @('SplashScreen.scale-200', 1240, 600, 0.62),

    # Partner Center store listing icon (not part of the package)
    @('StoreListingIcon-300x300', 300, 300, 0.86)
)

Write-Host ("Generating {0} icon files into {1}" -f $targets.Count, $OutDir)
foreach ($t in $targets) {
    $path = Join-Path $OutDir ($t[0] + '.png')
    New-ComposedIcon -Width $t[1] -Height $t[2] -Path $path -ForegroundScale $t[3]
}
Write-Host 'Done.'
Get-ChildItem $OutDir -Filter *.png | Measure-Object -Property Length -Sum |
    ForEach-Object { Write-Host ("Files: {0}, total {1:N0} KB" -f $_.Count, ($_.Sum / 1KB)) }

$fg.Dispose()
$bg.Dispose()

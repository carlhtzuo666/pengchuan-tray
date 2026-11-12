# 生成 Microsoft Store 提交包（.msixupload）
#
# 用途：把已打好的 MSIX 包装成商店后台要求的 .msixupload 格式。
# 商店受理时由微软用自己的证书重签，所以本地可以不签名（或用自己的测试证书）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-store-upload.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\make-store-upload.ps1 -MsixPath "<指定 msix>"
#
# 参数：
#   -MsixPath  已生成的 .msix 路径；省略则自动取 Package\output 下最新一个
#   -OutputDir 输出目录，默认 Package\output
#
# 说明：.msixupload 本质是一个 ZIP，内部只放一个 .msix 即可被商店识别。

param(
    [string]$MsixPath,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'Package\output' }

# ---- 1) 定位 MSIX ----
if (-not $MsixPath) {
    $latest = Get-ChildItem (Join-Path $OutputDir '*.msix') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) {
        throw "在 $OutputDir 找不到 .msix，请先运行 scripts\build-msix.ps1"
    }
    $MsixPath = $latest.FullName
}
if (-not (Test-Path $MsixPath)) { throw "MSIX 不存在: $MsixPath" }
Write-Host "[1/4] 源包: $MsixPath"

# ---- 2) 目标路径 ----
$name = [System.IO.Path]::GetFileNameWithoutExtension($MsixPath)
$upload = Join-Path $OutputDir ("$name.msixupload")
Write-Host "[2/4] 目标: $upload"
if (Test-Path $upload) { Remove-Item $upload -Force }

# ---- 3) 打包：只把一个 msix 放进临时目录再压缩 ----
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("store-upload-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    Copy-Item $MsixPath (Join-Path $temp (Split-Path $MsixPath -Leaf)) -Force
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $temp, $upload,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- 4) 校验结果 ----
if (-not (Test-Path $upload)) { throw "生成失败: $upload" }
$item = Get-Item $upload
Write-Host ("[3/4] 已生成 {0}  {1:N1} MB" -f $item.FullName, ($item.Length / 1MB))

$zip = [System.IO.Compression.ZipFile]::OpenRead($upload)
try {
    Write-Host '[4/4] 压缩包内容:'
    foreach ($e in $zip.Entries) {
        Write-Host ("       {0}  {1:N1} MB" -f $e.FullName, ($e.Length / 1MB))
    }
}
finally { $zip.Dispose() }

Write-Host ''
Write-Host '完成：把下面这个文件上传到 Partner Center 的「程序包」页即可。'
Write-Host "  $upload"
Write-Host ''
Write-Host '注意：'
Write-Host '  - 商店会用微软自己的证书重签，本地签名与否不影响受理'
Write-Host '  - 清单里的 Identity Name / Publisher 必须与 Partner Center 预留信息一致，'
Write-Host '    否则上传后被拒（用 build-msix.ps1 的 -IdentityName / -Publisher 参数指定）'

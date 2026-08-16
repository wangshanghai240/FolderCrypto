# ===========================================================================
#  FolderCrypto - 自动把 packages\FolderCrypto.pfx 的证书指纹更新到 build-msi.ps1
#
#  用法：powershell -ExecutionPolicy Bypass -File wix\update-thumb.ps1
#  读取新 pfx 的证书指纹，并替换 wix\build-msi.ps1 中 $thumb 的值。
# ===========================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$pfx = Join-Path $root 'packages\FolderCrypto.pfx'
$build = Join-Path $root 'wix\build-msi.ps1'
$pass = $env:FOLDCRYPTO_PFX_PASS
if ([string]::IsNullOrEmpty($pass)) {
    Write-Error '请先设置环境变量 FOLDCRYPTO_PFX_PASS（pfx 密码）。'
    exit 1
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
$cert.Import($pfx, $pass, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)
$thumb = $cert.Thumbprint
Write-Host "新证书指纹: $thumb"

$content = [System.IO.File]::ReadAllText($build)
if ($content -match '\$thumb\s*=\s*''[0-9A-Fa-f]+''') {
    $content = [regex]::Replace($content, '\$thumb\s*=\s*''[0-9A-Fa-f]+''', "`$thumb = '$thumb'")
    [System.IO.File]::WriteAllText($build, $content, (New-Object System.Text.UTF8Encoding($true)))
    Write-Host "已更新 $build 中的 \$thumb 为 $thumb"
} else {
    Write-Error '未能在 build-msi.ps1 中找到 $thumb 赋值，请手动检查。'
    exit 1
}

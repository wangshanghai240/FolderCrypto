# ===========================================================================
#  FolderCrypto - 重新生成自签名代码签名证书并导出 pfx/cer
#
#  用途：签名 MSI / MSIX。生成：
#    - packages\FolderCrypto.pfx   (私钥+证书, 含新密码)
#    - packages\FolderCrypto.cer   (仅公钥证书, 可分发给用户信任)
#
#  用法(在项目根目录)：powershell -ExecutionPolicy Bypass -File wix\new-cert.ps1
#  新证书的 pfx 密码从环境变量 FOLDCRYPTO_PFX_PASS 读取（切勿写死在脚本里）。
#
#  注意：
#   - 重新生成会替换旧 pfx/cer。
#   - MSIX 签名证书改变后，旧证书信任失效，用户需信任新 .cer 才能安装新版 MSIX。
#   - 本机若已信任旧 FolderCrypto.cer，请在"受信任的根证书颁发机构"中删除旧的，
#     否则可能出现"已安装更新的版本"的冲突提示。
# ===========================================================================
param(
    [string]$SubjectName = "FolderCrypto 文件夹加密",
    [int]$Years = 10
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$pfx = Join-Path $root 'packages\FolderCrypto.pfx'
$cer = Join-Path $root 'packages\FolderCrypto.cer'
$pfxPass = $env:FOLDCRYPTO_PFX_PASS
if ([string]::IsNullOrEmpty($pfxPass)) {
    Write-Error '请先设置环境变量 FOLDCRYPTO_PFX_PASS 作为新证书的 pfx 密码，例如：$env:FOLDCRYPTO_PFX_PASS = "你的密码"'
    exit 1
}

Write-Host "==> 1/3 生成自签名代码签名证书（Subject: $SubjectName）..."
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $SubjectName `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy Exportable `
    -KeySpec Signature `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($Years)

$thumb = $cert.Thumbprint
Write-Host "    证书指纹(Thumbprint): $thumb"

Write-Host "==> 2/3 导出 pfx（含私钥）..."
$secure = ConvertTo-SecureString $pfxPass -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null

Write-Host "==> 3/3 导出 cer（公钥证书）..."
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

Write-Host ""
Write-Host "新证书已生成并导出："
Write-Host "  pfx  : $pfx"
Write-Host "  cer  : $cer"
Write-Host "  指纹 : $thumb"
Write-Host ""
Write-Host '请把上述指纹更新到 wix\build-msi.ps1 的 $thumb 变量。'
Write-Host '（也可直接运行 wix\update-thumb.ps1 自动更新）'

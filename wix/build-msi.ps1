# ===========================================================================
#  FolderCrypto - Build MSI installer (WiX v7)
#  Chains: trimmed publish -> stage -> generate .wxs -> wix build -> sign.
#
#  Usage:  powershell -ExecutionPolicy Bypass -File .\build-msi.ps1
#  Output: ..\packages\FolderCrypto-Setup-<version>-x64.msi  (signed)
# ===========================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$kits = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
$pfx  = Join-Path $root 'packages\FolderCrypto.pfx'

# 私钥密码从环境变量 FOLDCRYPTO_PFX_PASS 读取，切勿硬编码进脚本/提交到 git。
$pfxPass = $env:FOLDCRYPTO_PFX_PASS
if ([string]::IsNullOrEmpty($pfxPass)) {
    Write-Error '未设置环境变量 FOLDCRYPTO_PFX_PASS（PKCS#12 私钥密码）。签名步骤将失败。'
}

# 直接从 pfx 读取证书指纹，确保与签名证书一致（更换证书后无需手动改 $thumb）。
$signCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
try {
    $signCert.Import($pfx, $pfxPass, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)
    $thumb = $signCert.Thumbprint
    Write-Host "签名证书指纹: $thumb  (Subject: $($signCert.Subject))"
}
catch {
    Write-Error "无法读取签名证书 $pfx（密码可能不正确）：$($_.Exception.Message)"
}

$publish = 'C:\Temp\_fc_publish'
$stage   = 'C:\Temp\_wix_src'
$wxs     = Join-Path $root 'wix\FolderCrypto.wxs'

# 1) trimmed publish
Write-Host '==> 1/4 dotnet publish (trimmed) ...'
Remove-Item $publish,$stage -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'FolderCrypto.App\FolderCrypto.App.csproj') -c Release `
    -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:PublishTrimmed=true -p:TrimMode=full -o $publish | Out-Null

# 2) stage + strip pdbs
Write-Host '==> 2/4 staging files ...'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item "$publish\*" $stage -Recurse -Force
Get-ChildItem $stage -Recurse -Filter '*.pdb' | Remove-Item -Force

# 2b) 复制 Shell 集成所需文件到 staging 根目录
#     (native ATL DLL + 加密/解密右键图标)，gen-wxs 会将它们打包进 ShellIntegration 组件。
$nativeDll = Join-Path $root 'FolderCrypto.ShellNative\x64\Release\FolderCrypto.ShellNative.dll'
if (-not (Test-Path $nativeDll)) {
    # 兜底：packages 目录内的副本
    $nativeDll = Join-Path $root 'packages\FolderCrypto.ShellNative.dll'
}
$overlayIco = Join-Path $root 'FolderCrypto.ShellNative\x64\Release\overlay-lock.ico'
if (-not (Test-Path $overlayIco)) { $overlayIco = Join-Path $root 'packages\overlay-lock.ico' }
$unlockIco  = Join-Path $root 'packages\unlock.ico'

foreach ($f in @($nativeDll, $overlayIco, $unlockIco)) {
    if (-not (Test-Path $f)) { Write-Error "缺少 Shell 集成文件: $f"; exit 1 }
    Copy-Item $f $stage -Force
}
Write-Host "    copied shell files (native dll + icons) to staging."

# 3) generate wxs and build msi
Write-Host '==> 3/4 generating wxs + wix build ...'
powershell -ExecutionPolicy Bypass -File (Join-Path $root 'wix\gen-wxs.ps1') -SourceDir $stage -Out $wxs | Out-Null
$version = '1.0.0'
$manifest = Get-Content (Join-Path $root 'FolderCrypto.App\Package.appxmanifest') -Raw
if ($manifest -match 'Version="(\d+)\.(\d+)\.(\d+)') { $version = "$($matches[1]).$($matches[2]).$($matches[3])" }
$outMsi = Join-Path $root "packages\FolderCrypto-Setup-$version-x64.msi"
wix build $wxs -arch x64 -o $outMsi | Out-Null

# 4) sign (copy to temp to avoid in-place locks, then move back)
Write-Host '==> 4/4 signing MSI ...'
$tmp = 'C:\Temp\_fc_msi_tmp.msi'
Copy-Item $outMsi $tmp -Force

$signLog = & "$kits\signtool.exe" sign /fd SHA256 /f $pfx /p $pfxPass /sha1 $thumb $tmp 2>&1
if ($LASTEXITCODE -ne 0) {
    $signLog | Write-Host
    throw "signtool 签名失败（退出码 $LASTEXITCODE）。请检查 FOLDCRYPTO_PFX_PASS 密码、证书与安装的签名后再次尝试。"
}
$signLog | Write-Host

Copy-Item $tmp $outMsi -Force
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

# 校验签名。注意：自签名证书的根证书默认不被系统信任，
# 因此 signtool verify 会提示 "root not trusted"——这是自签名预期的现象，并非签名损坏。
# sign 步骤 exit 0 已证明签名成功写入；此处的 verify 仅用于提示，不因根不受信任而中止。
$oldEAP = $ErrorActionPreference
$ErrorActionPreference = 'Continue'   # signtool 写 stderr 会触发 NativeCommandError，需避免在 Stop 下中止
$verifyExit = 0
try {
    $verifyLog = & "$kits\signtool.exe" verify /pa $outMsi 2>&1
    $verifyExit = $LASTEXITCODE
    $verifyLog | Write-Host
}
catch {
    Write-Host "（verify 输出被忽略）"
}
$ErrorActionPreference = $oldEAP

if ($verifyExit -eq 0) {
    Write-Host "签名校验通过（证书链受信任）。"
}
else {
    Write-Host "提示：签名已写入，但证书链的根证书当前未被系统信任（自签名证书的正常现象）。"
    Write-Host "      请确保随包分发了 FolderCrypto.cer，用户首次安装需先信任该证书。"
}

Write-Host "DONE: $outMsi  ($([math]::Round((Get-Item $outMsi).Length/1MB,1)) MB)"

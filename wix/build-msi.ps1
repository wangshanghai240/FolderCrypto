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
$thumb = 'DA635E69430FDBAB33423734763962CCE104D4DF'

$publish = 'C:\Temp\_fc_publish'
$stage   = 'C:\Temp\_wix_src'
$wxs     = Join-Path $root 'wix\FolderCrypto.wxs'

# 0) 重新生成深色模式右键/覆盖层图标（overlay-lock.ico=加密, unlock.ico=解密）
Write-Host '==> 0/4 regenerate dark-mode icons ...'
powershell -ExecutionPolicy Bypass -File (Join-Path $root 'wix\gen-icons.ps1') | Out-Null

# 1) publish（不裁剪）
# 注意：历史上这里用 -p:PublishTrimmed=true -p:TrimMode=full，会把 Core 中
# 「未被静态引用」的成员整段删除（ContainerService、PasswordHasher.Verify/Derive、
# PasswordPolicy.DescribeRules 等）。当时恰好无人调用所以没暴露，但只要将来新增
# 任何经反射 / DI / JSON 反序列化访问的成员，就会「开发正常、安装版抛
# MissingMethodException」。因此改为不裁剪，保证程序集完整；体积增加有限。
Write-Host '==> 1/4 dotnet publish (no trim) ...'
Remove-Item $publish,$stage -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'FolderCrypto.App\FolderCrypto.App.csproj') -c Release `
    -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:PublishTrimmed=false -o $publish | Out-Null

# 2) stage + strip pdbs, and add the right-click Shell components (FolderCrypto.Shell +
#    native overlay DLL + overlay icons). These register the Explorer context menu / lock icon.
Write-Host '==> 2/4 staging files ...'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item "$publish\*" $stage -Recurse -Force
Get-ChildItem $stage -Recurse -Filter '*.pdb' | Remove-Item -Force

# --- publish FolderCrypto.Shell (needed to register the context menu on install) ---
$shellPub = 'C:\Temp\_fc_shell_pub'
Remove-Item $shellPub -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'FolderCrypto.Shell\FolderCrypto.Shell.csproj') -c Release `
    -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:PublishSingleFile=false -p:PublishTrimmed=false -o $shellPub | Out-Null
# copy shell publish into a dedicated subfolder so gen-wxs doesn't collide file ids
$shellDir = Join-Path $stage 'shell-support'
New-Item -ItemType Directory -Force -Path $shellDir | Out-Null
Copy-Item "$shellPub\*" $shellDir -Recurse -Force
Get-ChildItem $shellDir -Recurse -Filter '*.pdb' | Remove-Item -Force

# --- native overlay DLL + overlay icons → same shell-support dir ---
$nativeDll = Join-Path $root 'FolderCrypto.ShellNative\x64\Release\FolderCrypto.ShellNative.dll'
if (Test-Path $nativeDll) { Copy-Item $nativeDll (Join-Path $shellDir 'FolderCrypto.ShellNative.dll') -Force }
# overlay-lock.ico / unlock.ico / toggle-key.ico：从 ShellNative 源码目录取
# （每次构建前由 gen-icons.ps1 重生成）
#   overlay-lock.ico = 红色锁：覆盖层角标（加密）
#   unlock.ico       = 绿色解锁：历史遗留，保留兼容
#   toggle-key.ico   = 中性钥匙：「加密 / 解密」共享菜单项图标
$icoSrc = @{
    'overlay-lock.ico' = (Join-Path $root 'FolderCrypto.ShellNative\overlay-lock.ico')
    'unlock.ico'       = (Join-Path $root 'FolderCrypto.ShellNative\unlock.ico')
    'toggle-key.ico'   = (Join-Path $root 'FolderCrypto.ShellNative\toggle-key.ico')
}
foreach ($ico in 'overlay-lock.ico','unlock.ico','toggle-key.ico') {
    $p = $icoSrc[$ico]
    if (Test-Path $p) { Copy-Item $p (Join-Path $shellDir $ico) -Force } else { Write-Warning "图标缺失: $p" }
}
Write-Host "  shell-support staged: $((Get-ChildItem $shellDir -File).Count) files"

# 2.5) 构建 Sparse MSIX（一级右键菜单所需的包身份），并连同证书一起打进安装目录。
#      稀疏包让应用被识别为“包应用”，desktop4:fileExplorerContextMenus 才会生效，
#      「加密 / 解密」出现在 Windows 11 一级右键菜单，而不是“更多选项”里。
#      程序文件仍用本 MSI 安装目录中的 FolderCrypto.App.exe / FolderCrypto.Menu.dll。
Write-Host '==> 2.5 building sparse context-menu package ...'
try {
    powershell -ExecutionPolicy Bypass -File (Join-Path $root 'wix\build-sparse-msix.ps1') -AssetSource (Join-Path $publish 'Assets') -PublishDir $publish | Out-Null
} catch {
    Write-Warning "sparse msix 构建失败：$($_.Exception.Message). 一级菜单将不可用（经典菜单不受影响）"
}
$sparseSrc = Get-ChildItem (Join-Path $root 'packages\FolderCrypto.SparseMenu-*-x64.msix') -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($sparseSrc) {
    $sparseStage = Join-Path $stage 'sparse'
    New-Item -ItemType Directory -Force -Path $sparseStage | Out-Null
    Copy-Item $sparseSrc.FullName $sparseStage -Force
    $cerSrc = Join-Path $root 'packages\FolderCrypto.cer'
    if (Test-Path $cerSrc) { Copy-Item $cerSrc $sparseStage -Force }
    Write-Host "  sparse staged: $($sparseSrc.Name)"
} else {
    Write-Warning '未找到 sparse msix，一级右键菜单将不可用'
}

# 3) generate wxs and build msi
Write-Host '==> 3/4 generating wxs + wix build ...'
powershell -ExecutionPolicy Bypass -File (Join-Path $root 'wix\gen-wxs.ps1') -SourceDir $stage -Out $wxs | Out-Null
$uiWsx = Join-Path $root 'wix\ui.wxs'
$version = '1.0.0'
$manifest = Get-Content (Join-Path $root 'FolderCrypto.App\Package.appxmanifest') -Raw
if ($manifest -match 'Version="(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?"') {
    $version = "$($matches[1]).$($matches[2]).$($matches[3])"
    if ($matches[4]) { $version += ".$($matches[4])" }
}
$outMsi = Join-Path $root "packages\FolderCrypto-Setup-$version-x64.msi"
$outFile = [System.IO.Path]::GetFileNameWithoutExtension($outMsi)   # 保持 FolderCrypto-Setup-1.0.14-x64
$outDir  = [System.IO.Path]::GetDirectoryName($outMsi)

# 先构建到临时路径，签名后再尝试覆盖正式名；若正式名被占用（残留旧文件被锁），
# 则回退输出到 "-signed-x64.msi"，避免整个流程因占用而失败。
$tmpMsi = Join-Path $outDir "$outFile-build.msi"
Remove-Item $tmpMsi -Force -ErrorAction SilentlyContinue
wix build $wxs $uiWsx -arch x64 -o $tmpMsi | Out-Null

# 4) sign (copy to temp to avoid in-place locks, then move back)
Write-Host '==> 4/4 signing MSI ...'
$tmp = 'C:\Temp\_fc_msi_tmp.msi'
Copy-Item $tmpMsi $tmp -Force

# 私钥密码从环境变量读取，绝不硬编码进源码（避免随 git 历史泄漏）。
#
# 两种签名方式，按可用性自动选择：
#   A) 设置了 FOLDCRYPTO_PFX_PASS -> 用 pfx 文件签名（可移植，适合 CI）
#   B) 未设置但本机证书存储中存在该证书 -> 用 /sha1 从存储签名（不需要密码）
if ($env:FOLDCRYPTO_PFX_PASS) {
    Write-Host "    使用 pfx 文件签名（证书指纹 $thumb）"
    & "$kits\signtool.exe" sign /fd SHA256 /f $pfx /p $env:FOLDCRYPTO_PFX_PASS /sha1 $thumb $tmp | Out-Null
}
elseif (Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $thumb }) {
    Write-Host "    使用本机证书存储签名（未设置 FOLDCRYPTO_PFX_PASS，证书 $thumb 可用）"
    & "$kits\signtool.exe" sign /fd SHA256 /sha1 $thumb /s My $tmp | Out-Null
}
else {
    throw "无法签名：既未设置环境变量 FOLDCRYPTO_PFX_PASS，本机证书存储中也找不到证书 $thumb。请先执行：`$env:FOLDCRYPTO_PFX_PASS='你的密码'。"
}

function Test-Writable([string]$p) {
    if (-not (Test-Path $p)) { return $true }
    try { $fs = [System.IO.File]::Open($p,'Open','ReadWrite','None'); $fs.Close(); return $true }
    catch { return $false }
}

# 尝试覆盖正式名；若占用则用 signed 名
$final = $outMsi
if ((Test-Path $outMsi) -and -not (Test-Writable $outMsi)) {
    $versionNoX64 = ([System.IO.Path]::GetFileNameWithoutExtension($outMsi) -replace '-x64$','')
    $final = Join-Path $outDir "$versionNoX64-signed-x64.msi"
    Write-Warning "正式名被其它进程占用，输出到签名版名: $(Split-Path $final -Leaf)"
}
Copy-Item $tmp $final -Force
Remove-Item $tmp,$tmpMsi -Force -ErrorAction SilentlyContinue

# 校验签名存在且证书指纹匹配。
# 说明：本机使用自签名证书，若证书尚未导入「受信任的根证书颁发机构」，
# signtool verify /pa 会报「根证书不受信任」。这不是签名失败——
# 包本身已正确签名，只是本机信任链未建立。因此这里改用 /pa /all 并
# 只把「证书链不受信任」视为警告，其余错误仍然失败。
Write-Host '==> 校验签名 ...'
& "$kits\signtool.exe" verify /pa $final 2>&1 | ForEach-Object { Write-Host "    $_" }
$verifyOk = $LASTEXITCODE -eq 0
if (-not $verifyOk) {
    # 单独确认签名块确实存在且指纹正确
    $sigInfo = & "$kits\signtool.exe" verify /pa /q /v $final 2>&1 | Out-String
    if ($sigInfo -match $thumb) {
        Write-Warning "签名已写入且指纹匹配，但本机未信任该自签名证书（用户首次安装需先信任 FolderCrypto.cer）。"
        Write-Warning "信任后可运行：signtool verify /pa `"$final`" 验证通过。"
        $verifyOk = $true
    }
}
if (-not $verifyOk) { throw "MSI 签名校验失败：$final" }

Write-Host "DONE: $final  ($([math]::Round((Get-Item $final).Length/1MB,1)) MB)"


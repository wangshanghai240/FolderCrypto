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

# 1) trimmed publish
Write-Host '==> 1/4 dotnet publish (trimmed) ...'
Remove-Item $publish,$stage -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'FolderCrypto.App\FolderCrypto.App.csproj') -c Release `
    -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:PublishTrimmed=true -p:TrimMode=full -o $publish | Out-Null

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
# overlay-lock.ico 与 unlock.ico：从 ShellNative 源码目录取（每次构建前由 gen-icons.ps1 重生成）
$icoSrc = @{
    'overlay-lock.ico' = (Join-Path $root 'FolderCrypto.ShellNative\overlay-lock.ico')
    'unlock.ico'       = (Join-Path $root 'FolderCrypto.ShellNative\unlock.ico')
}
foreach ($ico in 'overlay-lock.ico','unlock.ico') {
    $p = $icoSrc[$ico]
    if (Test-Path $p) { Copy-Item $p (Join-Path $shellDir $ico) -Force } else { Write-Warning "图标缺失: $p" }
}
Write-Host "  shell-support staged: $((Get-ChildItem $shellDir -File).Count) files"

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
& "$kits\signtool.exe" sign /fd SHA256 /f $pfx /p 'p4svcWdTBJV0yCajUnto' /sha1 $thumb $tmp | Out-Null

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
& "$kits\signtool.exe" verify /pa $final
Write-Host "DONE: $final  ($([math]::Round((Get-Item $final).Length/1MB,1)) MB)"


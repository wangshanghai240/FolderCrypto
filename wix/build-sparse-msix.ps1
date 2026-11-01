# ===========================================================================
#  构建 FolderCrypto 的 Sparse MSIX（一级右键菜单用的包身份）
#
#  产物: packages\FolderCrypto.SparseMenu-<version>-x64.msix  （已签名）
#
#  依赖: makeappx.exe / signtool.exe（Windows Kits）
#        packages\FolderCrypto.pfx         （签名证书）
#        packages\FolderCrypto.cer         （对应公钥证书，安装时需被信任）
#        $env:FOLDCRYPTO_PFX_PASS          （pfx 密码；未设置则仅打不签名并警告）
#
#  说明: 该包是“稀疏包”，只含清单 + 小图标；真实程序文件通过
#        uap10:AllowExternalContent 指向 MSI 安装目录（-ExternalLocation）。
# ===========================================================================
[CmdletBinding()]
param(
    [string]$Root = '',
    # 图标来源目录（通常是 dotnet publish 输出里的 Assets）
    [string]$AssetSource = 'C:\Temp\_fc_publish\Assets',
    # 发布目录（含 FolderCrypto.App.exe / FolderCrypto.Menu.dll）
    [string]$PublishDir = 'C:\Temp\_fc_publish'
)
$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent (Split-Path -Parent $PSCommandPath) }

$kits  = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
$makeappx = Join-Path $kits 'makeappx.exe'
$signtool = Join-Path $kits 'signtool.exe'
$pfx   = Join-Path $Root 'packages\FolderCrypto.pfx'
$pfxPass = $env:FOLDCRYPTO_PFX_PASS

if (-not (Test-Path $makeappx)) { throw "找不到 makeappx.exe: $makeappx" }

# --- 版本：从 Package.appxmanifest 读取并规范为 4 段 ---
$manifestPath = Join-Path $Root 'FolderCrypto.App\Package.appxmanifest'
$m = [regex]::Match((Get-Content $manifestPath -Raw), 'Version="(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?"')
if (-not $m.Success) { throw "无法解析版本号: $manifestPath" }
$v = @($m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value)
if ($m.Groups[4].Success -and $m.Groups[4].Value -ne '') { $v += $m.Groups[4].Value } else { $v += '0' }
$pkgVer = ($v -join '.')                 # 4 段，用于 MSIX Identity
$fileVer = ($v[0..2] -join '.')          # 3 段，用于文件名
$outMsix = Join-Path $Root "packages\FolderCrypto.SparseMenu-$fileVer-x64.msix"

# --- 准备打包目录 ---
$packDir = 'C:\Temp\_fc_sparse\pkg'
Remove-Item $packDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $packDir | Out-Null

$template = Join-Path $PSScriptRoot 'sparse\AppxManifest.xml'
if (-not (Test-Path $template)) { throw "缺少清单模板: $template" }
$manifestXml = [System.IO.File]::ReadAllText($template, [System.Text.Encoding]::UTF8).Replace('__VERSION__', $pkgVer)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $packDir 'AppxManifest.xml'), $manifestXml, $utf8NoBom)

# --- 图标（包内 assets，必须存在；否则 makeappx/安装会失败）---
$assetsDir = Join-Path $packDir 'Assets'
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
$need = @('StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png')
$fallbackAssets = Join-Path $Root 'FolderCrypto.App\Assets'
foreach ($n in $need) {
    $src = Join-Path $AssetSource $n
    if (-not (Test-Path $src)) { $src = Join-Path $fallbackAssets $n }
    if (-not (Test-Path $src)) { throw "缺少图标: $n（在 $AssetSource 或 $fallbackAssets）" }
    Copy-Item $src (Join-Path $assetsDir $n) -Force
}

# --- 放入清单引用的文件本体（makeappx 校验要求其存在于打包目录）---
# 说明：稀疏包在注册时用 -ExternalLocation 指向真实安装目录，运行时使用外部文件；
#       这里放入副本只为让 makeappx 校验通过，不会被解压到 WindowsApps。
$needFiles = @('FolderCrypto.App.exe', 'FolderCrypto.Menu.dll')
foreach ($fn in $needFiles) {
    $src = Join-Path $PublishDir $fn
    if (-not (Test-Path $src)) { $src = Join-Path $Root "FolderCrypto.App\$fn" }
    if (-not (Test-Path $src)) { throw "缺少文件: $fn（在 $PublishDir 或 FolderCrypto.App）" }
    Copy-Item $src (Join-Path $packDir $fn) -Force
}

# --- pack ---Remove-Item $outMsix -Force -ErrorAction SilentlyContinue
& $makeappx pack /d $packDir /p $outMsix /o | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outMsix)) { throw "makeappx pack 失败（退出码 $LASTEXITCODE）" }

# --- sign ---
if ($pfxPass -and (Test-Path $pfx) -and (Test-Path $signtool)) {
    & $signtool sign /fd SHA256 /f $pfx /p $pfxPass /a $outMsix | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "sparse msix 签名失败（退出码 $LASTEXITCODE），注册将因证书不受信任而失败" }
    else { Write-Host "  sparse msix signed: $outMsix" }
} else {
    Write-Warning "未签名（缺少 FOLDCRYPTO_PFX_PASS / pfx / signtool）。该包未签名，Add-AppxPackage 会失败。"
}

Write-Host "   sparse package: $outMsix  (Identity v$pkgVer, file v$fileVer)"

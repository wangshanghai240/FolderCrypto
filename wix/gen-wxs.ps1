# ===========================================================================
#  FolderCrypto - WiX v7 source generator
#  Walks the staged self-contained publish folder and emits FolderCrypto.wxs
#  with a Component per file (preserving the directory tree), plus shortcuts,
#  uninstall registry entry and icon.
#
#  Usage:  powershell -File .\gen-wxs.ps1 -SourceDir <publish folder> [-Out <file.wxs>]
# ===========================================================================
param(
    [Parameter(Mandatory=$true)][string]$SourceDir,
    [string]$Out = (Join-Path $PSScriptRoot 'FolderCrypto.wxs')
)

$ErrorActionPreference = 'Stop'
$src = (Resolve-Path $SourceDir).Path

$guidSeed = 'FolderCrypto'
function New-MsiGuid {
    param([string]$s)
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("$guidSeed::$s"))
    return [guid]::new($bytes).ToString('B').ToUpperInvariant()
}

$counter = 0
$dirIdCounter = 0
$dirMap = @{ '' = 'INSTALLFOLDER' }
$children = @{}

function Get-DirId([string]$relDir) {
    if ($dirMap.ContainsKey($relDir)) { return $dirMap[$relDir] }
    $script:dirIdCounter++
    $id = "dir_$($script:dirIdCounter)"
    $dirMap[$relDir] = $id
    return $id
}

# Collect files
$all = @(Get-ChildItem $src -Recurse -File | ForEach-Object {
    $_.FullName.Substring($src.Length).TrimStart('\','/')
})

# Register all directories (incl ancestors)
foreach ($rel in $all) {
    $dir = [System.IO.Path]::GetDirectoryName($rel)
    if ([string]::IsNullOrEmpty($dir)) { continue }
    $parts = $dir -split '[\\/]'
    $cur = ''
    foreach ($p in $parts) {
        $cur = if ($cur -eq '') { $p } else { "$cur\$p" }
        [void](Get-DirId $cur)
    }
}
# Build parent->children
foreach ($k in $dirMap.Keys) {
    if ($k -eq '') { continue }
    $parent = [System.IO.Path]::GetDirectoryName($k)
    if (-not $parent) { $parent = '' }
    if (-not $children.ContainsKey($parent)) { $children[$parent] = @() }
    $children[$parent] += $k
}

function Emit-Dirs([string]$parentRel) {
    $out = ''
    if (-not $children.ContainsKey($parentRel)) { return $out }
    foreach ($child in ($children[$parentRel] | Sort-Object)) {
        $cid = Get-DirId $child
        $name = [System.IO.Path]::GetFileName($child)
        $out += "        <Directory Id=`"$cid`" Name=`"$name`">`n"
        $out += Emit-Dirs $child
        $out += "        </Directory>`n"
    }
    return $out
}

# Emit component for each file
$componentList = @()
foreach ($rel in ($all | Sort-Object)) {
    $script:counter++
    $dir = [System.IO.Path]::GetDirectoryName($rel)
    if (-not $dir) { $dir = '' }
    $dirId = Get-DirId $dir
    $abs = Join-Path $src $rel
    $componentList += @"
    <Component Id="cmp_$($script:counter)" Directory="$dirId" Guid="$(New-MsiGuid $rel)">
      <File Id="file_$($script:counter)" Source="$abs" KeyPath="yes" />
    </Component>
"@
}

$subDirs = Emit-Dirs ''
$componentXml = $componentList -join "`n"

# ===========================================================================
# 右键菜单注册表项（写 HKCU\Software\Classes，无需管理员）。
# 直接以 MSI <RegistryKey> 组件实现：安装时写入、卸载时自动删除，
# 完全替代 FolderCrypto.Shell.exe 的运行时注册（避免目标机依赖 .NET/自定义动作）。
# 对应 ContextMenuRegistrar.RegisterVerb 写出的精确键值。
# ===========================================================================
$encryptStateClsid = '{F8A2B000-1234-4A5B-9C6D-7E8F9A0B1C2D}'
$decryptStateClsid = '{F8A2C100-1234-4A5B-9C6D-7E8F9A0B1C2D}'
$appExe           = '[INSTALLFOLDER]FolderCrypto.App.exe'
$lockIcon        = '[INSTALLFOLDER]shell-support\overlay-lock.ico'
$unlockIcon      = '[INSTALLFOLDER]shell-support\unlock.ico'

# 每个条目: rootShell, verb, label, arg, icon(可为空), stateHandlerClsid(可为空)
$verbs = @(
    @('*\shell',               'FolderCryptoEncrypt',    '加密',     'encrypt',         $lockIcon,   $encryptStateClsid),
    @('*\shell',               'FolderCryptoDecrypt',    '解密',     'decrypt',         $unlockIcon, $decryptStateClsid),
    @('Directory\shell',       'FolderCryptoEncrypt',    '加密',     'encrypt',         $lockIcon,   $encryptStateClsid),
    @('Directory\shell',       'FolderCryptoDecrypt',    '解密',     'decrypt',         $unlockIcon, $decryptStateClsid),
    @('Directory\Background\shell', 'FolderCryptoEncrypt', '加密选中', 'encrypt-here',    $lockIcon,   '')
)

function To-FormattedPath([string]$p) { return $p }  # keep [INSTALLFOLDER] formatted as-is

$ctxMenuXml = ''
$ctxRefIds  = @()
$idx = 0
foreach ($v in $verbs) {
    $idx++
    $rootShell = $v[0]; $verb = $v[1]; $label = $v[2]; $arg = $v[3]; $icon = $v[4]; $clsid = $v[5]
    $safe = $rootShell -replace '[\\*]','_'
    $cid = "CtxMenu_$idx"
    $key = "Software\Classes\$rootShell\$verb"
    $cmd = "&quot;{0}&quot; {1} &quot;%1&quot;" -f $appExe, $arg   # double-quote command line

    $values = "        <RegistryValue Root=`"HKCU`" Key=`"$key`" Type=`"string`" Value=`"$label`" KeyPath=`"yes`" />`n"
    $values += "        <RegistryValue Root=`"HKCU`" Key=`"$key`" Name=`"MUIVerb`" Type=`"string`" Value=`"$label`" />`n"
    if ($icon) { $values += "        <RegistryValue Root=`"HKCU`" Key=`"$key`" Name=`"Icon`" Type=`"string`" Value=`"$icon`" />`n" }
    if ($clsid) { $values += "        <RegistryValue Root=`"HKCU`" Key=`"$key`" Name=`"CommandStateHandler`" Type=`"string`" Value=`"$clsid`" />`n" }
    $values += "        <RegistryValue Root=`"HKCU`" Key=`"$key\command`" Type=`"string`" Value=`"$cmd`" />`n"

    $ctxMenuXml += @"
    <Component Id="$cid" Guid="$(New-MsiGuid "ctx-$rootShell-$verb")" Directory="INSTALLFOLDER">
$values    </Component>

"@
    $ctxRefIds += "      <ComponentRef Id=`"$cid`" />"
}
$ctxRefs = $ctxRefIds -join "`n"

# 保留卸载时用于清理传统/旧版遗留的 RemoveRegistryKey（FolderCrypto.Shell 运行过但未跟踪的键）
$contextMenuKeys = @(
    'Software\Classes\*\shell\FolderCryptoEncrypt',
    'Software\Classes\*\shell\FolderCryptoDecrypt',
    'Software\Classes\Directory\shell\FolderCryptoEncrypt',
    'Software\Classes\Directory\shell\FolderCryptoDecrypt',
    'Software\Classes\Directory\Background\shell\FolderCryptoEncrypt'
)
$cleanupRemoves = $contextMenuKeys | ForEach-Object {
    "        <RemoveRegistryKey Root=`"HKCU`" Key=`"$_`" Action=`"removeOnUninstall`" />"
} | Out-String

$iconPath = (Get-ChildItem $src -Filter 'LockIcon.ico' -Recurse | Select-Object -First 1).FullName
if (-not $iconPath) { $iconPath = (Join-Path $PSScriptRoot '..\FolderCrypto.App\Assets\LockIcon.ico') }
$iconId = 'appIcon'

$version = '1.0.0'
$manifestVer = Get-Content (Join-Path $PSScriptRoot '..\FolderCrypto.App\Package.appxmanifest') -Raw
if ($manifestVer -match 'Version="(\d+)\.(\d+)\.(\d+)') {
    $version = "$($matches[1]).$($matches[2]).$($matches[3])"
}
$upgradeCode = [guid]::new([System.Security.Cryptography.MD5]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes('FolderCrypto.App.UpgradeCode'))).ToString('B').ToUpperInvariant()

$wxs = @"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="FolderCrypto 文件夹加密" Manufacturer="FolderCrypto"
           Version="$version" UpgradeCode="$upgradeCode"
           Scope="perMachine" Compressed="yes">
    <MajorUpgrade DowngradeErrorMessage="已安装的版本更新，请勿降级。" />
    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />

    <Icon Id="$iconId" SourceFile="$iconPath" />

        <StandardDirectory Id="TARGETDIR" />
    <StandardDirectory Id="ProgramFiles6432Folder">
<Directory Id="INSTALLFOLDER" Name="FolderCrypto">
$subDirs
      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="AppMenuFolder" Name="FolderCrypto">
        <Component Id="StartMenuShortcut" Guid="$(New-MsiGuid 'startmenu')">
          <Shortcut Id="StartMenuAppShortcut" Name="FolderCrypto"
                    Description="文件夹/文件加密工具"
                    Target="[INSTALLFOLDER]FolderCrypto.App.exe"
                    WorkingDirectory="INSTALLFOLDER" Icon="$iconId" />
          <RemoveFolder Id="RemoveAppMenuFolder" Directory="AppMenuFolder" On="uninstall" />
          <RegistryValue Root="HKCU" Key="Software\FolderCrypto" Name="installed" Type="integer" Value="1" KeyPath="yes" />
        </Component>
      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="DesktopFolder">
      <Component Id="DesktopShortcut" Guid="$(New-MsiGuid 'desktop')">
        <Shortcut Id="DesktopAppShortcut" Name="FolderCrypto"
                  Description="文件夹/文件加密工具"
                  Target="[INSTALLFOLDER]FolderCrypto.App.exe"
                  WorkingDirectory="INSTALLFOLDER" Icon="$iconId" />
        <RegistryValue Root="HKCU" Key="Software\FolderCrypto" Name="desktop" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </StandardDirectory>

    <!-- 卸载时清理右键菜单残留（HKCU，无需管理员）。 -->
    <Component Id="ContextMenuCleanup" Guid="$(New-MsiGuid 'contextmenu-cleanup')" Directory="INSTALLFOLDER">
$cleanupRemoves    </Component>

    <!-- 安装时写入的右键菜单注册表项（对应 ContextMenuRegistrar，安装即注册、卸载即删除）。 -->
$ctxMenuXml
    <Property Id="ARPPRODUCTICON" Value="$iconId" />

    <!-- 引入自定义安装 UI（ui.wxs，含目录选择） -->
    <UI>
      <UIRef Id="FcWixUI" />
    </UI>

    <Feature Id="MainFeature" Title="FolderCrypto" Level="1">
      <ComponentGroupRef Id="ProductComponents" />
$ctxRefs
      <ComponentRef Id="StartMenuShortcut" />
      <ComponentRef Id="DesktopShortcut" />
      <ComponentRef Id="ContextMenuCleanup" />
    </Feature>
  </Package>

  <Fragment>
    <ComponentGroup Id="ProductComponents">
$componentXml
    </ComponentGroup>
  </Fragment>
</Wix>
"@

Set-Content -Path $Out -Value $wxs -Encoding UTF8
Write-Host "Wrote $Out"
Write-Host "dirs: $($dirMap.Count), files: $($all.Count)"


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

# Shell 集成相关文件：由固定组件 ShellIntegration 统一引用（避免同一文件被两个组件引用导致编译错误），
# 因此在这里从自动遍历中排除。
$excludedFiles = @(
    'FolderCrypto.ShellNative.dll',
    'overlay-lock.ico',
    'unlock.ico'
)

# Collect files
$all = @(Get-ChildItem $src -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length).TrimStart('\','/')
    $name = Split-Path $rel -Leaf
    if ($excludedFiles -contains $name) { return }   # 跳过由 ShellIntegration 引用的文件
    $rel
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

# ---- Shell 集成固定组件：右键菜单 + 锁图标覆盖层 ----
# 采用 WiX 原生注册表声明（不依赖运行时/自定义动作），安装与卸载均由 MSI 管理。
# 右键菜单注册到 HKLM\Software\Classes（机器级，对所有用户生效）。
$nativeDll    = Join-Path $src 'FolderCrypto.ShellNative.dll'
$overlayIco   = Join-Path $src 'overlay-lock.ico'
$unlockIco    = Join-Path $src 'unlock.ico'

$overlayClsid    = 'F8A2C000-1234-4A5B-9C6D-7E8F9A0B1C2D'
$overlayKeyName  = '  FolderCryptoLock'   # 前导空格确保覆盖层排序靠前

$shellXml = @"
    <Component Id="ShellIntegration" Directory="INSTALLFOLDER" Guid="$(New-MsiGuid 'shell-integration')">
      <File Id="shell_native_dll"  Source="$nativeDll"  KeyPath="yes" />
      <File Id="shell_overlay_ico" Source="$overlayIco" />
      <File Id="shell_unlock_ico"  Source="$unlockIco" />

      <!-- 右键菜单：文件级 加密/解密 -->
      <RegistryKey Root="HKLM" Key="Software\Classes\*\shell\FolderCryptoEncrypt">
        <RegistryValue Type="string" Value="加密" />
        <RegistryValue Name="MUIVerb" Type="string" Value="加密" />
        <RegistryValue Name="Icon" Type="string" Value="[#shell_overlay_ico]" />
        <RegistryKey Key="command">
          <RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]FolderCrypto.App.exe&quot; encrypt &quot;%1&quot;" />
        </RegistryKey>
      </RegistryKey>
      <RegistryKey Root="HKLM" Key="Software\Classes\*\shell\FolderCryptoDecrypt">
        <RegistryValue Type="string" Value="解密" />
        <RegistryValue Name="MUIVerb" Type="string" Value="解密" />
        <RegistryValue Name="Icon" Type="string" Value="[#shell_unlock_ico]" />
        <RegistryKey Key="command">
          <RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]FolderCrypto.App.exe&quot; decrypt &quot;%1&quot;" />
        </RegistryKey>
      </RegistryKey>

      <!-- 右键菜单：文件夹级 加密/解密 -->
      <RegistryKey Root="HKLM" Key="Software\Classes\Directory\shell\FolderCryptoEncrypt">
        <RegistryValue Type="string" Value="加密" />
        <RegistryValue Name="MUIVerb" Type="string" Value="加密" />
        <RegistryValue Name="Icon" Type="string" Value="[#shell_overlay_ico]" />
        <RegistryKey Key="command">
          <RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]FolderCrypto.App.exe&quot; encrypt &quot;%1&quot;" />
        </RegistryKey>
      </RegistryKey>
      <RegistryKey Root="HKLM" Key="Software\Classes\Directory\shell\FolderCryptoDecrypt">
        <RegistryValue Type="string" Value="解密" />
        <RegistryValue Name="MUIVerb" Type="string" Value="解密" />
        <RegistryValue Name="Icon" Type="string" Value="[#shell_unlock_ico]" />
        <RegistryKey Key="command">
          <RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]FolderCrypto.App.exe&quot; decrypt &quot;%1&quot;" />
        </RegistryKey>
      </RegistryKey>

      <!-- 右键菜单：文件夹空白处 加密选中 -->
      <RegistryKey Root="HKLM" Key="Software\Classes\Directory\Background\shell\FolderCryptoEncrypt">
        <RegistryValue Type="string" Value="加密选中" />
        <RegistryValue Name="MUIVerb" Type="string" Value="加密选中" />
        <RegistryValue Name="Icon" Type="string" Value="[#shell_overlay_ico]" />
        <RegistryKey Key="command">
          <RegistryValue Type="string" Value="&quot;[INSTALLFOLDER]FolderCrypto.App.exe&quot; encrypt-here &quot;%V&quot;" />
        </RegistryKey>
      </RegistryKey>

      <!-- 锁图标覆盖层：native DLL 的 COM 类注册（HKLM\Software\Classes\CLSID） -->
      <RegistryKey Root="HKLM" Key="Software\Classes\CLSID\{$overlayClsid}">
        <RegistryValue Type="string" Value="FolderCrypto Lock Overlay Handler" />
        <RegistryValue Name="AppID" Type="string" Value="{$overlayClsid}" />
        <RegistryKey Key="InprocServer32">
          <RegistryValue Type="string" Value="[#shell_native_dll]" />
          <RegistryValue Name="ThreadingModel" Type="string" Value="Apartment" />
        </RegistryKey>
        <RegistryKey Key="TypeLib">
          <RegistryValue Type="string" Value="{C8A2C000-1234-4A5B-9C6D-7E8F9A0B1C2D}" />
        </RegistryKey>
        <RegistryKey Key="Version">
          <RegistryValue Type="string" Value="1.0" />
        </RegistryKey>
      </RegistryKey>

      <!-- 注册 Shell 覆盖层标识 -->
      <RegistryKey Root="HKLM" Key="Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\$overlayKeyName">
        <RegistryValue Type="string" Value="{$overlayClsid}" />
      </RegistryKey>
    </Component>
"@

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

    <Property Id="ARPPRODUCTICON" Value="$iconId" />

    <Feature Id="MainFeature" Title="FolderCrypto" Level="1">
      <ComponentGroupRef Id="ProductComponents" />
      <ComponentRef Id="StartMenuShortcut" />
      <ComponentRef Id="DesktopShortcut" />
      <ComponentRef Id="ShellIntegration" />
    </Feature>
  </Package>

  <Fragment>
    <ComponentGroup Id="ProductComponents">
$componentXml
    </ComponentGroup>

$shellXml
  </Fragment>
</Wix>
"@

Set-Content -Path $Out -Value $wxs -Encoding UTF8
Write-Host "Wrote $Out"
Write-Host "dirs: $($dirMap.Count), files: $($all.Count)"

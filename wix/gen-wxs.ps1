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

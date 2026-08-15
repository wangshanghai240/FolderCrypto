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
$thumb = 'E25B41DD0AF64FACFBE19EB8FF277060E15A8463'

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
& "$kits\signtool.exe" sign /fd SHA256 /f $pfx /p 'FolderCrypto_Pfx_Pass2026!' /sha1 $thumb $tmp | Out-Null
Copy-Item $tmp $outMsi -Force
Remove-Item $tmp -Force -ErrorAction SilentlyContinue
& "$kits\signtool.exe" verify /pa $outMsi
Write-Host "DONE: $outMsi  ($([math]::Round((Get-Item $outMsi).Length/1MB,1)) MB)"

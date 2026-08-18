# ===========================================================================
#  FolderCrypto - perUser 诊断构建 + 自动化安装（定位 2343 / UI 消失）
#  1) publish 2) stage 3) gen perUser wxs 4) wix build 5) msiexec /l*v 自动跑
#  输出: C:\Temp\_fc_diag_i.log   (详细安装日志)
# ===========================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$publish = 'C:\Temp\_fc_diag_publish'
$stage   = 'C:\Temp\_fc_diag_src'
$wxs     = 'C:\Temp\_fc_diag_src\FolderCrypto.wxs'

Remove-Item $publish,$stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish,$stage | Out-Null

Write-Host '==> publish (trimmed) ...'
dotnet publish (Join-Path $root 'FolderCrypto.App\FolderCrypto.App.csproj') -c Release `
    -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:PublishTrimmed=true -p:TrimMode=full -o $publish | Out-Null

Write-Host '==> stage ...'
Copy-Item "$publish\*" $stage -Recurse -Force
Get-ChildItem $stage -Recurse -Filter '*.pdb' | Remove-Item -Force

Write-Host '==> gen perUser wxs ...'
powershell -ExecutionPolicy Bypass -File (Join-Path $root 'wix\gen-wxs.diag.ps1') -SourceDir $stage -Out $wxs | Out-Null

Write-Host '==> wix build ...'
$msi = 'C:\Temp\_fc_diag_peruser.msi'
Remove-Item $msi -Force -ErrorAction SilentlyContinue
wix build $wxs (Join-Path $root 'wix\ui.wxs') -arch x64 -o $msi
Write-Host "MSI built: $msi  ($([math]::Round((Get-Item $msi).Length/1MB,1)) MB)"

Write-Host '==> auto-install with verbose log ...'
$log = 'C:\Temp\_fc_diag_i.log'
Remove-Item $log -Force -ErrorAction SilentlyContinue
Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /l*v `"$log`"" -Wait -NoNewWindow
Write-Host "exit done. log: $log"
Write-Host ('--- key lines ---')
Select-String -Path $log -Pattern '2343|Return value 3|Error|INSTALLFOLDER|SetTargetPath|DirectoryCombo' -ErrorAction SilentlyContinue | Select-Object -Last 40 | ForEach-Object { $_.Line } | Out-String | Write-Host

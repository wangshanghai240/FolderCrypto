# ===========================================================================
#  FolderCrypto - Build self-developed .NET (WPF) install bootstrapper
#  Chains: build+sign MSI -> embed into bootstrapper -> publish self-contained
#          single-file WPF exe -> sign -> copy to packages.
#
#  Usage:  powershell -ExecutionPolicy Bypass -File .\build-bootstrapper.ps1
#  Output: ..\packages\FolderCrypto-Setup-<version>-x64.exe  (single-file, signed)
# ===========================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$kits  = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
$pfx   = Join-Path $root 'packages\FolderCrypto.pfx'
$thumb = 'DA635E69430FDBAB33423734763962CCE104D4DF'
$pass  = 'p4svcWdTBJV0yCajUnto'

# 1) build + sign the MSI (reuses wix\build-msi.ps1)
Write-Host '==> 1/5 building + signing MSI ...'
powershell -ExecutionPolicy Bypass -File (Join-Path $root 'wix\build-msi.ps1')

# 2) pick the newest MSI and derive version
$msi = Get-ChildItem (Join-Path $root 'packages\FolderCrypto-Setup-*.msi') |
       Where-Object { $_.Name -notmatch 'wixpdb' } |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw '未找到 MSI 产物，请先构建 MSI。' }
$version = '1.0.0'
if ($msi.Name -match 'FolderCrypto-Setup-(\d+\.\d+\.\d+)') { $version = $matches[1] }
Write-Host "   using MSI: $($msi.Name)  (v$version)"

# 3) embed the MSI into the bootstrapper project (build-bootstrapper embeds it)
Write-Host '==> 2/5 embedding MSI ...'
$embedded = Join-Path $root 'FolderCrypto.Bootstrapper\Embedded\FolderCrypto-Setup.msi'
Copy-Item $msi.FullName $embedded -Force
Write-Host "   embedded: $([math]::Round((Get-Item $embedded).Length/1MB,1)) MB"

# 4) publish self-contained single-file WPF exe
Write-Host '==> 3/5 publishing bootstrapper (self-contained single-file) ...'
$pubOut = 'C:\Temp\_fc_bootstrapper_pub'
Remove-Item $pubOut -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'FolderCrypto.Bootstrapper\FolderCrypto.Bootstrapper.csproj') -c Release `
    -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $pubOut | Out-Null

$exe = Join-Path $pubOut 'FolderCrypto.Setup.exe'
if (-not (Test-Path $exe)) { throw '发布失败：未找到 FolderCrypto.Setup.exe' }

# 5) sign the exe (copy to temp, sign, move back)
Write-Host '==> 4/5 signing exe ...'
$tmpExe = 'C:\Temp\_fc_bootstrapper_signed.exe'
Copy-Item $exe $tmpExe -Force
& "$kits\signtool.exe" sign /fd SHA256 /f $pfx /p $pass /sha1 $thumb $tmpExe | Out-Null
Copy-Item $tmpExe $exe -Force
Remove-Item $tmpExe -Force -ErrorAction SilentlyContinue

# 6) copy to packages with proper name
$outExe = Join-Path $root "packages\FolderCrypto-Setup-$version-x64.exe"
Copy-Item $exe $outExe -Force
& "$kits\signtool.exe" verify /pa $outExe
Write-Host "DONE: $outExe  ($([math]::Round((Get-Item $outExe).Length/1MB,1)) MB)"

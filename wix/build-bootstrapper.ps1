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

# 签名：优先用 pfx 文件（需要环境变量密码），否则回退到本机证书存储。
function Sign-File([string]$path) {
    if ($env:FOLDCRYPTO_PFX_PASS) {
        & "$kits\signtool.exe" sign /fd SHA256 /f $pfx /p $env:FOLDCRYPTO_PFX_PASS /sha1 $thumb $path | Out-Null
    }
    elseif (Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $thumb }) {
        & "$kits\signtool.exe" sign /fd SHA256 /sha1 $thumb /s My $path | Out-Null
    }
    else {
        throw "无法签名：既未设置 FOLDCRYPTO_PFX_PASS，本机也找不到证书 $thumb。"
    }
}

# 1) build + sign the MSI (reuses wix\build-msi.ps1)
Write-Host '==> 1/5 building + signing MSI ...'
powershell -ExecutionPolicy Bypass -File (Join-Path $root 'wix\build-msi.ps1')

# 2) 选取 MSI：必须与当前 Package.appxmanifest 的版本号一致。
#    早期实现按「修改时间最新」取，一旦某次 MSI 构建失败，就会静默取到旧版本，
#    打出「版本号不符」的安装包却毫无提示。现在改为按版本号精确匹配并严格校验。
$manifestPath = Join-Path $root 'FolderCrypto.App\Package.appxmanifest'
$expectedVer = $null
if ((Get-Content $manifestPath -Raw) -match 'Version="(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?"') {
    $expectedVer = "$($matches[1]).$($matches[2]).$($matches[3])"
}
if (-not $expectedVer) { throw "无法从 Package.appxmanifest 解析版本号：$manifestPath" }

$expectedMsi = Join-Path $root "packages\FolderCrypto-Setup-$expectedVer-x64.msi"
if (-not (Test-Path $expectedMsi)) {
    throw "未找到与当前版本匹配的 MSI：$expectedMsi`n请先确认 wix\build-msi.ps1 构建成功（版本 $expectedVer）。"
}
$msi = Get-Item $expectedMsi
$version = $expectedVer
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
Sign-File $tmpExe
Copy-Item $tmpExe $exe -Force
Remove-Item $tmpExe -Force -ErrorAction SilentlyContinue

# 6) copy to packages with proper name
$outExe = Join-Path $root "packages\FolderCrypto-Setup-$version-x64.exe"
Copy-Item $exe $outExe -Force
& "$kits\signtool.exe" verify /pa $outExe
if ($LASTEXITCODE -ne 0) {
    Write-Warning "exe 已签名（指纹 $thumb），但本机未信任该自签名证书；用户首次安装需先信任 FolderCrypto.cer。"
}
Write-Host "DONE: $outExe  ($([math]::Round((Get-Item $outExe).Length/1MB,1)) MB)"

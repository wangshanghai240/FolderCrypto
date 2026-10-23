# =============================================================================
#  修复文件夹锁图标（覆盖层 / 角标）不显示的问题
#
#  必须以管理员身份运行。
#
#  根因：ShellIconOverlayIdentifiers 写在 HKLM，但对应的 COM 类
#        {F8A2C000-...} 只注册在 HKCU\Software\Classes\CLSID。
#        Explorer 在查找覆盖层处理程序时按的是 HKLM 的 CLSID 视图，
#        在 HKCU 的注册不会被采用，于是角标永远不显示。
#        实测对比：机器上所有能正常工作的覆盖层
#        （IDM / AccExtIco1-3 / EnhancedStorageShell / Offline Files）
#        的 CLSID 全部注册在 HKLM，只有本项目的在 HKCU。
#
#  用法（管理员 PowerShell）：
#    powershell -ExecutionPolicy Bypass -File "F:\VScode\文件夹加密\fix-overlay-hklm.ps1"
# =============================================================================

$ErrorActionPreference = 'Stop'

# 检查管理员权限
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[错误] 需要管理员权限。请右键 PowerShell 选择「以管理员身份运行」后重试。" -ForegroundColor Red
    exit 1
}

$dll = 'D:\FolderCrypto\shell-support\FolderCrypto.ShellNative.dll'
if (-not (Test-Path $dll)) { throw "DLL 不存在：$dll" }

$overlayClsid = '{F8A2C000-1234-4A5B-9C6D-7E8F9A0B1C2D}'
$encryptClsid = '{F8A2B000-1234-4A5B-9C6D-7E8F9A0B1C2D}'
$decryptClsid = '{F8A2C100-1234-4A5B-9C6D-7E8F9A0B1C2D}'

$defs = @(
    @{ Clsid = $overlayClsid; Label = 'FolderCrypto Lock Overlay Handler' },
    @{ Clsid = $encryptClsid; Label = 'FolderCrypto Encrypt Menu Command Handler' },
    @{ Clsid = $decryptClsid; Label = 'FolderCrypto Decrypt Menu Command Handler' }
)

# ---- 1) 在 HKLM 注册 COM 类（覆盖层必需）----
Write-Host "==> 1/3 注册 COM 类到 HKLM ..."
foreach ($d in $defs) {
    $base = "HKLM:\Software\Classes\CLSID\$($d.Clsid)"
    New-Item -Path $base -Force | Out-Null
    Set-ItemProperty -Path $base -Name '(default)' -Value $d.Label

    $inproc = "$base\InprocServer32"
    New-Item -Path $inproc -Force | Out-Null
    Set-ItemProperty -Path $inproc -Name '(default)' -Value $dll
    Set-ItemProperty -Path $inproc -Name 'ThreadingModel' -Value 'Apartment'
    Write-Host "    [OK] HKLM\Software\Classes\CLSID\$($d.Clsid)"
}

# ---- 2) 确认覆盖层登记（前导两个空格保证排序靠前）----
Write-Host "==> 2/3 确认覆盖层登记 ..."
$ovRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers'
$ovKey = "$ovRoot\  FolderCryptoLock"
New-Item -Path $ovKey -Force | Out-Null
Set-ItemProperty -Path $ovKey -Name '(default)' -Value $overlayClsid

# 清理旧版无前导空格的重复项
if (Test-Path "$ovRoot\FolderCryptoLock") {
    Remove-Item "$ovRoot\FolderCryptoLock" -Recurse -Force
    Write-Host "    [清理] 旧的无前导空格项"
}
Write-Host "    [OK] '$ovKey' -> $overlayClsid"

# ---- 3) 显示当前覆盖层排序（确认在前 15 位内）----
Write-Host "==> 3/3 当前覆盖层顺序（Windows 只显示前 15 个）"
$i = 0
Get-ChildItem $ovRoot -ErrorAction SilentlyContinue | ForEach-Object {
    $i++
    $mark = if ($_.PSChildName -match 'FolderCrypto') { '  <== 本项目' } else { '' }
    $flag = if ($i -le 15) { '可见' } else { '超出限制' }
    Write-Host ("    {0,2}. [{1}] {2}{3}" -f $i, $flag, $_.PSChildName, $mark)
}

# ---- 4) 重启 Explorer 让覆盖层生效 ----
Write-Host ""
Write-Host "==> 重启 Explorer ..."
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host ""
Write-Host "[完成] 覆盖层已注册到 HKLM。" -ForegroundColor Green
Write-Host "       请对任意文件夹执行一次「加密 / 解密」加密，
       加密后的文件夹应出现红色锁角标。"
Write-Host ""
Write-Host "如仍未显示，请注销并重新登录（覆盖层缓存需要重建）。"

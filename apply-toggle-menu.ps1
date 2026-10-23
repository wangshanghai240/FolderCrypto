# =============================================================================
#  应用「单一共享菜单项」方案（兼容 Pulse 等第三方文件管理器）
#
#  背景：Pulse 只枚举注册表静态 \shell\verb 项，不支持 IExplorerCommand
#        动态显隐，也不支持 AppliesTo / SubCommands。
#        因此「加密」「解密」两个静态项必然同时出现，无法靠 COM 状态区分。
#
#  方案：改成一个菜单项「加密 / 解密」，点击后由程序检测目标状态，
#        自动决定弹加密窗口还是解密窗口。
#
#  用法：powershell -ExecutionPolicy Bypass -File "F:\VScode\文件夹加密\apply-toggle-menu.ps1"
#  回滚：powershell -ExecutionPolicy Bypass -File "F:\VScode\文件夹加密\revert-toggle-menu.ps1"
# =============================================================================

$ErrorActionPreference = 'Stop'

$exe = 'D:\FolderCrypto\FolderCrypto.App.exe'
if (-not (Test-Path $exe)) { throw "主程序不存在：$exe" }

$lockIcon = 'D:\FolderCrypto\shell-support\overlay-lock.ico'

# ---- 1) 备份原始菜单键 ----
$backupDir = 'C:\Temp\_fc_menu_backup'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

$oldKeys = @(
    'Software\Classes\*\shell\FolderCryptoEncrypt',
    'Software\Classes\*\shell\FolderCryptoDecrypt',
    'Software\Classes\Directory\shell\FolderCryptoEncrypt',
    'Software\Classes\Directory\shell\FolderCryptoDecrypt'
)

foreach ($k in $oldKeys) {
    $safe = ($k -replace '[\\*]', '_')
    $out = Join-Path $backupDir "$safe.reg"
    & reg export "HKCU\$k" $out /y 2>$null | Out-Null
}
Write-Host "[OK] 已备份原菜单项到 $backupDir"

# ---- 2) 删除两个独立项 ----
foreach ($k in $oldKeys) {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($k, $false)
    Write-Host "[删除] HKCU\$k"
}

# ---- 3) 写入单一共享项（文件 + 文件夹各一份）----
$targets = @(
    @{ Shell = 'Software\Classes\*\shell';         Label = '加密 / 解密' },
    @{ Shell = 'Software\Classes\Directory\shell'; Label = '加密 / 解密' }
)

foreach ($t in $targets) {
    $key = "$($t.Shell)\FolderCryptoToggle"

    $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($key)
    $k.SetValue('', $t.Label)
    $k.SetValue('MUIVerb', $t.Label)
    $k.SetValue('Icon', $lockIcon)
    $k.Close()

    $ck = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$key\command")
    $ck.SetValue('', ('"' + $exe + '" toggle "%1"'))
    $ck.Close()

    Write-Host "[写入] HKCU\$key"
    Write-Host "       command = `"$exe`" toggle `"%1`""
}

# ---- 4) 通知外壳刷新 ----
if (-not ([System.Management.Automation.PSTypeName]'Win32.ShNotify').Type) {
    Add-Type -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);' -Name ShNotify -Namespace Win32
}
[Win32.ShNotify]::SHChangeNotify(0x8000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host ""
Write-Host "[完成] 现在右键菜单只显示一个「加密 / 解密」项。" -ForegroundColor Green
Write-Host "       点击后程序检测状态：未加密 -> 加密窗口；已加密 -> 解密窗口。"
Write-Host "       如需回滚，运行 revert-toggle-menu.ps1"

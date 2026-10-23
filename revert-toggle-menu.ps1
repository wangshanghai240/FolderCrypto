# =============================================================================
#  回滚「单一共享菜单项」，恢复为「加密 / 解密」两个独立菜单项
#
#  用法：powershell -ExecutionPolicy Bypass -File "F:\VScode\文件夹加密\revert-toggle-menu.ps1"
# =============================================================================

$ErrorActionPreference = 'Stop'

$exe = 'D:\FolderCrypto\FolderCrypto.App.exe'
$lockIcon = 'D:\FolderCrypto\shell-support\overlay-lock.ico'
$unlockIcon = 'D:\FolderCrypto\shell-support\unlock.ico'

if (-not (Test-Path $exe)) { throw "主程序不存在：$exe" }

# 删除单一共享项
foreach ($p in @('Software\Classes\*\shell\FolderCryptoToggle',
                 'Software\Classes\Directory\shell\FolderCryptoToggle')) {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($p, $false)
    Write-Host "[删除] HKCU\$p"
}

# 恢复两个独立项
$verbs = @(
    @{ Shell = 'Software\Classes\*\shell';         Verb = 'FolderCryptoEncrypt'; Label = '加密'; Arg = 'encrypt'; Icon = $lockIcon },
    @{ Shell = 'Software\Classes\*\shell';         Verb = 'FolderCryptoDecrypt'; Label = '解密'; Arg = 'decrypt'; Icon = $unlockIcon },
    @{ Shell = 'Software\Classes\Directory\shell'; Verb = 'FolderCryptoEncrypt'; Label = '加密'; Arg = 'encrypt'; Icon = $lockIcon },
    @{ Shell = 'Software\Classes\Directory\shell'; Verb = 'FolderCryptoDecrypt'; Label = '解密'; Arg = 'decrypt'; Icon = $unlockIcon }
)

foreach ($v in $verbs) {
    $key = "$($v.Shell)\$($v.Verb)"
    $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($key)
    $k.SetValue('', $v.Label)
    $k.SetValue('MUIVerb', $v.Label)
    $k.SetValue('Icon', $v.Icon)
    $k.Close()

    $ck = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$key\command")
    $ck.SetValue('', ('"' + $exe + '" ' + $v.Arg + ' "%1"'))
    $ck.Close()

    Write-Host "[写入] HKCU\$key"
}

if (-not ([System.Management.Automation.PSTypeName]'Win32.ShNotifyR').Type) {
    Add-Type -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);' -Name ShNotifyR -Namespace Win32
}
[Win32.ShNotifyR]::SHChangeNotify(0x8000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host ""
Write-Host "[完成] 已还原为双菜单项。" -ForegroundColor Yellow
Write-Host "       注意：在 Pulse 等不支持 IExplorerCommand 的外壳中，"
Write-Host "       两个项会同时显示（这是该方案已知的局限）。"

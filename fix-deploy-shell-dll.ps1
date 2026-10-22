# =============================================================================
#  FolderCrypto 修复部署脚本（需以管理员运行一次，然后重启电脑）
#
#  用途：替换被 Explorer / PowerToys / Pulse 占用的原生 Shell 扩展 DLL。
#        这些进程会一直加载 FolderCrypto.ShellNative.dll，无法在运行时覆盖。
#
#  用法：右键“以管理员身份运行 PowerShell”，然后执行
#          powershell -ExecutionPolicy Bypass -File "F:\VScode\文件夹加密\fix-deploy-shell-dll.ps1"
# =============================================================================

$ErrorActionPreference = 'Stop'

$target = 'D:\FolderCrypto\shell-support\FolderCrypto.ShellNative.dll'
$staged = 'D:\FolderCrypto\shell-support\FolderCrypto.ShellNative.new.dll'
$backup = 'D:\FolderCrypto\shell-support\FolderCrypto.ShellNative.old.dll'

if (-not (Test-Path $staged)) {
    Write-Host "[错误] 暂存文件不存在：$staged" -ForegroundColor Red
    exit 1
}

Write-Host "目标 DLL : $target"
Write-Host ("新 DLL   : $staged ({0} bytes)" -f (Get-Item $staged).Length)
Write-Host ""

# 先把当前（旧）DLL 复制一份作为回滚备份
if (-not (Test-Path $backup)) {
    try {
        Copy-Item $target $backup -Force
        Write-Host "[OK] 已创建回滚备份：$backup"
    } catch {
        Write-Host "[警告] 无法创建备份（文件被占用），跳过。" -ForegroundColor Yellow
    }
}

# 注册重启后替换操作
#   PendingFileRenameOperations 格式（REG_MULTI_SZ）：
#     \??\<源路径>  ->  空字符串         表示删除
#     \??\<源路径>  ->  \??\<目标路径>   表示重命名/覆盖
$src = '\??\' + $staged
$dst = '\??\' + $target

$key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
$existing = (Get-ItemProperty -Path $key -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations

$list = [System.Collections.Generic.List[string]]::new()
if ($null -ne $existing) {
    foreach ($e in $existing) { $list.Add($e) }
}

# 避免重复写入同一对
$alreadyQueued = $false
for ($i = 0; $i -lt $list.Count - 1; $i += 2) {
    if ($list[$i] -eq $src) { $alreadyQueued = $true; break }
}

if ($alreadyQueued) {
    Write-Host "[跳过] 替换操作已在队列中。"
} else {
    $list.Add($src)
    $list.Add($dst)
    Set-ItemProperty -Path $key -Name PendingFileRenameOperations -Value ([string[]]$list) -Type MultiString
    Write-Host "[OK] 已登记「重启后替换 DLL」。" -ForegroundColor Green
}

Write-Host ""
Write-Host "===== 当前 PendingFileRenameOperations 队列 ====="
$now = (Get-ItemProperty -Path $key -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations
if ($null -eq $now) {
    Write-Host "(空)"
} else {
    for ($i = 0; $i -lt $now.Count; $i += 2) {
        $s = $now[$i]
        $d = if ($i + 1 -lt $now.Count) { $now[$i + 1] } else { '' }
        Write-Host ("  {0}  ==>  {1}" -f $s, $(if ($d) { $d } else { '(删除)' }))
    }
}

Write-Host ""
Write-Host "请重启电脑以完成 DLL 替换。重启后运行以下命令验证：" -ForegroundColor Cyan
Write-Host '  Get-Item "D:\FolderCrypto\shell-support\FolderCrypto.ShellNative.dll" | Select-Object Length, LastWriteTime'
Write-Host "预期 Length = 51200（新版本），而非 39936（旧版本）。"

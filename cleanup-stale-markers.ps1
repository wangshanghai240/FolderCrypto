# ===========================================================================
#  cleanup-stale-markers.ps1
#  清理“陈旧 .folderlock 标记”的文件夹.
#
#  背景: 旧版本的文件夹加密功能曾把文件夹设为 Hidden+System 并放一个
#        .folderlock 标记. 后来文件夹属性恢复可见后, 残留的 .folderlock
#        仍会让软件误判为“已加密”(其实内容从未真正加密).
#
#  作用: 扫描目录, 找出带 .folderlock 的文件夹, 并删除这些陈旧的标记,
#        使这些文件夹能重新被正常“加密”(设置新密码).
#
#  注意: 只删除“头部为 FCENC000 的合法加密标记”; 会逐一交互确认.
#
#  用法 (PowerShell):
#     .\cleanup-stale-markers.ps1                          # 扫描桌面
#     .\cleanup-stale-markers.ps1 -StartPath 'I:\'          # 指定根目录
#     .\cleanup-stale-markers.ps1 -StartPath 'I:\' -Force   # 不逐一确认, 一键清理
#     .\cleanup-stale-markers.ps1 -ScanOnly                 # 只扫描, 不删除
# ===========================================================================
param(
    [string]$StartPath = "$env:USERPROFILE\Desktop",
    [switch]$Force,
    [switch]$ScanOnly
)
$ErrorActionPreference = 'SilentlyContinue'

# 文件夹标记文件名
$MarkerName = '.folderlock'
# 加密标记魔数 (文件头)
$Magic = [System.Text.Encoding]::ASCII.GetBytes('FCENC000')

if (-not (Test-Path -LiteralPath $StartPath)) {
    Write-Host "路径不存在: $StartPath"
    exit 1
}

Write-Host "扫描目录: $StartPath"
Write-Host ''

# ---- 扫描所有带 .folderlock 标记的文件夹 ----
$folders = @()
Get-ChildItem -LiteralPath $StartPath -Directory -Recurse -Force | ForEach-Object {
    $marker = Join-Path $_.FullName $MarkerName
    $cond = (Test-Path -LiteralPath $marker) -and (-not (Test-Path -LiteralPath $marker -PathType Container))
    if ($cond) {
        $isValid = $false
        try {
            $fs = [System.IO.File]::OpenRead($marker)
            try {
                $head = New-Object byte[] $Magic.Length
                $n = $fs.Read($head, 0, $Magic.Length)
                $isValid = ($n -eq $Magic.Length)
                if ($isValid) {
                    for ($i = 0; $i -lt $Magic.Length; $i++) {
                        if ($head[$i] -ne $Magic[$i]) { $isValid = $false; break }
                    }
                }
            } finally { $fs.Dispose() }
        } catch { $isValid = $false }

        if ($isValid) {
            $folders += [pscustomobject]@{
                Folder = $_.FullName
                MarkerLength = (Get-Item -LiteralPath $marker -Force).Length
            }
        }
    }
}

if ($folders.Count -eq 0) {
    Write-Host '未发现带 .folderlock 的文件夹 (没有陈旧标记需要清理).'
    exit 0
}

Write-Host "发现 $($folders.Count) 个带加密标记的文件夹:"
for ($i = 0; $i -lt $folders.Count; $i++) {
    Write-Host ("  [{0}] {1}  (标记 {2} 字节)" -f ($i + 1), $folders[$i].Folder, $folders[$i].MarkerLength)
}

if ($ScanOnly) {
    Write-Host ''
    Write-Host '[-ScanOnly] 仅扫描, 未做任何修改.'
    exit 0
}

Write-Host ''
Write-Host '警告: 删除 .folderlock 标记后, 该文件夹将被视为“未加密”,'
Write-Host '      里面的内容保持原样(不会被改动), 之后可重新右键“加密”设置新密码.'
Write-Host '      (含 FCENC000 魔数的合法标记才会被删除)'

if (-not $Force) {
    Write-Host ''
    $ans = Read-Host '全部清理? [Y/N]'
    if ($ans -notmatch '^[Yy]') {
        Write-Host '已取消.'
        exit 0
    }
}

$removed = 0
foreach ($f in $folders) {
    $marker = Join-Path $f.Folder $MarkerName
    if (Test-Path -LiteralPath $marker) {
        Remove-Item -LiteralPath $marker -Force
        if (-not (Test-Path -LiteralPath $marker)) {
            Write-Host "已删除标记: $($f.Folder)"
            $removed++
        } else {
            Write-Host "删除失败: $($f.Folder)"
        }
    }
}

Write-Host ''
Write-Host "完成. 共删除 $removed 个陈旧 .folderlock 标记."
Write-Host '这些文件夹现在可正常右键“加密”.'

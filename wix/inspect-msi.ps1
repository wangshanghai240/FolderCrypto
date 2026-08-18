# Inspect ControlEvent table for FcSetupDlg in final MSI.
$msi = 'f:\VScode\文件夹加密\packages\FolderCrypto-Setup-1.0.14-v2-x64.msi'
$i = New-Object -ComObject WindowsInstaller.Installer
$db = $i.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $i, @($msi, 0))

function Q([string]$sql) {
    $v = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    $v.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $v, $null) | Out-Null
    $rows = @()
    while ($true) {
        $r = $v.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $v, $null)
        if ($null -eq $r) { break }
        $vals = @()
        $count = $r.GetType().InvokeMember('FieldCount', 'GetProperty', $null, $r, $null)
        for ($k = 1; $k -le $count; $k++) {
            $vals += $r.GetType().InvokeMember('StringData', 'GetProperty', $null, $r, @($k))
        }
        $rows += , $vals
    }
    return , $rows
}

Write-Host '== All ControlEvents in Db (Event | Control | Arg) =='
$rows = Q 'SELECT Event, Dialog_, Control_, Argument FROM `ControlEvent`'
foreach ($r in $rows) { Write-Host ("  [" + $r[0] + "] Dlg=" + $r[1] + " Ctrl=" + $r[2] + " Arg=" + $r[3]) }
$set = (Q 'SELECT Event FROM `ControlEvent` WHERE `Event` = ''SetTargetPath''')
Write-Host ("SetTargetPath occurrences: " + $set.Count)
$end = (Q "SELECT Event, Control_ FROM `ControlEvent` WHERE `Event` = 'EndDialog' AND `Dialog_` = 'FcSetupDlg'")
Write-Host 'EndDialog in FcSetupDlg:'
foreach ($r in $end) { Write-Host ("  Event=" + $r[0] + " Ctrl=" + $r[1]) }

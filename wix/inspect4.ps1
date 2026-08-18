# Inspect v3 MSI: Registry table + shell-support files + Feature components.
$msi = 'f:\VScode\文件夹加密\packages\FolderCrypto-Setup-1.0.14-v3-x64.msi'
$i = New-Object -ComObject WindowsInstaller.Installer
$db = $i.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $i, @($msi, [int]0))

function Q([string]$sql) {
    $v = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @($sql))
    $v.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $v, $null) | Out-Null
    $rows = @()
    while ($true) {
        $r = $v.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $v, $null)
        if ($null -eq $r) { break }
        $vals = @()
        $count = $r.GetType().InvokeMember('FieldCount', 'GetProperty', $null, $r, $null)
        for ($k = 1; $k -le $count; $k++) { $vals += $r.GetType().InvokeMember('StringData', 'GetProperty', $null, $r, @($k)) }
        $rows += , $vals
    }
    return , $rows
}

Write-Host '== Registry rows mentioning FolderCryptoEncrypt/Decrypt (first 6 cols) =='
try { $rows = Q "SELECT `Registry`,`Root`,`Key`,`Name`,`Value` FROM `Registry` WHERE `Key` LIKE '%shell%FolderCrypto%'"; foreach ($r in $rows) { Write-Host ("  " + $r[0] + " | " + $r[1] + " | " + $r[2] + " | " + $r[3] + " | " + $r[4]) } } catch { Write-Host ('  err: ' + $_.Exception.Message) }

Write-Host '== File table: shell-support files =='
try { $rows = Q "SELECT `FileName` FROM `File` WHERE `FileName` LIKE '%overlay%' OR `FileName` LIKE '%unlock%' OR `FileName` LIKE '%Shell%'"; foreach ($r in $rows) { Write-Host ('  ' + $r[0]) } } catch { Write-Host ('  err: ' + $_.Exception.Message) }

# Inspect v2 MSI tables via WindowsInstaller COM with clean queries.
$msi = 'f:\VScode\文件夹加密\packages\FolderCrypto-Setup-1.0.14-v2-x64.msi'
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

Write-Host '== CustomAction =='
try { $rows = Q 'SELECT Action FROM `CustomAction`'; Write-Host ('  count=' + $rows.Count); foreach ($r in $rows) { Write-Host ('  Action=' + $r[0]) } }
catch { Write-Host ('  err: ' + $_.Exception.Message) }

Write-Host '== File table: shell-containing =='
try { $rows = Q "SELECT File, FileName FROM `File` WHERE `FileName` LIKE '%SH%'"; foreach ($r in $rows) { Write-Host ('  ' + $r[0] + '  ' + $r[1]) } }
catch { Write-Host ('  err: ' + $_.Exception.Message) }

Write-Host '== Fragment/Binary/Icon presence =='
foreach ($t in 'Binary','Icon') {
    try { $rows = Q "SELECT `Name` FROM ``$t``"; Write-Host ("  $t count=" + $rows.Count) } catch { Write-Host ("  $t err") }
}

Write-Host '== Properties of interest =='
try { $rows = Q "SELECT `Property`,`Value` FROM `Property` WHERE `Property`='PRODUCTNAME' OR `Property`='ARPPRODUCTICON'"; foreach ($r in $rows) { Write-Host ('  ' + $r[0] + ' = ' + $r[1]) } } catch { }

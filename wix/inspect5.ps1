$msi = 'f:\VScode\文件夹加密\packages\FolderCrypto-Setup-1.0.14-v3-x64.msi'
$i = New-Object -ComObject WindowsInstaller.Installer
$db = $i.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $i, @($msi, [int]0))
Write-Host "db opened ok"
$v = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT Registry,Root,Key,Name,Value FROM Registry WHERE Key LIKE '%shell%FolderCrypto%'"))
$v.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $v, $null) | Out-Null
$n = 0
while ($true) {
    $r = $v.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $v, $null)
    if ($null -eq $r) { break }
    $n++
    $reg = $r.GetType().InvokeMember('StringData', 'GetProperty', $null, $r, @(1))
    $root = $r.GetType().InvokeMember('StringData', 'GetProperty', $null, $r, @(2))
    $key = $r.GetType().InvokeMember('StringData', 'GetProperty', $null, $r, @(3))
    $name = $r.GetType().InvokeMember('StringData', 'GetProperty', $null, $r, @(4))
    $val = $r.GetType().InvokeMember('StringData', 'GetProperty', $null, $r, @(5))
    Write-Host ("  $reg | $root | $key | name=$name | $val")
}
Write-Host "context menu registry rows: $n"

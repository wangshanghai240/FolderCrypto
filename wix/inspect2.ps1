# Inspect v2 MSI: CustomAction table + whether FolderCrypto.Shell* files are present (via File table name).
$msi = 'f:\VScode\文件夹加密\packages\FolderCrypto-Setup-1.0.14-v2-x64.msi'
$i = New-Object -ComObject WindowsInstaller.Installer
try { $db = $i.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $i, @($msi, 0)) } catch { Write-Host ('Open db failed: ' + $_.Exception.Message); exit }

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

Write-Host '== CustomAction table =='
try {
    $rows = Q 'SELECT Action, Type, Source, Target FROM `CustomAction`'
    if ($rows.Count -eq 0) { Write-Host '  (no CustomActions)' }
    foreach ($r in $rows) { Write-Host ("  Action=" + $r[0] + " Type=" + $r[1] + " Source=" + $r[2] + " Target=" + $r[3]) }
} catch { Write-Host '  no CustomAction table' }

Write-Host '== Shell-related files in File table =='
try {
    $rows = Q "SELECT File, FileName FROM `File` WHERE `FileName` LIKE '%Shell%'"
    foreach ($r in $rows) { Write-Host ("  " + $r[0] + "  " + $r[1]) }
} catch { Write-Host '  (file query failed)' }

Write-Host '== InstallExecuteSequence (non-standard actions) =='
try {
    $rows = Q "SELECT Action, Sequence, Condition FROM `InstallExecuteSequence` WHERE `Action` NOT IN ('ValidateProductID','InstallInitialize','ProcessComponents','UnpublishFeatures','RemoveFiles','InstallFiles','CreateShortcuts','WriteRegistryValues','RegisterUser','RemoveShortcuts','RegisterProduct','PublishProduct','RegisterTypeLibraries','PublishFeatures','RemoveRegistryValues','RemoveShortcuts','RemoveFolders','CreateFolders','InstallFinalize','InstallExecute','CostInitialize','FileCost','CostFinalize','InstallValidate','InstallServices','StopServices','DeleteServices','StartServices','DuplicateFiles','SelfRegModules','SelfUnregModules','MsiPublishAssemblies','MsiUnpublishAssemblies')"
    foreach ($r in $rows) { Write-Host ("  Action=" + $r[0] + " Seq=" + $r[1] + " Cond=" + $r[2]) }
} catch { Write-Host '  (seq query failed)' }

# After clicking Install: check install completion, FCTest contents, and MSI events/log.
Start-Sleep -Seconds 10
Write-Host '=== msiexec windows now ==='
Get-Process msiexec -ErrorAction SilentlyContinue | Select-Object Id, MainWindowTitle | Format-Table | Out-String | Write-Host
Write-Host '=== top-level windows ==='
Add-Type -AssemblyName UIAutomationClient
$walk = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($w in $walk) { if ($w.Current.Name.Trim() -ne '') { Write-Host ('  ' + $w.Current.Name) } }
Write-Host '=== FCTest populated? ==='
$files = Get-ChildItem 'C:\Temp\FCTest' -Recurse -File -ErrorAction SilentlyContinue
Write-Host ('file count: ' + @($files).Count)
$files | Select-Object -First 10 Name | Format-Table | Out-String | Write-Host
Write-Host '=== registered? ==='
Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*FolderCrypto*' } | Select-Object DisplayName, DisplayVersion, InstallLocation | Format-Table | Out-String | Write-Host
Write-Host '=== e2e log: result & key lines ==='
$log = 'C:\Temp\_fc_e2e.log'
if (Test-Path $log) {
    Get-Content $log | Select-String 'Return value 3|1603|1602|安装成功|安装失败|Product: FolderCrypto|MainEngineThread is returning|INSTALLFOLDER|Executing op: File' | Select-Object -Last 15 | ForEach-Object { $_.Line } | Out-String | Write-Host
} else { Write-Host 'no log' }

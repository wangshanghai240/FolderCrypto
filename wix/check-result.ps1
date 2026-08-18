# Wait and detect whether an error modal ('无效' / 2343 / error) appeared, and install state.
Start-Sleep -Seconds 5
Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
Write-Host '=== all visible top-level windows (titles) ==='
$walk = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($w in $walk) { if ($w.Current.Name.Trim() -ne '') { Write-Host ('  [' + $w.Current.Name + ']') } }
Write-Host '=== msiexec ==='
Get-Process msiexec -ErrorAction SilentlyContinue | Select-Object Id, MainWindowTitle, StartTime | Format-Table | Out-String | Write-Host
Write-Host '=== recent MSI events ==='
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MsiInstaller'; StartTime=(Get-Date).AddMinutes(-8)} -ErrorAction SilentlyContinue | Select-Object -First 6 TimeCreated,Id | Format-Table | Out-String | Write-Host

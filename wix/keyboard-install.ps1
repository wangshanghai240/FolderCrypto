# Drive the MSI dialog via keyboard: navigate to Install (default) and press Enter; capture result.
param([int]$MsiPid)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);' -Name U -Namespace N
$p=Get-Process -Id $MsiPid; $p.Refresh(); [N.U]::SetForegroundWindow($p.MainWindowHandle)|Out-Null; Start-Sleep -Milliseconds 500
# The Install button is Default; Enter should activate it. But if focus is on PathEdit/DirectoryCombo, first Tab to button.
# Send a few Tab presses then Enter to be robust.
foreach($i in 1..6){ [System.Windows.Forms.SendKeys]::SendWait('{TAB}'); Start-Sleep -Milliseconds 150 }
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Write-Host 'Pressed Tab(6)+Enter to reach & activate Install'
Start-Sleep -Seconds 10
Write-Host '=== windows now ==='
Add-Type -AssemblyName UIAutomationClient
$walk=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
foreach($w in $walk){ if($w.Current.Name.Trim() -ne ''){ Write-Host ('  '+$w.Current.Name) } }
Get-Process msiexec -ErrorAction SilentlyContinue | Select-Object Id,MainWindowTitle | Format-Table | Out-String | Write-Host
Write-Host '=== recent MSI events (5 min) ==='
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MsiInstaller'; StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | Select-Object -First 10 TimeCreated,Id | Format-Table | Out-String | Write-Host

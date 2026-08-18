# Click the "安装" (Install) default button in the FolderCrypto MSI window via Enter key,
# then detect whether the "INSTALLFOLDER 无效" (invalid path) modal appears.
param([int]$MsiPid)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);' -Name W -Namespace N | Out-Null

$p = Get-Process -Id $MsiPid
$p.Refresh()
[N.W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Seconds 3

# Enumerate top-level windows to see if an error modal appeared
Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::RootElement
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
Write-Host '=== top-level windows after Enter ==='
foreach ($w in $all) { if ($w.Current.Name.Trim() -ne '') { Write-Host ('  ' + $w.Current.Name) } }
Write-Host '=== msiexec processes ==='
Get-Process msiexec -ErrorAction SilentlyContinue | Select-Object Id, MainWindowTitle | Format-Table | Out-String | Write-Host

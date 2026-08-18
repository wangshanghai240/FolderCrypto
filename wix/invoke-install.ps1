# Find the "安装" button and invoke it via UIA InvokePattern, then check for error modal.
param([int]$MsiPid)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Get-Process -Id $MsiPid
$p.Refresh()
$hwnd = $p.MainWindowHandle
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
Write-Host ("Window: " + $root.Current.Name)

$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '安装')
$btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if (-not $btn) { Write-Host 'Install button not found; enumerating:' }
else {
    Write-Host ("Found: " + $btn.Current.ControlType.ProgrammaticName)
    try {
        $inv = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        Write-Host 'Invoked Install button.'
    } catch { Write-Host ('Invoke failed: ' + $_.Exception.Message) }
}
Start-Sleep -Seconds 3
Write-Host '=== top-level windows after click ==='
$walk = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($w in $walk) { if ($w.Current.Name.Trim() -ne '') { Write-Host ('  ' + $w.Current.Name) } }
Write-Host '=== msiexec titles ==='
Get-Process msiexec -ErrorAction SilentlyContinue | Select-Object Id, MainWindowTitle | Format-Table | Out-String | Write-Host

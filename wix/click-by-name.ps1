# Click a control by its Name within the window using its bounding rectangle.
param([int]$MsiPid, [string]$CtrlName = '安装')
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags,uint dx,uint dy,uint dwData,uint dwExtraInfo);' -Name M -Namespace N

function Click-Point([int]$x,[int]$y){
  [N.M]::SetCursorPos($x,$y) | Out-Null
  Start-Sleep -Milliseconds 150
  [N.M]::mouse_event(0x0002,0,0,0,0) | Out-Null  # down
  Start-Sleep -Milliseconds 80
  [N.M]::mouse_event(0x0004,0,0,0,0) | Out-Null  # up
}

$p = Get-Process -Id $MsiPid; $p.Refresh()
$rootEl = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $CtrlName)
$ctrl = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if (-not $ctrl) { Write-Host "Not found: $CtrlName"; exit }
$rect = $ctrl.Current.BoundingRectangle
$cx = [int]($rect.X + $rect.Width/2); $cy = [int]($rect.Y + $rect.Height/2)
Write-Host ("$CtrlName rect=({0},{1},{2},{3}) center=({4},{5})" -f $rect.X,$rect.Y,$rect.Width,$rect.Height,$cx,$cy)
Click-Point $cx $cy
Write-Host "Clicked $CtrlName"
Start-Sleep -Seconds 3
Write-Host '=== top-level windows ==='
$walk = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($w in $walk) { if ($w.Current.Name.Trim() -ne '') { Write-Host ('  ' + $w.Current.Name) } }
Get-Process msiexec -ErrorAction SilentlyContinue | Select-Object Id, MainWindowTitle | Format-Table | Out-String | Write-Host

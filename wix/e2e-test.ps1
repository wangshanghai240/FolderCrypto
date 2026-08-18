# Full end-to-end UI test: set install path in PathEdit, click Install, verify.
param([int]$MsiPid, [string]$InstallPath = 'C:\Temp\FCTest')
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,uint e); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);' -Name U -Namespace N

function ClickAt([int]$x,[int]$y){ [N.U]::SetCursorPos($x,$y)|Out-Null; Start-Sleep -Milliseconds 120; [N.U]::mouse_event(2,0,0,0,0)|Out-Null; Start-Sleep -Milliseconds 60; [N.U]::mouse_event(4,0,0,0,0)|Out-Null }
function Ctrl-A-Typed([string]$txt){ [System.Windows.Forms.SendKeys]::SendWait('^a'); Start-Sleep -Milliseconds 150; [System.Windows.Forms.SendKeys]::SendWait($txt) }

$p=Get-Process -Id $MsiPid; $p.Refresh(); [N.U]::SetForegroundWindow($p.MainWindowHandle)|Out-Null; Start-Sleep -Milliseconds 400
$root=[System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

# find the PathEdit pane: control with property value INSTALLFOLDER showing path. We match value containing 'FolderCrypto' or Program Files
$target=$null
$all=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
foreach($e in $all){
  try{ $v=$e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }catch{ continue }
  if($v -and $v -match 'Program Files|FolderCrypto\\'){ $target=$e; break }
}
if(-not $target){ Write-Host 'PathEdit not found; screenshot needed'; exit }
$r=$target.Current.BoundingRectangle; $x=[int]($r.X+$r.Width/2); $y=[int]($r.Y+$r.Height/2)
Write-Host ("PathEdit at ("+$x+","+$y+") value="+$target.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value)

# focus path edit and set new path
ClickAt $x $y; Start-Sleep -Milliseconds 300
Ctrl-A-Typed $InstallPath
Start-Sleep -Milliseconds 300

# click Install
$byName=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'安装')
$btn=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$byName)
if($btn){ $br=$btn.Current.BoundingRectangle; ClickAt ([int]($br.X+$br.Width/2)) ([int]($br.Y+$br.Height/2)); Write-Host 'Clicked 安装' }
Start-Sleep -Seconds 8
Write-Host '=== windows now ==='
$walk=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
foreach($w in $walk){ if($w.Current.Name.Trim() -ne ''){ Write-Host ('  '+$w.Current.Name) } }
Get-Process msiexec -ErrorAction SilentlyContinue | Select-Object Id,MainWindowTitle | Format-Table | Out-String | Write-Host
Write-Host '=== installed? ==='
Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*FolderCrypto*' } | Select-Object DisplayName,DisplayVersion,InstallLocation | Format-Table | Out-String | Write-Host
Write-Host '=== FCTest contents ==='
Test-Path 'C:\Temp\FCTest'
Get-ChildItem 'C:\Temp\FCTest' -ErrorAction SilentlyContinue | Select-Object -First 8 Name | Format-Table | Out-String | Write-Host
Write-Host '=== recent MSI events ==='
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='MsiInstaller'; StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | Select-Object -First 8 TimeCreated,Id | Format-Table | Out-String | Write-Host

# Reliable E2E: launch perUser, click Edit box by proportional coords, type path, click Install, verify.
param([string]$InstallPath = 'F:\FCTest4')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,uint e);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public struct RECT { public int Left,Top,Right,Bottom; }
public struct POINT { public int X,Y; }
'@ -Name Win -Namespace Native

function Dpi([IntPtr]$h){ # get window dpi scale via GetDpiForWindow if available
  return 1.0
}

Remove-Item $InstallPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne 24700 } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

$msi='C:\Temp\_fc_diag_peruser.msi'; $log='C:\Temp\_fc_edit3.log'; Remove-Item $log -Force -ErrorAction SilentlyContinue
$p = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /l*v `"$log`"" -PassThru
Start-Sleep -Seconds 7; $p.Refresh()
$hwnd = $p.MainWindowHandle
Write-Host ("PID="+$p.Id+" hwnd="+$hwnd+" title="+$p.MainWindowTitle)
if ($hwnd -eq 0) { Write-Host 'no window'; exit }
[Native.Win]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 400

# Map MSI dialog (370 x 270 units) to client pixels via GetClientRect
$rect = New-Object Native.Win+RECT
[Native.Win]::GetClientRect($hwnd, [ref]$rect) | Out-Null
$cw = $rect.Right - $rect.Left; $ch = $rect.Bottom - $rect.Top
Write-Host ("client=" + $cw + "x" + $ch + " (msi 370x270)")
# Edit box: X=25 Y=112 W=320 H=18 -> center (185,121) units
$ux=185; $uy=121
$px = [int]($ux * $cw / 370); $py = [int]($uy * $ch / 270)
$pt = New-Object Native.Win+POINT; $pt.X=$px; $pt.Y=$py
[Native.Win]::ClientToScreen($hwnd, [ref]$pt) | Out-Null
[Native.Win]::SetCursorPos($pt.X,$pt.Y) | Out-Null; Start-Sleep -Milliseconds 200
[Native.Win]::mouse_event(2,0,0,0,0)|Out-Null; Start-Sleep -Milliseconds 60; [Native.Win]::mouse_event(4,0,0,0,0)|Out-Null
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('^a'); Start-Sleep -Milliseconds 150
[System.Windows.Forms.SendKeys]::SendWait($InstallPath)
Start-Sleep -Milliseconds 300
Write-Host ("clicked Edit + typed: " + $InstallPath)

# Click 安装: units (264, 251.5)
$ix=264; $iy=251
$px2=[int]($ix*$cw/370); $py2=[int]($iy*$ch/270)
$pt2=New-Object Native.Win+POINT; $pt2.X=$px2; $pt2.Y=$py2
[Native.Win]::ClientToScreen($hwnd,[ref]$pt2)|Out-Null
[Native.Win]::SetCursorPos($pt2.X,$pt2.Y)|Out-Null; Start-Sleep -Milliseconds 150; [Native.Win]::mouse_event(2,0,0,0,0)|Out-Null; Start-Sleep -Milliseconds 60; [Native.Win]::mouse_event(4,0,0,0,0)|Out-Null
Write-Host 'clicked 安装'
Start-Sleep -Seconds 12

Write-Host '=== result ==='
Write-Host ("installed to $InstallPath files: $((Get-ChildItem $InstallPath -File -ErrorAction SilentlyContinue).Count)")
$all=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
Write-Host 'windows:'
foreach($w in $all){ if($w.Current.Name -match 'FolderCrypto|无效|错误|安装'){ Write-Host ('  '+$w.Current.Name) } }
Write-Host '=== log tail ==='
Get-Content $log | Select-String 'INSTALLFOLDER|MainEngineThread|1603|1314|1801|SetTargetPath|returning' | Select-Object -Last 14 | ForEach-Object { $_.Line } | Out-String | Write-Host

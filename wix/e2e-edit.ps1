# E2E test for Edit+SetTargetPath directory selection.
# 1) launch perUser MSI cleanly 2) fill Edit with a custom dir 3) click 安装 4) verify install dir + files.
param([string]$InstallPath = 'F:\FCTest3')
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y); [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,uint e);' -Name U -Namespace N

# ensure dir exists & empty
Remove-Item $InstallPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null

# kill stray msiexec (except stuck 24700)
Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne 24700 } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

$msi = 'C:\Temp\_fc_diag_peruser.msi'
$log = 'C:\Temp\_fc_edit2.log'
Remove-Item $log -Force -ErrorAction SilentlyContinue
$p = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /l*v `"$log`"" -PassThru
Start-Sleep -Seconds 7
$p.Refresh()
Write-Host ("PID=" + $p.Id + " title=" + $p.MainWindowTitle)

# find the main window; wait for it
$winEl = $null
for ($i=0; $i -lt 10; $i++) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    if ($root -and $root.Current.Name -match 'FolderCrypto') { $winEl = $root; break }
    $p.Refresh(); Start-Sleep -Seconds 1
}
if (-not $winEl) { Write-Host 'main window not found'; exit }
[N.U]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500

# find the Edit control (auto id EditText or a value that looks like a path)
$edit = $null
$all = $winEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($e in $all) {
    try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { continue }
    if ($v -and $v -match '\\\\|FolderCrypto|Program Files') { $edit = $e; break }
}
if (-not $edit) {
    # fallback: click the known Edit area using control coords? enumerate buttons
    Write-Host 'Edit not found via value; enumerating Edit/EditControl:'
    foreach ($e in $all) { if ($e.Current.ControlType.ProgrammaticName -match 'Edit') { $r=$e.Current.BoundingRectangle; Write-Host ("  Edit at " + $r.X + "," + $r.Y) } }
    exit
}
$r2 = $edit.Current.BoundingRectangle
$ex = [int]($r2.X + 10); $ey = [int]($r2.Y + $r2.Height/2)
[N.U]::SetCursorPos($ex, $ey) | Out-Null; Start-Sleep -Milliseconds 200; [N.U]::mouse_event(2,0,0,0,0)|Out-Null; Start-Sleep -Milliseconds 60; [N.U]::mouse_event(4,0,0,0,0)|Out-Null
Start-Sleep -Milliseconds 300
[System.Windows.Forms.SendKeys]::SendWait('^a'); Start-Sleep -Milliseconds 150
[System.Windows.Forms.SendKeys]::SendWait($InstallPath); Start-Sleep -Milliseconds 300
Write-Host ("typed path: " + $InstallPath)

# click 安装
$byName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '安装')
$btn = $winEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $byName)
if ($btn) { $br=$btn.Current.BoundingRectangle; [N.U]::SetCursorPos([int]($br.X+$br.Width/2),[int]($br.Y+$br.Height/2))|Out-Null; Start-Sleep -Milliseconds 150; [N.U]::mouse_event(2,0,0,0,0)|Out-Null; Start-Sleep -Milliseconds 60; [N.U]::mouse_event(4,0,0,0,0)|Out-Null; Write-Host 'clicked 安装' }
Start-Sleep -Seconds 12

Write-Host '=== result ==='
Write-Host ("MSI exit: " + (Get-Process -Id $p.Id -ErrorAction SilentlyContinue).MainWindowTitle)
Write-Host ("installed to $InstallPath exists: $(Test-Path $InstallPath); files: $((Get-ChildItem $InstallPath -File -ErrorAction SilentlyContinue).Count)")
$inst = Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like '*FolderCrypto*' }
if ($inst) { Write-Host ("registered: " + $inst.InstallLocation) } else { Write-Host 'not registered' }
Write-Host '=== log: INSTALLFOLDER & final ==='
Get-Content $log | Select-String 'INSTALLFOLDER|MainEngineThread|return value|1603|1314|1801|SetTargetPath' | Select-Object -Last 12 | ForEach-Object { $_.Line } | Out-String | Write-Host

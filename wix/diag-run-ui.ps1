$ErrorActionPreference = 'Continue'
$msi = 'C:\Temp\_fc_diag_peruser.msi'
Start-Process msiexec.exe -ArgumentList "/i `"$msi`"" 
Write-Host "launched UI; wait for dialog"

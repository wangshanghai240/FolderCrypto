param([string]$StartPath = "$env:USERPROFILE\Desktop")
$ErrorActionPreference = 'SilentlyContinue'
if (-not (Test-Path $StartPath)) { Write-Host "Path not found: $StartPath"; exit 1 }
Write-Host "Scanning under: $StartPath"
$rows = @()
Get-ChildItem -LiteralPath $StartPath -Directory -Recurse -Force | ForEach-Object {
    $marker = Join-Path $_.FullName '.folderlock'
    if (Test-Path -LiteralPath $marker) {
        $rows += $_.FullName
    }
}
if ($rows.Count -eq 0) { Write-Host 'No encrypted (.folderlock) folders found.'; exit 0 }
Write-Host "Found $($rows.Count) encrypted folder(s):"
$rows | ForEach-Object { Write-Host "  $_" }
Write-Host ''
Write-Host 'These folders are hidden. Restore visibility now? (clear Hidden+System, keep .folderlock so they stay encrypted) [Y/N]'
$ans = Read-Host
if ($ans -match '^[Yy]') {
    foreach ($d in $rows) {
        $attrs = (Get-Item -LiteralPath $d -Force).Attributes
        $new = $attrs -band (-bnot ([System.IO.FileAttributes]::Hidden -bor [System.IO.FileAttributes]::System))
        Get-Item -LiteralPath $d -Force | ForEach-Object { $_.Attributes = $new }
        Write-Host "Restored visibility: $d"
    }
    Write-Host 'Done. The folders are now visible (still encrypted).'
} else {
    Write-Host 'Skipped.'
}
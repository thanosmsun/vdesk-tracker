$exe = "C:\Users\ThanosMilios\projects\vdesk-tracker\publish\VirtualDesktopTracker.exe"
$startMenu = [Environment]::GetFolderPath("StartMenu")
$lnkPath = Join-Path $startMenu "Programs\VirtualDesktopTracker.lnk"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnkPath)
$sc.TargetPath = $exe
$sc.WorkingDirectory = Split-Path $exe -Parent
$sc.Description = "Track time spent per Windows virtual desktop and manage task views"
$sc.WindowStyle = 1
$sc.IconLocation = "$exe,0"
$sc.Save()
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($ws) | Out-Null
Write-Host "Created: $lnkPath"
Write-Host "Target:  $exe"
Get-Item $lnkPath | Select-Object FullName, Length, LastWriteTime

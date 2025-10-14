param(
  [string]$ServiceName = "UsbCopyMonService"
)

# Resolve the path for the service executable
$exe = Resolve-Path ".\UsbCopyMon.Service.exe"

# Ensure correct formatting for sc.exe binPath
$exePath = "`"$exe`""

# If the service exists, stop and delete it
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
  sc.exe stop $ServiceName | Out-Null
  sc.exe delete $ServiceName | Out-Null
}

# Create and start the service
sc.exe create $ServiceName binPath= $exePath start= auto | Out-Null
sc.exe start $ServiceName | Out-Null

sc.exe failure "UsbCopyMonService" actions= restart/5000/restart/5000/restart/5000 reset= 86400 
sc.exe failureflag "UsbCopyMonService" 1

sc.exe qfailure "UsbCopyMonService"

sc.exe config "UsbCopyMonService" start= delayed-auto

# Stop the Tray application by its name (if running)
Get-Process -Name "UsbCopyMon.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force

# Start the Tray application
$trayExe = Resolve-Path "..\Tray\UsbCopyMon.Tray.exe"
Start-Process -FilePath "$trayExe"

Write-Host "Installed and started $ServiceName from $exe"

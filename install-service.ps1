param(
  [string]$PublishDir = "UsbCopyMon.Service\bin\Release\net8.0-windows\win-x64\publish",
  [string]$ServiceName = "UsbCopyMonService"
)

# Resolve the path for the service executable
$exe = Resolve-Path "$PublishDir\UsbCopyMon.Service.exe"

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

# Stop the Tray application by its name (if running)
Get-Process -Name "UsbCopyMon.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force

# Start the Tray application
$trayExe = Resolve-Path ".\UsbCopyMon.Tray\bin\Release\net8.0-windows\publish\UsbCopyMon.Tray.exe"
Start-Process -FilePath "$trayExe"

Write-Host "Installed and started $ServiceName from $exe"

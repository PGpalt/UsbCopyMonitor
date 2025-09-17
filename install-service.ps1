param(
  [string]$PublishDir = "UsbCopyMon.Service\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish",
  [string]$ServiceName = "UsbCopyMonService"
)

$exe = Resolve-Path "$PublishDir\UsbCopyMon.Service.exe"
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
  sc.exe stop $ServiceName | Out-Null
  sc.exe delete $ServiceName | Out-Null
}
sc.exe create $ServiceName binPath= '"' + $exe + '"' start= auto | Out-Null
sc.exe start $ServiceName | Out-Null
Write-Host "Installed and started $ServiceName from $exe"

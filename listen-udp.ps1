# listen-udp.ps1
$port = 5514
$udp  = New-Object System.Net.Sockets.UdpClient($port)
$ep   = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any,0)
Write-Host "Listening on UDP $port ..."
while ($true) {
  $data = $udp.Receive([ref]$ep)
  $txt  = [Text.Encoding]::UTF8.GetString($data)
  Write-Host "$(Get-Date -Format o)  $($ep.Address):$($ep.Port) -> $txt"
}

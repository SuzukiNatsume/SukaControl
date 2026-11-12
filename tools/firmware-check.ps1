$ErrorActionPreference = 'Stop'
$outFile = 'D:\Suzuki\Xiaomi\research\firmware-validation.json'
$results = [ordered]@{}
try {
 $device = Get-CimInstance -Namespace root/wmi -ClassName MICommonInterface
 function Call-Mi([byte]$command,[bool]$write=$false,[byte]$value=0,[byte]$value2=0) {
  $packet = [byte[]]::new(32)
  $packet[1] = $(if($write){0xfb}else{0xfa});$packet[3]=$command;$packet[4]=$value;$packet[6]=$value2
  $reply = Invoke-CimMethod -InputObject $device -MethodName MiInterface -Arguments @{ InData=$packet }
  return ,([byte[]]$reply.OutData)
 }
 $mode = (Call-Mi 8)[4]
 $od = (Call-Mi 10 $false 6)[6]
 $gfx = (Call-Mi 11)[4]
 $results['before'] = @{performance=$mode;overdrive=$od;graphics=$gfx;sensors=[Convert]::ToHexString((Call-Mi 9))}
 try {
  $modes = @()
  foreach($n in 2,1,3,4) {
   $set = Call-Mi 8 $true $n
   Start-Sleep -Milliseconds 350
   $read = (Call-Mi 8)[4]
   $modes += @{requested=$n;actual=$read;response=[Convert]::ToHexString($set);passed=($read -eq $n)}
  }
  $results['performance'] = $modes
  $toggle = [byte](1-$od)
  $set = Call-Mi 10 $true 6 $toggle
  Start-Sleep -Milliseconds 750
  $actual = (Call-Mi 10 $false 6)[6]
  $results['overdrive'] = @{requested=$toggle;actual=$actual;response=[Convert]::ToHexString($set);passed=($toggle -eq $actual)}
  $results['graphicsSameModeWrite'] = [Convert]::ToHexString((Call-Mi 11 $true $gfx))
 } finally {
  $null = Call-Mi 8 $true $mode
  $null = Call-Mi 10 $true 6 $od
  Start-Sleep -Milliseconds 750
  $results['restored'] = @{performance=(Call-Mi 8)[4];overdrive=(Call-Mi 10 $false 6)[6];graphics=(Call-Mi 11)[4]}
 }
} catch { $results['error'] = $_.ToString() }
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outFile

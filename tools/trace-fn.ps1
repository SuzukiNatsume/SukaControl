$ErrorActionPreference='Stop'
$events=New-Object 'System.Collections.Generic.List[object]'
$out='D:\Suzuki\Xiaomi\research\fn-physical-trace.json'
$sources=@()
try {
 foreach($name in 'HID_EVENT20','HID_EVENT21','HID_EVENT22','HID_EVENT23') {
  try { Register-CimIndicationEvent -Namespace root/wmi -ClassName $name -SourceIdentifier "MiControlTrace-$name" | Out-Null; $sources+="MiControlTrace-$name" } catch { $events.Add(@{type='subscription-error';name=$name;error=$_.ToString()}) }
 }
 $device=Get-CimInstance -Namespace root/wmi -ClassName MICommonInterface
 $until=[DateTime]::UtcNow.AddSeconds(100); $last=-1
 while([DateTime]::UtcNow -lt $until) {
  $packet=New-Object byte[] 32; $packet[1]=0xfa; $packet[3]=10; $packet[4]=1
  $data=(Invoke-CimMethod -InputObject $device -MethodName MiInterface -Arguments @{InData=$packet}).OutData
  if($data[6] -ne $last) { $last=$data[6]; $events.Add(@{time=[DateTimeOffset]::Now.ToString('O');type='fn-state';value=$last}) }
  foreach($event in @(Get-Event | Where-Object SourceIdentifier -like 'MiControlTrace-*')) {
   $indication=$event.SourceEventArgs.NewEvent
   $events.Add(@{time=[DateTimeOffset]::Now.ToString('O');type=$event.SourceIdentifier;detail=$indication.EventDetail;instance=$indication.InstanceName})
   Remove-Event -EventIdentifier $event.EventIdentifier
  }
  $events | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $out -Encoding UTF8
  Start-Sleep -Milliseconds 150
 }
} catch { $events.Add(@{type='error';error=$_.ToString()}) }
finally { foreach($source in $sources){Unregister-Event -SourceIdentifier $source -ErrorAction SilentlyContinue}; $events | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $out -Encoding UTF8 }

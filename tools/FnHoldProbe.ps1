$ErrorActionPreference='Stop'
$device=Get-CimInstance -Namespace root/wmi -ClassName MICommonInterface
$end=[DateTime]::UtcNow.AddSeconds(60);$result=New-Object 'System.Collections.Generic.List[object]';$previous=''
while([DateTime]::UtcNow -lt $end){
 $state=[ordered]@{}
 foreach($sub in 0..15){$p=New-Object byte[] 32;$p[1]=0xfa;$p[3]=10;$p[4]=[byte]$sub;$b=[byte[]](Invoke-CimMethod -InputObject $device -MethodName MiInterface -Arguments @{InData=$p}).OutData;$state["$sub"]=([BitConverter]::ToString($b)-replace '-','')}
 $text=$state|ConvertTo-Json -Compress
 if($text -ne $previous){$result.Add(@{time=[DateTimeOffset]::Now.ToString('O');state=$state});$previous=$text;$result|ConvertTo-Json -Depth 5|Set-Content 'D:\Suzuki\Xiaomi\research\fn-held-state.json' -Encoding UTF8}
 Start-Sleep -Milliseconds 150
}

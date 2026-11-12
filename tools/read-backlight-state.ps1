$ErrorActionPreference='Stop'
$device=Get-CimInstance -Namespace root/wmi -ClassName MICommonInterface
$result=@()
foreach($sub in 3,4,5) {
 $packet=New-Object byte[] 32;$packet[1]=0xfa;$packet[3]=10;$packet[4]=[byte]$sub
 $data=[byte[]](Invoke-CimMethod -InputObject $device -MethodName MiInterface -Arguments @{InData=$packet}).OutData
 $result+=@{sub=$sub;data=([BitConverter]::ToString($data) -replace '-','')}
}
$result | ConvertTo-Json | Set-Content 'D:\Suzuki\Xiaomi\research\backlight-read.json' -Encoding UTF8

$ErrorActionPreference='Stop'
$results=New-Object 'System.Collections.Generic.List[object]'
function Request([byte]$command,[byte]$value=0) {
 $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','MiControl.Hardware.v1',[IO.Pipes.PipeDirection]::InOut)
 try {
  $pipe.Connect(1000);$body=[Text.Encoding]::UTF8.GetBytes((@{Command=$command;Write=$false;Value=$value;Value2=0}|ConvertTo-Json -Compress))
  $pipe.Write([BitConverter]::GetBytes($body.Length),0,4);$pipe.Write($body,0,$body.Length)
  $reader=[IO.BinaryReader]::new($pipe);$length=$reader.ReadInt32();return [Text.Encoding]::UTF8.GetString($reader.ReadBytes($length))|ConvertFrom-Json
 } finally {$pipe.Dispose()}
}
$end=[DateTime]::UtcNow.AddSeconds(100);$before=''
while([DateTime]::UtcNow -lt $end) {
 try {
  $performance=Request 8;$fn=Request 10 1
  $p=[Convert]::FromBase64String($performance.Data);$f=[Convert]::FromBase64String($fn.Data)
  $current="$($p[4])/$($f[6])"
  if($current -ne $before){$results.Add(@{time=[DateTimeOffset]::Now.ToString('O');performance=$p[4];fnLock=$f[6]});$before=$current}
 } catch {$results.Add(@{error=$_.ToString()});break}
 $results|ConvertTo-Json -Depth 4|Set-Content 'D:\Suzuki\Xiaomi\research\fn-g-trace.json' -Encoding UTF8
 Start-Sleep -Milliseconds 180
}

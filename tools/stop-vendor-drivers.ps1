$ErrorActionPreference = 'Stop'
$results = @()
foreach ($name in 'MiServiceDaemon','XiaomiBusEnumerator') {
    $driver = Get-CimInstance Win32_SystemDriver -Filter "Name='$name'"
    if (!$driver) { continue }
    $change = Invoke-CimMethod -InputObject $driver -MethodName ChangeStartMode -Arguments @{StartMode='Disabled'}
    $stopResult = Invoke-CimMethod -InputObject $driver -MethodName StopService
    if ($name -eq 'XiaomiBusEnumerator' -and $stopResult.ReturnValue -ne 0) {
        $pnp = & pnputil.exe /disable-device 'ROOT\SOFTWAREDEVICE\0000' 2>&1 | Out-String
    }
    $after = Get-CimInstance Win32_SystemDriver -Filter "Name='$name'"
    $results += [pscustomobject]@{Name=$name;Change=$change.ReturnValue;Stop=$stopResult.ReturnValue;State=$after.State;StartMode=$after.StartMode;PnP=$pnp}
}
$results | ConvertTo-Json -Depth 5 | Set-Content 'D:\Suzuki\Xiaomi\research\vendor-driver-stop-result.json'

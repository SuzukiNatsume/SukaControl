$ErrorActionPreference = 'Stop'
$auditRoot = 'D:\Suzuki\Xiaomi\research'
$vendorPattern = '(?i)C:\\Program Files\\MI\\|C:\\Program Files \(x86\)\\Timi Personal Computing\\'
try {
    $services = @(Get-CimInstance Win32_Service | Where-Object { $_.PathName -match $vendorPattern })
    if (!(Test-Path "$auditRoot/vendor-services-initial.json")) { $services | Select-Object Name,State,StartMode,PathName | ConvertTo-Json -Depth 5 | Set-Content "$auditRoot/vendor-services-initial.json" }
    foreach ($name in 'MiDeviceService','MiService') {
        $sddl = (& sc.exe sdshow $name | Where-Object { $_ -match '^D:' }) -join ''
        if ($sddl) {
            if (!(Test-Path "$auditRoot/$name-sddl.txt")) { $sddl | Set-Content "$auditRoot/$name-sddl.txt" }
            $updated = $sddl.Replace('(D;;DCWP;;;SY)','').Replace('(D;;DCWP;;;WD)','')
            if ($updated -ne $sddl) { & sc.exe sdset $name $updated | Out-Null; if ($LASTEXITCODE -ne 0) { throw "Could not stop protection for $name" } }
        }
    }
    $tasks = @(Get-ScheduledTask | Where-Object { ($_.Actions | ForEach-Object { $_.Execute }) -match $vendorPattern })
    $tasks | Select-Object TaskName,TaskPath,State | ConvertTo-Json -Depth 5 | Set-Content "$auditRoot/vendor-tasks-before.json"
    foreach ($task in $tasks) { Export-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath | Set-Content (Join-Path $auditRoot ('vendor-task-' + ($task.TaskPath + $task.TaskName -replace '[^a-zA-Z0-9-]','_') + '.xml')); Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath | Out-Null; Stop-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction SilentlyContinue }
    foreach ($service in $services) { Set-Service -Name $service.Name -StartupType Disabled }
    foreach ($service in $services) { Stop-Service -Name $service.Name -Force -ErrorAction Stop }
    $processes = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -match $vendorPattern })
    $processes | Select-Object ProcessId,Name,ExecutablePath | ConvertTo-Json | Set-Content "$auditRoot/vendor-processes-before.json"
    foreach ($process in $processes) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    $remainingServices = @(Get-CimInstance Win32_Service | Where-Object { $_.PathName -match $vendorPattern -and $_.State -ne 'Stopped' })
    $remainingProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -match $vendorPattern })
    @{ success=($remainingServices.Count -eq 0 -and $remainingProcesses.Count -eq 0); remainingServices=$remainingServices.Name; remainingProcesses=$remainingProcesses.Name } | ConvertTo-Json -Depth 5 | Set-Content "$auditRoot/vendor-stop-result.json"
} catch { @{success=$false;error=$_.ToString()} | ConvertTo-Json | Set-Content "$auditRoot/vendor-stop-result.json"; exit 1 }

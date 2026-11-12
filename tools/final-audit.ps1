$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$vendorPattern='(?i)C:\\Program Files\\MI\\|C:\\Program Files \(x86\)\\Timi Personal Computing\\'
$before=Get-Content "$projectRoot/backup/icc-before-final.json" -Raw | ConvertFrom-Json
$profiles=@(foreach($profile in $before) {
    $path=Join-Path "$env:WINDIR/System32/spool/drivers/color" $profile.Name
    [pscustomobject]@{Name=$profile.Name;Unchanged=((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $profile.Hash)}
})
$remainingServices=@(Get-CimInstance Win32_Service | Where-Object PathName -match $vendorPattern | Select-Object -ExpandProperty Name)
$remainingDrivers=@(Get-CimInstance Win32_SystemDriver | Where-Object { $_.PathName -match $vendorPattern -or $_.Name -in 'MiServiceDaemon','XiaomiBusEnumerator' } | Select-Object -ExpandProperty Name)
$remainingApps=@(Get-ChildItem HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/Uninstall,HKLM:/SOFTWARE/WOW6432Node/Microsoft/Windows/CurrentVersion/Uninstall | ForEach-Object {Get-ItemProperty $_.PSPath} | Where-Object DisplayName -match '^Xiaomi|^小米电脑管家$|^MiService$' | Select-Object -ExpandProperty DisplayName)
$vendorFolders=@('C:\Program Files\MI','C:\Program Files (x86)\Timi Personal Computing') + @(Get-Content "$projectRoot/research/vendor-data-folders.txt")
$remainingFolders=@($vendorFolders | Where-Object { Test-Path -LiteralPath $_ })
$remainingTasks=@(Get-ScheduledTask | Where-Object { ($_.Actions | ForEach-Object { $_.Execute }) -match $vendorPattern } | Select-Object -ExpandProperty TaskName)
$remainingProcesses=@(Get-CimInstance Win32_Process | Where-Object ExecutablePath -match $vendorPattern | Select-Object -ExpandProperty Name)
$pending=@((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations | Where-Object { $_ -match 'Xiaomi|MiService|Timi|Program Files\\MI\\' })
$wuDrivers=@(Get-CimInstance Win32_PnPSignedDriver | Where-Object InfName -in 'oem42.inf','oem54.inf' | Select-Object DeviceName,InfName,DriverVersion,IsSigned)
$state=Get-Content "$env:ProgramData/MiControl/install-state.json" -Raw | ConvertFrom-Json
$service=Get-Service MiControlHardware
$uninstallAudit=Get-Content "$projectRoot/research/uninstall-audit.json" -Raw | ConvertFrom-Json
$result=[ordered]@{time=[DateTimeOffset]::Now;icc=$profiles;ownedIcc=@($state.AddedProfiles.PSObject.Properties | Select-Object -ExpandProperty Name);officialApps=$remainingApps;officialServices=$remainingServices;officialSoftwareDrivers=$remainingDrivers;officialProcesses=$remainingProcesses;officialTasks=$remainingTasks;officialFolders=$remainingFolders;pendingOfficialFileOperations=$pending;windowsUpdateDriversRetained=$wuDrivers;miControlService=$service.Status.ToString();startup=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').MiControl;uninstallAuditPassed=$uninstallAudit.passed;hotkeysPhysicalCheck=@{F7='Copilot: user confirmed';F8='MiControl: user confirmed'};installer=Get-FileHash "$projectRoot/dist/MiControl-Setup-0.1.0.exe" -Algorithm SHA256;graphicsRebootValidation='Not performed; current graphics mode retained'}
$result.passed=(@($profiles | Where-Object { !$_.Unchanged }).Count -eq 0 -and $result.ownedIcc.Count -eq 0 -and $remainingApps.Count -eq 0 -and $remainingServices.Count -eq 0 -and $remainingDrivers.Count -eq 0 -and $remainingProcesses.Count -eq 0 -and $remainingTasks.Count -eq 0 -and $remainingFolders.Count -eq 0 -and $wuDrivers.Count -eq 2 -and $service.Status -eq 'Running' -and $uninstallAudit.passed)
$result | ConvertTo-Json -Depth 8 | Set-Content "$projectRoot/research/final-audit.json"
$result | ConvertTo-Json -Depth 8

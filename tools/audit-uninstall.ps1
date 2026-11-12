$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$colorRoot = Join-Path $env:WINDIR 'System32/spool/drivers/color'
$before = @(Get-Content "$projectRoot/backup/icc-before-final.json" -Raw | ConvertFrom-Json)
$icc = foreach ($profile in $before) {
    $path = Join-Path $colorRoot $profile.Name
    [pscustomobject]@{Name=$profile.Name;Exists=(Test-Path -LiteralPath $path);Unchanged=((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $profile.Hash)}
}
$colorBefore = Get-Content "$projectRoot/research/uninstall-color-before.json" -Raw | ConvertFrom-Json
$colorAfter = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display\{4d36e96e-e325-11ce-bfc1-08002be10318}\0008'
$paths = @("$env:ProgramFiles\MiControl", "$env:ProgramData\MiControl", "$env:LOCALAPPDATA\MiControl", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\MiControl.lnk", "$env:PUBLIC\Desktop\MiControl.lnk")
$remainingPaths = @($paths | Where-Object { Test-Path -LiteralPath $_ })
$remainingTemp = @(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'MiControlSetup-*' | Select-Object -ExpandProperty FullName)
$run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$remainingProcesses = @(Get-Process MiControl -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$serviceExists = Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\MiControlHardware'
$uninstallEntry = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MiControl'
$tasks = @(Get-ScheduledTask | Where-Object { $_.TaskName -match '^MiControl' -or ($_.Actions | ForEach-Object { $_.Execute }) -match '\\MiControl\\MiControl\.exe' } | Select-Object -ExpandProperty TaskName)
$colorRestored = (@(Compare-Object @($colorBefore.Profiles) @($colorAfter.ICMProfile)).Count -eq 0 -and [bool]$colorAfter.UsePerUserProfiles -eq $colorBefore.PerUser)
$result = [ordered]@{time=[DateTimeOffset]::Now;remainingPaths=$remainingPaths;remainingStaging=$remainingTemp;remainingProcesses=$remainingProcesses;serviceExists=$serviceExists;uninstallEntryExists=$uninstallEntry;startupExists=($null -ne $run.MiControl);remainingTasks=$tasks;colorAssociationRestored=$colorRestored;icc=$icc}
$result.passed = ($remainingPaths.Count -eq 0 -and $remainingTemp.Count -eq 0 -and $remainingProcesses.Count -eq 0 -and !$serviceExists -and !$uninstallEntry -and !$result.startupExists -and $tasks.Count -eq 0 -and $colorRestored -and @($icc | Where-Object { !$_.Unchanged }).Count -eq 0)
$result | ConvertTo-Json -Depth 7 | Set-Content "$projectRoot/research/uninstall-audit.json"
$result | ConvertTo-Json -Depth 7

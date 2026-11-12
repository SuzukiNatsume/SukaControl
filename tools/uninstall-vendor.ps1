$ErrorActionPreference = 'Stop'
$projectRoot = 'D:\Suzuki\Xiaomi'
$colorRoot = Join-Path $env:WINDIR 'System32/spool/drivers/color'
$backupRoot = Join-Path $projectRoot 'backup/color-final'
$result = [ordered]@{started=[DateTimeOffset]::Now;uninstallers=@();iccRestored=@()}
try {
    $independent = Get-Content "$projectRoot/research/validation-without-vendor.json" -Raw | ConvertFrom-Json
    foreach ($test in 'firmware','performance','overdrive','graphicsSameMode','keyboardLocks','sensors','lighting','liveEffects','colorProfile','eyeMode') { if (!$independent.$test.passed) { throw "Independent validation did not pass: $test" } }
    $audit = Get-Content "$projectRoot/research/uninstall-audit.json" -Raw | ConvertFrom-Json
    if (!$audit.passed) { throw 'MiControl uninstall validation has not passed.' }
    foreach ($name in 'oem42.inf','oem54.inf') { if (!(Test-Path -LiteralPath (Join-Path $env:WINDIR "INF/$name"))) { throw "Expected Windows Update driver is missing: $name" } }
    $uninstallers = @(
        'C:\Program Files\MI\Xiaomi G Command Center\2.1.1.413\Uninstall.exe',
        'C:\Program Files\MI\XiaomiPCManager\5.5.2.10\Uninstall.exe',
        'C:\Program Files (x86)\Timi Personal Computing\MiService\4.1.1.188\Uninstall.exe'
    )
    foreach ($uninstaller in $uninstallers) {
        if (!(Test-Path -LiteralPath $uninstaller)) { continue }
        $result.current = $uninstaller
        $result | ConvertTo-Json -Depth 6 | Set-Content "$projectRoot/research/vendor-uninstall-result.json"
        $process = Start-Process -FilePath $uninstaller -ArgumentList '/S /NORESTART' -PassThru -Wait
        $result.uninstallers += @{path=$uninstaller;exitCode=$process.ExitCode}
        $result | ConvertTo-Json -Depth 6 | Set-Content "$projectRoot/research/vendor-uninstall-result.json"
        if ($process.ExitCode -notin 0,3010) { throw "Uninstaller failed: $uninstaller ($($process.ExitCode))" }
    }
    $result.success = $true
} catch { $result.success = $false; $result.error = $_.ToString() }
finally {
    foreach ($profile in Get-Content "$projectRoot/backup/icc-before-final.json" -Raw | ConvertFrom-Json) {
        $destination = Join-Path $colorRoot $profile.Name
        if (!(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $profile.Hash) {
            Copy-Item -LiteralPath (Join-Path $backupRoot $profile.Name) -Destination $destination -Force
            $result.iccRestored += $profile.Name
        }
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $profile.Hash) { $result.success=$false; $result.error='ICC integrity verification failed.' }
    }
    & reg.exe import "$projectRoot/backup/user-icm-final.reg" | Out-Null
    $result.finished=[DateTimeOffset]::Now
    $result | ConvertTo-Json -Depth 6 | Set-Content "$projectRoot/research/vendor-uninstall-result.json"
}

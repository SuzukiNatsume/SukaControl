$ErrorActionPreference = 'Stop'
$projectRoot = 'D:\Suzuki\Xiaomi'
Start-Transcript -LiteralPath "$projectRoot/research/vendor-cleanup-transcript.txt" -Force | Out-Null
$result = [ordered]@{started=[DateTimeOffset]::Now;removed=@();preservedIcc=@()}
$allowed = @(
 'C:\Program Files\MI',
 'C:\Program Files (x86)\Timi Personal Computing',
 'C:\ProgramData\MI',
 'C:\ProgramData\Timi Personal Computing',
 (Join-Path $env:LOCALAPPDATA 'MI'),
 (Join-Path $env:LOCALAPPDATA 'xiaomicloud-updater'),
 (Join-Path $env:APPDATA 'MI'),
 (Join-Path $env:APPDATA 'XiaomiCloud'),
 (Join-Path $env:APPDATA 'XiaomiCloudStorage'),
 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\小米'
) | ForEach-Object { [IO.Path]::GetFullPath($_) }
function Remove-OwnedTree([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { return }
    $item=Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($path) } else { Remove-Item -LiteralPath $path -Force }
        return
    }
    if ($item.PSIsContainer) {
        foreach($child in Get-ChildItem -LiteralPath $path -Force) { Remove-OwnedTree $child.FullName }
    } elseif ($item.Extension -in '.icc','.icm') {
        $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $iccBackup=Join-Path $projectRoot 'backup/vendor-icc-preserved'
        New-Item -ItemType Directory -Path $iccBackup -Force | Out-Null
        Copy-Item -LiteralPath $path -Destination (Join-Path $iccBackup ($hash + '-' + $item.Name)) -Force
        $result.preservedIcc += @{original=$path;hash=$hash}
    }
    try { Remove-Item -LiteralPath $path -Force }
    catch {
        # Some official logs have a protected ACL. Repair only the owned node being removed.
        & takeown.exe /F $path /A | Out-Null
        if ($LASTEXITCODE -ne 0) { throw }
        if ($item.PSIsContainer) { $security=New-Object Security.AccessControl.DirectorySecurity } else { $security=New-Object Security.AccessControl.FileSecurity }
        $admin=New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
        $system=New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'
        $security.SetOwner($admin); $security.SetAccessRuleProtection($true,$false)
        foreach($sid in $admin,$system) { $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow'))) }
        Set-Acl -LiteralPath $path -AclObject $security
        Remove-Item -LiteralPath $path -Force
    }
}
try {
    $validation=Get-Content "$projectRoot/research/validation-after-vendor-uninstall.json" -Raw | ConvertFrom-Json
    foreach($test in 'firmware','performance','overdrive','graphicsSameMode','keyboardLocks','sensors','lighting','liveEffects','colorProfile','eyeMode') { if (!$validation.$test.passed) { throw "Validation failed: $test" } }
    foreach($path in $allowed) {
        $resolved=[IO.Path]::GetFullPath($path)
        if ($allowed -notcontains $resolved -or $resolved -eq [IO.Path]::GetPathRoot($resolved)) { throw 'Invalid cleanup root.' }
        if (Test-Path -LiteralPath $resolved) { Remove-OwnedTree $resolved; $result.removed += $resolved }
    }
    foreach($name in 'MI','Timi Personal Computing') {
        foreach($hive in 'HKCU','HKLM') {
            $key="$hive\Software\$name"
            $registryPath=$hive + ':\Software\' + $name
            if(Test-Path -LiteralPath $registryPath) {
                & reg.exe export $key "$projectRoot/backup/registry-$hive-$($name -replace ' ','_').reg" /y | Out-Null
                Remove-Item -LiteralPath $registryPath -Recurse -Force
            }
        }
    }
    & reg.exe import "$projectRoot/backup/user-icm-final.reg" | Out-Null
    $profiles=Get-Content "$projectRoot/backup/icc-before-final.json" -Raw | ConvertFrom-Json
    foreach($profile in $profiles) {
        $path=Join-Path "$env:WINDIR/System32/spool/drivers/color" $profile.Name
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $profile.Hash) { throw "ICC changed: $($profile.Name)" }
    }
    $result.success=$true
} catch { $result.success=$false; $result.error=$_.ToString() }
finally { $result.finished=[DateTimeOffset]::Now; $result | ConvertTo-Json -Depth 7 | Set-Content "$projectRoot/research/vendor-cleanup-result.json"; Stop-Transcript | Out-Null }

$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    $releaseRoot=Join-Path $PSScriptRoot 'dist/SukaControl'
    $serviceRoot=Join-Path $PSScriptRoot 'dist/ServicePayload'
    dotnet publish src/MiControl/MiControl.csproj -c Release -r win-x64 --self-contained true -o $releaseRoot -p:DebugType=None -p:NuGetAudit=false
    if($LASTEXITCODE -ne 0){throw 'SukaControl build failed.'}
    dotnet publish src/SukaService/SukaService.csproj -c Release -r win-x64 --self-contained true -o $serviceRoot -p:DebugType=None -p:NuGetAudit=false
    if($LASTEXITCODE -ne 0){throw 'SukaService build failed.'}
    foreach($file in Get-ChildItem -LiteralPath $serviceRoot -File -Recurse){
        $relative=[IO.Path]::GetRelativePath($serviceRoot,$file.FullName)
        $destination=Join-Path $releaseRoot $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        if ($file.Name -like "SukaService.*" -or !(Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $file.FullName -Destination $destination -Force }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $payload=Join-Path $PSScriptRoot 'installer/payload.zip'
    if(Test-Path -LiteralPath $payload){Remove-Item -LiteralPath $payload}
    $archive=[IO.Compression.ZipFile]::Open($payload,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($file in Get-ChildItem -LiteralPath $releaseRoot -File -Recurse){
            if($file.Extension -in '.pdb','.png','.hex' -or ($file.Extension -eq '.json' -and $file.Name -notmatch '\.(deps|runtimeconfig)\.json$')){continue}
            $relative=[IO.Path]::GetRelativePath($releaseRoot,$file.FullName).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)|Out-Null
        }
    }finally{$archive.Dispose()}
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    & $compiler /nologo /target:winexe /platform:x64 /win32icon:src\MiControl\Assets\MiControl.ico /out:dist\SukaControl-Setup-0.2.4.exe /win32manifest:installer\setup.manifest /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll '/resource:installer\payload.zip,payload.zip' installer\Setup.cs
    if($LASTEXITCODE -ne 0){throw 'Installer build failed.'}
    Get-FileHash dist/SukaControl-Setup-0.2.4.exe -Algorithm SHA256 | Format-List
}finally{Pop-Location}

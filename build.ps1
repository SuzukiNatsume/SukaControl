$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    $version=([xml](Get-Content 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
    if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid release version'}
    $releaseRoot=Join-Path $PSScriptRoot 'dist/SukaControl'
    $serviceRoot=Join-Path $PSScriptRoot 'dist/ServicePayload'
    foreach($outputRoot in @($releaseRoot,$serviceRoot)){
        $resolved=[IO.Path]::GetFullPath($outputRoot)
        $expectedParent=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
        if((Split-Path $resolved -Parent) -ne $expectedParent -or (Split-Path $resolved -Leaf) -notin @('SukaControl','ServicePayload')){throw 'Invalid build output path'}
        if(Test-Path -LiteralPath $resolved){
            if((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Build output cannot be a linked directory'}
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
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
    foreach($name in @('SukaControl.dll','Suka.Core.dll','SukaService.dll')){
        $actual=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $releaseRoot $name)).ProductVersion.Split('+')[0]
        if($actual -ne $version){throw "Component version mismatch: $name ($actual / $version)"}
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
    $installer=Join-Path $PSScriptRoot "dist/SukaControl-Setup-$version.exe"
    $manifest=[xml](Get-Content 'installer/setup.manifest' -Raw)
    $manifest.assembly.assemblyIdentity.version="$version.0"
    $manifestPath=Join-Path $PSScriptRoot 'dist/setup.manifest'
    $manifest.Save($manifestPath)
    $versionSource=Join-Path $PSScriptRoot 'dist/SetupVersion.cs'
    [IO.File]::WriteAllText($versionSource,"[assembly:System.Reflection.AssemblyVersion(""$version.0"")][assembly:System.Reflection.AssemblyFileVersion(""$version.0"")][assembly:System.Reflection.AssemblyInformationalVersion(""$version"")][assembly:System.Reflection.AssemblyProduct(""SukaControl"")]")
    & $compiler /nologo /target:winexe /platform:x64 /win32icon:src\MiControl\Assets\MiControl.ico "/out:$installer" "/win32manifest:$manifestPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll '/resource:installer\payload.zip,payload.zip' installer\Setup.cs $versionSource
    if($LASTEXITCODE -ne 0){throw 'Installer build failed.'}
    if([Diagnostics.FileVersionInfo]::GetVersionInfo($installer).ProductVersion -ne $version){throw 'Installer version mismatch'}
    Get-FileHash $installer -Algorithm SHA256 | Format-List
}finally{Pop-Location}

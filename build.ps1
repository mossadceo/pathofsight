param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$sdk = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) {
    New-Item -ItemType Directory -Force .tools | Out-Null
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile .tools/dotnet-install.ps1
    & ./.tools/dotnet-install.ps1 -Version 10.0.401 -InstallDir "$PSScriptRoot/.tools/dotnet" -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed' }
}
& $sdk build pathofsight.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if (-not $SkipTests) {
    & $sdk run --project tests/pathofsight.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed' }
}
$bundle = Join-Path $PSScriptRoot 'dist/pathofsight-win-x64'
& $sdk publish src/pathofsight/pathofsight.csproj -c Release -r win-x64 --self-contained true -o $bundle -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath README.md,LICENSE,THIRD-PARTY-NOTICES.md,VALIDATION.md -Destination $bundle -Force
Copy-Item -LiteralPath vendor/POE2Radar.LICENSE -Destination $bundle -Force
Copy-Item -LiteralPath .tools/dotnet/LICENSE.txt -Destination "$bundle/DOTNET-LICENSE.txt" -Force
Copy-Item -LiteralPath .tools/dotnet/ThirdPartyNotices.txt -Destination "$bundle/DOTNET-ThirdPartyNotices.txt" -Force
$archive = Join-Path $PSScriptRoot 'dist/pathofsight-win-x64.zip'
Compress-Archive -Path "$bundle/*" -DestinationPath $archive -Force
(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash | Set-Content -LiteralPath "$archive.sha256"
Write-Output "Ready: $archive"

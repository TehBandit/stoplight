param()
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$toolsDirectory = Join-Path $projectRoot '.tools'
$sdkDirectory = Join-Path $toolsDirectory 'dotnet'
$sdk = Join-Path $sdkDirectory 'dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) {
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    $installer = Join-Path $toolsDirectory 'dotnet-install.ps1'
    Write-Host 'Downloading the official Microsoft .NET installer...'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & $installer -Channel '10.0' -Architecture x64 -InstallDir $sdkDirectory -NoPath
    if (-not (Test-Path -LiteralPath $sdk)) { throw '.NET SDK installation did not complete.' }
}
Write-Host 'Building the portable Windows app...'
& (Join-Path $projectRoot 'build.ps1') -Publish
Write-Host 'Ready. Double-click Start-Stoplight.cmd to launch the tray icons.'

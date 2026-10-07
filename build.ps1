param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\packages'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) {
    $systemSdk = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($systemSdk) { $sdk = $systemSdk.Source }
    else { throw 'No .NET SDK found. Run powershell -ExecutionPolicy Bypass -File .\setup.ps1 first.' }
}
$project = Join-Path $projectRoot 'Stoplight\Stoplight.csproj'
if ($Publish) {
    & $sdk publish $project -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' -o (Join-Path $projectRoot 'dist')
} else {
    & $sdk build $project -c Release
}
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

param([string]$Dotnet = 'dotnet', [switch]$Portable)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
Push-Location $PSScriptRoot
try {
    & $Dotnet restore tests/ThermalDesk.Tests/ThermalDesk.Tests.csproj --configfile NuGet.Config
    if ($LASTEXITCODE) { throw 'Test dependency restore failed.' }
    & $Dotnet run --project tests/ThermalDesk.Tests/ThermalDesk.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE) { throw 'Tests failed.' }
    $app = 'src/ThermalDesk.App/ThermalDesk.App.csproj'
    if ($Portable) {
        & $Dotnet restore $app --configfile NuGet.Config -r win-x64
        if ($LASTEXITCODE) { throw 'App dependency restore failed.' }
        & $Dotnet publish $app -c Release -r win-x64 --self-contained true --no-restore -o dist/ThermalDesk-win-x64
    } else {
        & $Dotnet restore $app --configfile NuGet.Config
        if ($LASTEXITCODE) { throw 'App dependency restore failed.' }
        & $Dotnet build $app -c Release --no-restore
    }
    if ($LASTEXITCODE) { throw 'Build failed.' }
    if ($Portable) {
        Copy-Item -LiteralPath LICENSE,README.md,THIRD-PARTY-NOTICES.txt -Destination dist/ThermalDesk-win-x64
        Copy-Item -LiteralPath licenses -Destination dist/ThermalDesk-win-x64 -Recurse -Force
        Write-Host 'Ready: dist/ThermalDesk-win-x64/ThermalDesk.exe'
    }
} finally { Pop-Location }

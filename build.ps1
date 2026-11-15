param(
    [string]$Dotnet = 'dotnet',
    [switch]$Portable,
    [switch]$OfflineChecks,
    [string]$CheckOutput = 'artifacts/checks',
    [string]$PackageSource,
    [switch]$SkipNuGetAudit
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet' }
Push-Location $PSScriptRoot
try {
    $checks = [IO.Path]::GetFullPath($CheckOutput)
    New-Item -ItemType Directory -Path $checks -Force | Out-Null
    $restoreOptions = @('--configfile', (Join-Path $PSScriptRoot 'NuGet.Config'))
    if ($PackageSource) { $restoreOptions += @('--source', $PackageSource) }
    if ($SkipNuGetAudit) {
        Write-Warning 'NuGet vulnerability audit explicitly skipped for this build.'
        $restoreOptions += '-p:NuGetAudit=false'
    }
    & $Dotnet restore tests/ThermalDesk.Tests/ThermalDesk.Tests.csproj @restoreOptions
    if ($LASTEXITCODE) { throw 'Test dependency restore failed.' }
    & $Dotnet run --project tests/ThermalDesk.Tests/ThermalDesk.Tests.csproj -c Release --no-restore 2>&1 | Tee-Object -FilePath (Join-Path $checks 'core-tests.txt')
    if ($LASTEXITCODE) { throw 'Tests failed.' }
    $app = 'src/ThermalDesk.App/ThermalDesk.App.csproj'
    # The committed lock file includes win-x64; use the same RID for every build.
    & $Dotnet restore $app @restoreOptions -r win-x64 --locked-mode
    if ($LASTEXITCODE) { throw 'App dependency restore failed.' }
    & $Dotnet build $app -c Release -r win-x64 --self-contained false --no-restore
    if ($LASTEXITCODE) { throw 'Build failed.' }
    if ($OfflineChecks) {
        $assembly = Join-Path $PSScriptRoot 'src/ThermalDesk.App/bin/Release/net10.0-windows/win-x64/ThermalDesk.dll'
        # Only these explicit modes are allowed here; they inject fake hardware.
        & $Dotnet $assembly --widget-check (Join-Path $checks 'widgets')
        if ($LASTEXITCODE) { throw 'Offline widget/controller checks failed.' }
        & $Dotnet $assembly --sampler-check (Join-Path $checks 'sampler-checks.json')
        if ($LASTEXITCODE) { throw 'Offline sampler checks failed.' }
        & $Dotnet $assembly --render-preview (Join-Path $checks 'overview-preview.png')
        if ($LASTEXITCODE) { throw 'Offline preview rendering failed.' }
        & (Join-Path $PSScriptRoot 'installer/Test-StartupMigration.ps1') | Tee-Object -FilePath (Join-Path $checks 'startup-migration.json')
    }
    if ($Portable) {
        & $Dotnet publish $app -c Release -r win-x64 --self-contained true --no-restore -o dist/ThermalDesk-win-x64
        if ($LASTEXITCODE) { throw 'Portable publish failed.' }
        Copy-Item -LiteralPath LICENSE,README.md,THIRD-PARTY-NOTICES.txt,VALIDATION.txt -Destination dist/ThermalDesk-win-x64 -Force
        Copy-Item -LiteralPath licenses -Destination dist/ThermalDesk-win-x64 -Recurse -Force
        Write-Host 'Ready: dist/ThermalDesk-win-x64/ThermalDesk.exe'
    }
} finally { Pop-Location }

param(
    [Parameter(Mandatory=$true)][string]$Compiler,
    [switch]$SandboxTest
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compilerPath = (Resolve-Path -LiteralPath $Compiler).Path
$signature = Get-AuthenticodeSignature -LiteralPath $compilerPath
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys|Jordan Russell') {throw 'Use the official, signed Inno Setup compiler.'}
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/ThermalDesk.App/ThermalDesk.App.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid version'}
$binary = Join-Path $projectRoot 'dist/ThermalDesk-win-x64'
if(-not(Test-Path -LiteralPath (Join-Path $binary 'ThermalDesk.exe'))){throw 'Publish the self-contained application first.'}
$publishedVersion = (Get-Item -LiteralPath (Join-Path $binary 'ThermalDesk.exe')).VersionInfo.ProductVersion.Split('+')[0]
if($publishedVersion -ne $version){throw "Publish version $version before packaging (binary is $publishedVersion)."}
$arguments = @('/Qp',('/DAppVersion='+$version),('/DSourceDir='+$binary),('/DArtifactDir='+(Join-Path $projectRoot 'artifacts')))
if($SandboxTest){$arguments += '/DSandboxTest'}
$arguments += (Join-Path $PSScriptRoot 'ThermalDesk.iss')
& $compilerPath @arguments
if($LASTEXITCODE -ne 0){throw "Inno Setup failed with $LASTEXITCODE"}
$suffix = if($SandboxTest){'SandboxTest'}else{'Setup'}
$artifact = Join-Path $projectRoot "artifacts/ThermalDesk-$version-$suffix-x64.exe"
Get-FileHash -LiteralPath $artifact -Algorithm SHA256 | Select-Object @{N='File';E={[IO.Path]::GetFileName($_.Path)}},Hash

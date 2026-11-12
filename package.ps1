$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$root = $PSScriptRoot
$binary = Join-Path $root 'dist/ThermalDesk-win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $binary 'ThermalDesk.exe'))) { throw 'Build the portable application first.' }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'README.md'),(Join-Path $root 'THIRD-PARTY-NOTICES.txt'),(Join-Path $root 'VALIDATION.txt') -Destination $binary -Force
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $binary -Recurse -Force
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
function Write-Zip([string]$Destination, [string]$Base, [IO.FileInfo[]]$Files) {
    if (-not ([IO.Path]::GetFullPath($Destination)).StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive target outside artifacts.' }
    $stream = [IO.File]::Open($Destination, [IO.FileMode]::Create)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $Files) {
                $basePath = [IO.Path]::GetFullPath($Base).TrimEnd('\') + '\'
                if (-not $file.FullName.StartsWith($basePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive source outside base directory.' }
                $relative = $file.FullName.Substring($basePath.Length).Replace('\', '/')
                if ($relative.StartsWith('../') -or [IO.Path]::IsPathRooted($relative)) { throw 'Archive source outside base directory.' }
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/ThermalDesk.App/ThermalDesk.App.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
Write-Zip (Join-Path $artifacts "ThermalDesk-$version-preview-win-x64.zip") $binary @(Get-ChildItem -LiteralPath $binary -File -Recurse)
$files = @('.gitignore','Directory.Build.props','NuGet.Config','build.ps1','package.ps1','README.md','LICENSE','THIRD-PARTY-NOTICES.txt','VALIDATION.txt') | ForEach-Object { Get-Item -LiteralPath (Join-Path $root $_) }
$files += Get-ChildItem -LiteralPath (Join-Path $root 'src'),(Join-Path $root 'tests'),(Join-Path $root 'licenses'),(Join-Path $root 'installer') -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
Write-Zip (Join-Path $artifacts "ThermalDesk-$version-source.zip") $root $files
$hashes = @(Get-FileHash -LiteralPath (Join-Path $artifacts "ThermalDesk-$version-preview-win-x64.zip"),(Join-Path $artifacts "ThermalDesk-$version-source.zip") -Algorithm SHA256 | Select-Object @{n='File';e={[IO.Path]::GetFileName($_.Path)}},Hash)
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256.json') -Encoding utf8
$hashes | Format-List

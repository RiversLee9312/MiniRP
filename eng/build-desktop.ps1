param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-native.ps1') -Runtime $Runtime
$output = Join-Path $workspace "artifacts/desktop/$Runtime"
dotnet publish (Join-Path $workspace 'src/Minichat.Cli') -r $Runtime -c Release --self-contained true -o $output -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
$coreName = if ($Runtime.StartsWith('win-')) { 'libminichatcore.dll' } elseif ($Runtime.StartsWith('osx-')) { 'libminichatcore.dylib' } else { 'libminichatcore.so' }
$sqliteName = if ($Runtime.StartsWith('win-')) { 'e_sqlite3.dll' } elseif ($Runtime.StartsWith('osx-')) { 'libe_sqlite3.dylib' } else { 'libe_sqlite3.so' }
Copy-Item -LiteralPath (Join-Path $workspace "artifacts/native/$Runtime/$coreName") -Destination $output
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$sqlite = Join-Path $nugetRoot "sqlitepclraw.lib.e_sqlite3/3.53.3/runtimes/$Runtime/native/$sqliteName"
Copy-Item -LiteralPath $sqlite -Destination $output
New-Item -ItemType Directory -Force (Join-Path $output 'presets') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $workspace 'presets') -Filter '*.json' -File | Copy-Item -Destination (Join-Path $output 'presets')
foreach ($name in @('package.json', 'package-lock.json', '.env.example', 'README.md', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $workspace $name) -Destination $output
}

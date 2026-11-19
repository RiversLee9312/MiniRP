param([string]$Runtime = 'win-x64', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-native.ps1') -Runtime $Runtime
$output = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory, $workspace) }
    else { Join-Path $workspace "artifacts/desktop/$Runtime" }
dotnet publish (Join-Path $workspace 'src/MiniRP.Cli') -r $Runtime -c Release --self-contained true -o $output -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
$coreName = if ($Runtime.StartsWith('win-')) { 'libminirpcore.dll' } elseif ($Runtime.StartsWith('osx-')) { 'libminirpcore.dylib' } else { 'libminirpcore.so' }
$sqliteName = if ($Runtime.StartsWith('win-')) { 'e_sqlite3.dll' } elseif ($Runtime.StartsWith('osx-')) { 'libe_sqlite3.dylib' } else { 'libe_sqlite3.so' }
Copy-Item -LiteralPath (Join-Path $workspace "artifacts/native/$Runtime/$coreName") -Destination $output
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$sqlite = Join-Path $nugetRoot "sqlitepclraw.lib.e_sqlite3/3.53.3/runtimes/$Runtime/native/$sqliteName"
Copy-Item -LiteralPath $sqlite -Destination $output
foreach ($name in @('package.json', 'package-lock.json', '.env.example', 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $workspace $name) -Destination $output
}

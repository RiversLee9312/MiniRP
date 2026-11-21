param([string]$Runtime = 'win-x64', [string]$OutputDirectory, [string]$ContainerImage, [string]$QemuRuntime, [string]$CurlRuntime)
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
if ($Runtime -eq 'win-x64') {
    if (!$CurlRuntime) { $CurlRuntime = Join-Path $workspace 'artifacts/curl-runtime/windows' }
    if (!(Test-Path -LiteralPath (Join-Path $CurlRuntime 'minirp-curl.exe'))) { throw 'The Windows x64 build requires the native curl runtime.' }
    $curlOutput = Join-Path $output 'curl'
    New-Item -ItemType Directory -Force $curlOutput | Out-Null
    Get-ChildItem -LiteralPath $CurlRuntime | Copy-Item -Destination $curlOutput -Force
    if (!$ContainerImage) { $ContainerImage = Join-Path $workspace 'artifacts/container-image' }
    if (!$QemuRuntime) { $QemuRuntime = Join-Path $workspace 'artifacts/container-runtime/windows' }
    if (!(Test-Path -LiteralPath (Join-Path $ContainerImage 'SHA256SUMS')) -or
        !(Test-Path -LiteralPath (Join-Path $ContainerImage 'ROOM_VM')) -or
        !(Test-Path -LiteralPath (Join-Path $QemuRuntime 'qemu-system-x86_64.exe'))) {
        throw 'Build the bundled x86-64 Alpine image and headless QEMU runtime first. See docs/project/agent-container.md.'
    }
    $imageOutput = Join-Path $output 'container-image'
    $qemuOutput = Join-Path $output 'qemu'
    New-Item -ItemType Directory -Force $imageOutput,$qemuOutput | Out-Null
    foreach ($name in @('kernel', 'initrd.gz', 'root.squashfs', 'SHA256SUMS', 'ROOM_VM')) {
        Copy-Item -LiteralPath (Join-Path $ContainerImage $name) -Destination $imageOutput
    }
    Get-ChildItem -LiteralPath $QemuRuntime | Copy-Item -Destination $qemuOutput -Recurse -Force
}

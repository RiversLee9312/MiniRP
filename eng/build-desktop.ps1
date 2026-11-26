param([string]$Runtime = 'win-x64', [string]$OutputDirectory, [string]$ContainerImage, [string]$QemuRuntime, [string]$CurlRuntime, [string]$FfmpegRuntime, [switch]$NoSymbols)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$output = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory, $workspace) }
    else { Join-Path $workspace "artifacts/desktop/$Runtime" }
$buildIntermediates = if ($NoSymbols) { Join-Path $workspace "artifacts/dotnet-$Runtime-package" }
& (Join-Path $PSScriptRoot 'build-native.ps1') -Runtime $Runtime -NoSymbols:$NoSymbols -ArtifactsPath $buildIntermediates
$publishOptions = @(if ($NoSymbols) {
    '-p:DebugType=none'
    "-p:PathMap=$workspace=/_/MiniRP"
    '--artifacts-path'
    $buildIntermediates
})
dotnet publish (Join-Path $workspace 'src/MiniRP.Cli') -r $Runtime -c Release --self-contained true -o $output -v minimal @publishOptions
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
$staticRoot = Join-Path $output 'wwwroot'
if ($env:MINIRP_WEB_PREBUILT) {
    $prebuilt = [System.IO.Path]::GetFullPath($env:MINIRP_WEB_PREBUILT, $workspace)
    if (!(Test-Path -LiteralPath (Join-Path $prebuilt 'index.html') -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $prebuilt 'openui/index.html') -PathType Leaf)) {
        throw 'The prebuilt assets must contain the WebUI and desktop OpenUI entry points.'
    }
    if ($prebuilt -ne $staticRoot) {
        New-Item -ItemType Directory -Force $staticRoot | Out-Null
        Get-ChildItem -LiteralPath $prebuilt | Copy-Item -Destination $staticRoot -Recurse -Force
    }
} else {
    $web = Join-Path $workspace 'apps/desktop-web'
    $openui = Join-Path $workspace 'apps/android/genui'
    foreach ($source in @($web, $openui)) {
        npm ci --prefix $source --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Web dependency installation failed.' }
    }
    node (Join-Path $web 'node_modules/vite/bin/vite.js') build $web --outDir $staticRoot
    if ($LASTEXITCODE -ne 0) { throw 'Web UI build failed.' }
    node (Join-Path $openui 'build.mjs') $staticRoot --desktop
    if ($LASTEXITCODE -ne 0) { throw 'OpenUI build failed.' }
}
$coreName = if ($Runtime.StartsWith('win-')) { 'libminirpcore.dll' } elseif ($Runtime.StartsWith('osx-')) { 'libminirpcore.dylib' } else { 'libminirpcore.so' }
$sqliteName = if ($Runtime.StartsWith('win-')) { 'e_sqlite3.dll' } elseif ($Runtime.StartsWith('osx-')) { 'libe_sqlite3.dylib' } else { 'libe_sqlite3.so' }
Copy-Item -LiteralPath (Join-Path $workspace "artifacts/native/$Runtime/$coreName") -Destination $output
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$sqlite = Join-Path $nugetRoot "sqlitepclraw.lib.e_sqlite3/3.53.3/runtimes/$Runtime/native/$sqliteName"
Copy-Item -LiteralPath $sqlite -Destination $output
foreach ($name in @('package.json', 'package-lock.json', '.env.example', 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $workspace $name) -Destination $output
}
npm ci --prefix $output --omit=dev --ignore-scripts --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Browser tools dependency installation failed.' }
if ($Runtime.StartsWith('win-')) {
    $browserOutput = Join-Path $output 'browser'
    & (Join-Path $PSScriptRoot 'browser/webview2/build.ps1') -Runtime $Runtime -OutputDirectory $browserOutput
    $cefPlatform = if ($Runtime -eq 'win-arm64') { 'windowsarm64' } else { 'windows64' }
    python (Join-Path $PSScriptRoot 'browser/cef/build.py') $browserOutput --platform $cefPlatform
    if ($LASTEXITCODE -ne 0) { throw 'CEF host build failed.' }
}
if ($Runtime -eq 'win-x64') {
    if (!$CurlRuntime) { $CurlRuntime = Join-Path $workspace 'artifacts/curl-runtime/windows' }
    if (!(Test-Path -LiteralPath (Join-Path $CurlRuntime 'minirp-curl.exe'))) { throw 'The Windows x64 build requires the native curl runtime.' }
    $curlOutput = Join-Path $output 'curl'
    New-Item -ItemType Directory -Force $curlOutput | Out-Null
    Get-ChildItem -LiteralPath $CurlRuntime | Copy-Item -Destination $curlOutput -Force
    if (!$FfmpegRuntime) { $FfmpegRuntime = Join-Path $workspace 'artifacts/ffmpeg-x264-runtime/windows' }
    foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'ffmpeg-source.tar.xz', 'ffmpeg-license.txt', 'mingw-runtime-license.txt', 'LIBX264')) {
        if (!(Test-Path -LiteralPath (Join-Path $FfmpegRuntime $name) -PathType Leaf)) {
            throw 'Build the shared FFmpeg runtime first. See docs/project/agent-container.md.'
        }
    }
    $ffmpegOutput = Join-Path $output 'ffmpeg'
    New-Item -ItemType Directory -Force $ffmpegOutput | Out-Null
    Get-ChildItem -LiteralPath $FfmpegRuntime -File | Copy-Item -Destination $ffmpegOutput -Force
    if (!$ContainerImage) { $ContainerImage = Join-Path $workspace 'artifacts/container-image' }
    if (!$QemuRuntime) { $QemuRuntime = Join-Path $workspace 'artifacts/container-runtime/windows' }
    if (!(Test-Path -LiteralPath (Join-Path $ContainerImage 'SHA256SUMS')) -or
        !(Test-Path -LiteralPath (Join-Path $ContainerImage 'ROOM_VM')) -or
        !(Test-Path -LiteralPath (Join-Path $ContainerImage 'HOST_FFMPEG')) -or
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

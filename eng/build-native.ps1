param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64', 'osx-x64', 'linux-bionic-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$NoSymbols,
    [string]$ArtifactsPath
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$originalPath = $env:PATH
Push-Location $workspace
try {
    if ($Runtime.StartsWith('win-')) {
        # A minimal PATH also prevents vcvars from exceeding cmd.exe's line limit.
        $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot;$env:ProgramFiles\dotnet"
        $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (!$installation) { throw 'Install the Visual Studio C++ desktop build tools.' }
        $architecture = if ($Runtime -eq 'win-arm64') { 'amd64_arm64' } else { 'amd64' }
        $vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvarsall.bat'
        foreach ($line in (cmd /c "`"$vcvars`" $architecture >nul && set")) {
            if ($line -match '^([^=]+)=(.*)$') {
                [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
            }
        }
    }
    if ($Runtime -eq 'linux-bionic-arm64') {
        if (!$env:ANDROID_NDK_ROOT) { $env:ANDROID_NDK_ROOT = Join-Path $env:LOCALAPPDATA 'Android\Sdk\ndk\28.2.13676358' }
        $env:PATH = (Join-Path $env:ANDROID_NDK_ROOT 'toolchains\llvm\prebuilt\windows-x86_64\bin') + ';' + $env:PATH
    }
    $output = Join-Path $workspace "artifacts/native/$Runtime"
    $publishOptions = @(
        if ($NoSymbols) { '-p:DebugType=none'; "-p:PathMap=$workspace=/_/MiniRP" }
        if ($ArtifactsPath) { '--artifacts-path'; $ArtifactsPath }
    )
    dotnet publish src/MiniRP.Core -r $Runtime -c Release -o $output -v minimal @publishOptions
    if ($LASTEXITCODE -ne 0) { throw 'NativeAOT build failed.' }
    if ($Runtime -eq 'linux-bionic-arm64') {
        $destination = Join-Path $workspace 'apps/android/app/src/main/jniLibs/arm64-v8a'
        New-Item -ItemType Directory -Force $destination | Out-Null
        Copy-Item -LiteralPath (Join-Path $output 'libminirpcore.so') -Destination $destination
        $sqlite = Join-Path $env:USERPROFILE '.nuget/packages/sqlitepclraw.lib.e_sqlite3/3.53.3/runtimes/android-arm64/native/libe_sqlite3.so'
        Copy-Item -LiteralPath $sqlite -Destination $destination
    }
} finally {
    $env:PATH = $originalPath
    Pop-Location
}

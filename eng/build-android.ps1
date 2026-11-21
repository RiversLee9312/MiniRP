param([switch]$Install, [switch]$Optimized, [string]$Device = '<ADB device serial>', [string]$ContainerRuntime, [string]$ContainerImage, [string]$CurlRuntime)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-native.ps1') -Runtime linux-bionic-arm64
if (!$env:JAVA_HOME) { $env:JAVA_HOME = '<JDK 21 installation directory>' }
if (!$env:ANDROID_HOME) { $env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$container = if ($ContainerRuntime) { (Resolve-Path -LiteralPath $ContainerRuntime).Path }
    elseif (Test-Path (Join-Path $workspace 'artifacts/container-runtime/android/jniLibs/arm64-v8a/libminirp_qemu.so')) {
        Join-Path $workspace 'artifacts/container-runtime/android'
    }
$options = if ($container) { @("-Pminirp.containerRuntime=$container") } else { @() }
if ($ContainerImage) { $options = @($options) + "-Pminirp.containerImage=$((Resolve-Path -LiteralPath $ContainerImage).Path)" }
if ($CurlRuntime) { $options = @($options) + "-Pminirp.curlRuntime=$((Resolve-Path -LiteralPath $CurlRuntime).Path)" }
if ($Optimized) { $options = @($options) + '-Pminirp.optimizeDebug=true' }
& (Join-Path $workspace 'apps/android/gradlew.bat') -p (Join-Path $workspace 'apps/android') :app:assembleDebug --console=plain @options
if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
$apk = Join-Path $workspace 'apps/android/app/build/outputs/apk/debug/app-debug.apk'
$destination = Join-Path $workspace 'artifacts/MiniRP.apk'
Copy-Item -LiteralPath $apk -Destination $destination
if ($Install) { & (Join-Path $env:ANDROID_HOME 'platform-tools/adb.exe') -s $Device install -r $destination }

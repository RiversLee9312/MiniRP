param([switch]$Install, [string]$Device = '<ADB device serial>')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-native.ps1') -Runtime linux-bionic-arm64
if (!$env:JAVA_HOME) { $env:JAVA_HOME = '<JDK 21 installation directory>' }
if (!$env:ANDROID_HOME) { $env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
& (Join-Path $workspace 'apps/android/gradlew.bat') -p (Join-Path $workspace 'apps/android') :app:assembleDebug --console=plain
if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
$apk = Join-Path $workspace 'apps/android/app/build/outputs/apk/debug/app-debug.apk'
$destination = Join-Path $workspace 'artifacts/Minichat.apk'
Copy-Item -LiteralPath $apk -Destination $destination
if ($Install) { & (Join-Path $env:ANDROID_HOME 'platform-tools/adb.exe') -s $Device install -r $destination }

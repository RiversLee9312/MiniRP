param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [Parameter(Mandatory)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$version = '1.0.4370.26'
$checksum = '1C0DACAC84B70109B3FD42F90C53A0BA942ABC3A87590D7A4E0CCD86E4A06CF4'
$sdkRoot = Join-Path $workspace "artifacts/browser-host-tools/webview2-$version"
$package = Join-Path $sdkRoot 'webview2.zip'
$sdk = Join-Path $sdkRoot 'sdk'
New-Item -ItemType Directory -Force $sdkRoot,$OutputDirectory | Out-Null

if (!(Test-Path -LiteralPath $package)) {
    $download = Join-Path $sdkRoot ("webview2-" + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        $origin = 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2'
        Invoke-WebRequest -Uri "$origin/$version/microsoft.web.webview2.$version.nupkg" -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $checksum) {
            throw 'The WebView2 SDK download did not match its pinned SHA-256.'
        }
        Move-Item -LiteralPath $download -Destination $package -Force
    } finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    }
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $checksum) {
    throw 'The cached WebView2 SDK did not match its pinned SHA-256.'
}
if (!(Test-Path -LiteralPath (Join-Path $sdk 'build/native/arm64/WebView2LoaderStatic.lib'))) {
    Expand-Archive -LiteralPath $package -DestinationPath $sdk -Force
}

$architecture = $Runtime.Substring(4)
$platform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
$build = Join-Path $sdkRoot $Runtime
cmake -S $PSScriptRoot -B $build -A $platform "-DWEBVIEW2_SDK=$sdk" "-DWEBVIEW2_ARCH=$architecture"
if ($LASTEXITCODE -ne 0) { throw 'Could not configure the WebView2 host.' }
cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw 'Could not build the WebView2 host.' }

Copy-Item -LiteralPath (Join-Path $build 'Release/minirp-webview2.exe') -Destination $OutputDirectory -Force
foreach ($name in @('LICENSE.txt', 'NOTICE.txt')) {
    Copy-Item -LiteralPath (Join-Path $sdk $name) -Destination (Join-Path $OutputDirectory "WebView2-$name") -Force
}

param(
    [string]$OutputDirectory = 'artifacts/installers',
    [string]$NodeRuntime,
    [string]$Wix = 'wix',
    [string]$ContainerImage,
    [string]$QemuRuntime,
    [string]$CurlRuntime
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Build the MSI on Windows.' }
$workspace = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory, $workspace)
New-Item -ItemType Directory -Force $output | Out-Null
if (!$NodeRuntime) { $NodeRuntime = Split-Path -Parent (node -p 'process.execPath') }
$node = Join-Path $NodeRuntime 'node.exe'
$nodeLicense = Join-Path $NodeRuntime 'LICENSE'
if (!(Test-Path -LiteralPath $node) -or !(Test-Path -LiteralPath $nodeLicense)) {
    throw 'Use -NodeRuntime to select a Windows x64 Node.js distribution containing node.exe and LICENSE.'
}
& $node -e "if (process.arch !== 'x64' || Number(process.versions.node.split('.')[0]) < 24) process.exit(1)"
if ($LASTEXITCODE -ne 0) { throw 'The packaged Node.js runtime must be Windows x64 and version 24 or later.' }

$stage = [IO.Directory]::CreateTempSubdirectory('minirp-msi-').FullName
try {
    $payload = Join-Path $stage 'payload'
    & (Join-Path $PSScriptRoot 'build-desktop.ps1') -Runtime win-x64 -OutputDirectory $payload -NoSymbols `
        -ContainerImage $ContainerImage -QemuRuntime $QemuRuntime -CurlRuntime $CurlRuntime
    $browser = Join-Path $payload 'browser'
    Copy-Item -LiteralPath $node -Destination $browser
    Copy-Item -LiteralPath $nodeLicense -Destination (Join-Path $browser 'node-LICENSE.txt')

    $project = Join-Path $workspace 'src/MiniRP.Cli/MiniRP.Cli.csproj'
    $version = dotnet msbuild $project -getProperty:Version
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the desktop version.' }
    $msi = Join-Path $output "MiniRP-$version-win-x64.msi"
    & $Wix build (Join-Path $PSScriptRoot 'packaging/MiniRP.wxs') -arch x64 -d "Payload=$payload" -d "Version=$version" -pdbtype none -out $msi
    if ($LASTEXITCODE -ne 0) { throw 'MSI packaging failed.' }
    Get-Item -LiteralPath $msi
} finally {
    $stagePath = (Resolve-Path -LiteralPath $stage).Path
    if ([IO.Path]::GetDirectoryName($stagePath) -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and
        [IO.Path]::GetFileName($stagePath).StartsWith('minirp-msi-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
}

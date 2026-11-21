param(
    [Parameter(Mandatory)][string]$Qemu,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Qemu).Path
$destinationPath = [IO.Path]::GetFullPath($Destination)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$dumpbin = & $vswhere -latest -products '*' -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe' | Select-Object -First 1
if (!$dumpbin) { throw 'Install the Visual Studio C++ desktop build tools.' }
$queue = [Collections.Generic.Queue[string]]::new()
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$queue.Enqueue('qemu-system-x86_64.exe')
New-Item -ItemType Directory -Force $destinationPath | Out-Null
while ($queue.Count) {
    $name = $queue.Dequeue()
    if (!$seen.Add($name)) { continue }
    $path = Join-Path $source $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing QEMU binary $name." }
    Copy-Item -LiteralPath $path -Destination $destinationPath
    $result = & $dumpbin /dependents $path
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect $name." }
    foreach ($line in $result) {
        if ($line -match '^\s+([\w.+-]+\.dll)\s*$' -and (Test-Path -LiteralPath (Join-Path $source $Matches[1]) -PathType Leaf)) {
            $queue.Enqueue($Matches[1])
        }
    }
}
$firmware = Join-Path $destinationPath 'firmware'
New-Item -ItemType Directory -Force $firmware | Out-Null
foreach ($name in @('bios-256k.bin', 'linuxboot_dma.bin', 'kvmvapic.bin')) {
    $file = Get-ChildItem -LiteralPath (Join-Path $source 'share') -Recurse -Filter $name -File | Select-Object -First 1
    if (!$file) { throw "Missing QEMU firmware $name." }
    Copy-Item -LiteralPath $file.FullName -Destination $firmware
}
foreach ($name in @('COPYING', 'COPYING.LIB')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $destinationPath
}

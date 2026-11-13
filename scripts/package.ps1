[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repoRoot 'artifacts'
$stage = Join-Path $output ('package-' + [Guid]::NewGuid().ToString('N'))
$pluginTarget = Join-Path $stage 'BepInEx/plugins/AvalonDismantling'
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
$pluginBuild = Join-Path $repoRoot 'src/AvalonDismantling.Plugin/bin/Release/netstandard2.1'
foreach ($name in @('AvalonDismantling.Plugin.dll', 'AvalonDismantling.Core.dll')) {
    $source = Join-Path $pluginBuild $name
    if (-not (Test-Path -LiteralPath $source)) { throw "Build output missing: $name" }
    Copy-Item -LiteralPath $source -Destination $pluginTarget
}
foreach ($name in @('README.md', 'LICENSE', 'CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $stage
}
$docsTarget = Join-Path $stage 'docs'
New-Item -ItemType Directory -Path $docsTarget -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/VALIDATION.md') -Destination $docsTarget
$zip = Join-Path $output 'AvalonDismantling-1.1.1-Mono.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  AvalonDismantling-1.1.1-Mono.zip`n")
Write-Output $zip

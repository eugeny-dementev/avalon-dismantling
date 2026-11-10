[CmdletBinding()]
param([Parameter(Mandatory)][string]$GameRoot, [string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gamePath = (Resolve-Path -LiteralPath $GameRoot).Path
$assembly = Join-Path $gamePath 'Fall of Avalon_Data/Managed/TG.Main.dll'
if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash -ne '57E9FBE5659B25539FDCF1A734F0C5CDE277FEDF36EEA6FEACB237855C69EE2A') {
    throw 'This release targets the verified 1.30.012 Mono assembly. Review compatibility before building for another version.'
}
$dotnet = (Get-Command $DotnetPath -ErrorAction Stop).Source
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location -LiteralPath $repoRoot
try {
    & $dotnet run --project tests/AvalonDismantling.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE) { throw 'Recovery tests failed.' }
    & $dotnet build src/AvalonDismantling.Plugin/AvalonDismantling.Plugin.csproj -c Release "-p:GameRoot=$gamePath" --nologo
    if ($LASTEXITCODE) { throw 'Plugin build failed.' }
} finally { Pop-Location }

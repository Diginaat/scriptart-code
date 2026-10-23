# Windows counterpart of eng/package.sh; see that script for what it produces.
# Usage: eng/package.ps1 [-Runtimes win-x64,linux-x64]
param([string[]] $Runtimes = @())
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$out = 'artifacts'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
if ($Runtimes.Count -eq 0) {
    $Runtimes = @((dotnet --info | Select-String '^\s*RID:\s*(\S+)').Matches[0].Groups[1].Value)
}

function Invoke-Dotnet {
    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $args failed with exit code $LASTEXITCODE" }
}

Invoke-Dotnet test tests/ScriptArt.Tests -c Release
foreach ($runtime in $Runtimes) {
    $target = "$out/ScriptArtCode-$runtime"
    Invoke-Dotnet publish src/ScriptArt.App -c Release -r $runtime --self-contained true -p:DebugType=none -o $target
    Compress-Archive -Path $target -DestinationPath "$target.zip"
}

Invoke-Dotnet pack src/ScriptArt.Cli -c Release -o "$out/packages"
Invoke-Dotnet pack src/ScriptArt.NodeSdk -c Release -o "$out/packages"
Invoke-Dotnet pack src/ScriptArt.GraphModel -c Release -o "$out/packages"
Write-Output "Artifacts are in $out/."

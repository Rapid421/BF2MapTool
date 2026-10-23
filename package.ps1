#Requires -Version 5.1
<#
.SYNOPSIS
    Builds FrostyCLI (Release/x64) and packages a self-contained distributable zip.
.DESCRIPTION
    Produces dist\frostycli-v<Version>.zip containing the exe + all runtime DLLs +
    Adapters\ + Profiles\ + thirdparty\ + completions\ + README + DEPENDENCIES + QUICKSTART.
    The zip is self-contained: unzip and run, no build step or extra downloads (a compatible
    game install is still required -- see DEPENDENCIES.md).
.EXAMPLE
    .\package.ps1 -Version 0.1.0
#>
param(
    [string]$Version = "0.1.0",
    [switch]$SkipBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $SkipBuild) {
    Write-Host "Building FrostyCLI (Release/x64)..."
    & dotnet build (Join-Path $root 'FrostyCLI.sln') -c Release -p:Platform=x64 -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

$runtime = Join-Path $root 'FrostyCLI\bin\x64\Release'
if (-not (Test-Path (Join-Path $runtime 'FrostyCLI.exe'))) { throw "FrostyCLI.exe not found in $runtime -- build first." }

$dist  = Join-Path $root 'dist'
$stage = Join-Path $dist 'frostycli'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Copy the self-contained runtime, minus debug symbols.
robocopy $runtime $stage /E /XF *.pdb *.xml | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)." }

# Extras: shell completions + docs.
Copy-Item (Join-Path $root 'completions') (Join-Path $stage 'completions') -Recurse -Force
foreach ($doc in 'README.md','DEPENDENCIES.md') {
    if (Test-Path (Join-Path $root $doc)) { Copy-Item (Join-Path $root $doc) $stage -Force }
}

@"
FrostyCLI v$Version — quick start
=================================
1. This folder is self-contained. Run FrostyCLI.exe from here.
2. Tell it where your game is (any one of):
     FrostyCLI.exe --game-path "C:\Program Files (x86)\Steam\steamapps\common\STAR WARS Battlefront II" <cmd>
     setx FROSTYCLI_GAME_PATH_SWBF2 "C:\...\STAR WARS Battlefront II"   (then reopen the shell)
     create frostycli.json next to the exe: { "game-path": "C:/.../STAR WARS Battlefront II" }
3. Discover commands:   FrostyCLI.exe man      (or  FrostyCLI.exe man <command>)
4. Tab-completion:      see completions\ (PowerShell + bash)

Requires Windows x64 + .NET Framework 4.8 + a compatible Frosty/game install.
See DEPENDENCIES.md for the full list and compatibility notes.
"@ | Set-Content (Join-Path $stage 'QUICKSTART.txt') -Encoding utf8

$zip = Join-Path $dist "frostycli-v$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "`nPackaged: $zip ($sizeMb MB)"
exit 0   # robocopy leaves $LASTEXITCODE non-zero on success; report clean

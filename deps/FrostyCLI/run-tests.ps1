#Requires -Version 5.1
<#
.SYNOPSIS
    Run the FrostyCLI test suite with both Star Wars Battlefront game paths set, so
    the SWBF1- and SWBF2-gated integration tests can actually run (otherwise they skip).
.DESCRIPTION
    Sets FROSTYCLI_GAME_PATH_SWBF1 / FROSTYCLI_GAME_PATH_SWBF2 for this process, then
    invokes FrostyCLI.Tests.exe, passing through any args.
    Edit the two paths below if your installs differ.
.EXAMPLE
    .\run-tests.ps1                 # full suite (slow ref-index/level tests gated out)
    .\run-tests.ps1 --unit          # fast: unit tests only, no game needed (~4s)
    .\run-tests.ps1 --filter Phase7 # one group, warm (~3s)
    .\run-tests.ps1 --slow          # include the slow ref-index / level tests
.NOTES
    First cold run per game builds an asset cache (~200s) then warm runs are fast.
    Don't delete the bin\...\Caches folder or you pay the cold cost again.
#>

Set-StrictMode -Version Latest
# Continue (not Stop): the test exe writes to stderr for error-path tests that still PASS;
# under -Stop that would abort the script. We surface the exe's own exit code at the end.
$ErrorActionPreference = 'Continue'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- Game install locations (edit to match your machine) ---
$env:FROSTYCLI_GAME_PATH_SWBF1 = 'C:\Program Files\EA Games\STAR WARS Battlefront'
$env:FROSTYCLI_GAME_PATH_SWBF2 = 'C:\Program Files (x86)\Steam\steamapps\common\STAR WARS Battlefront II'

# Optional: the FE editor's Plugins folder, so the P4 merge-handler test can load the broader
# custom action handlers (localization / weapon-table / network-registry). Skips if absent.
$env:FROSTYCLI_HANDLER_PLUGINS = 'C:\Users\hartz\Desktop\BF2_Modding\Frosty\frosty-editor\FE-1063 (New)\Plugins'

foreach ($kv in @(
    @{ Name = 'SWBF1'; Path = $env:FROSTYCLI_GAME_PATH_SWBF1 },
    @{ Name = 'SWBF2'; Path = $env:FROSTYCLI_GAME_PATH_SWBF2 })) {
    if (Test-Path (Join-Path $kv.Path 'Data\layout.toc')) {
        Write-Host ("{0}: {1}" -f $kv.Name, $kv.Path)
    } else {
        Write-Warning ("{0} game data not found at '{1}' -- its tests will skip." -f $kv.Name, $kv.Path)
    }
}

$exe = Join-Path $scriptDir 'FrostyCLI.Tests\bin\x64\Release\FrostyCLI.Tests.exe'
if (-not (Test-Path $exe)) {
    Write-Error "Test runner not found at $exe`nBuild first: dotnet build $scriptDir\FrostyCLI.sln -c Release -p:Platform=x64"
}

& $exe @args
exit $LASTEXITCODE

#Requires -Version 5.1
<#
.SYNOPSIS
    Persists the game-path environment variables FrostyCLI's integration tests / convenience use.
.DESCRIPTION
    lib/ (the Frosty SDK DLLs + plugins + native deps) is already committed and self-contained, so
    there is nothing to copy. This script just sets FROSTYCLI_GAME_PATH_SWBF1 / _SWBF2 so you don't
    have to pass --game-path every time. Edit the two paths below to match your installs.

    To refresh the bundled lib/ DLLs from a different Frosty editor build, copy that build's
    FrostySdk/FrostyCore/FrostyHash/FrostyControls + Profiles + MeshSetPlugin/TexturePlugin into lib/
    yourself (see DEPENDENCIES.md). FrostyCLI is pinned to the FE-1063 SDK shipped in lib/.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Edit to match your installs ---
$swbf1Path = 'C:\Program Files\EA Games\STAR WARS Battlefront'
$swbf2Path = 'C:\Program Files (x86)\Steam\steamapps\common\STAR WARS Battlefront II'

foreach ($g in @(
    @{ Var = 'FROSTYCLI_GAME_PATH_SWBF1'; Path = $swbf1Path },
    @{ Var = 'FROSTYCLI_GAME_PATH_SWBF2'; Path = $swbf2Path })) {
    [Environment]::SetEnvironmentVariable($g.Var, $g.Path, 'User')  # persist for new shells
    Set-Item -Path "Env:$($g.Var)" -Value $g.Path                   # and this session
    if (Test-Path (Join-Path $g.Path 'Data\layout.toc')) {
        Write-Host "  Set $($g.Var) = $($g.Path)"
    } else {
        Write-Warning "  Set $($g.Var) = $($g.Path)  (game data not found here -- edit the path)"
    }
}

Write-Host "`nsetup.ps1 complete. Restart shells to pick up the persisted env vars."

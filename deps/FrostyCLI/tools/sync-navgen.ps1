# Re-vendors the navmesh image library, its two tools and its tests from BfiiMapTools into tools\navgen.
#
# BfiiMapTools owns the NavPower image format: Bfii.Navmesh reads, writes, compiles and round-trips the
# pathfinding blob; navgen is the Recast front end that turns a level's geometry (`frostycli navmesh
# geometry`) into an image; navmesh is its inspect / export-obj / compile / islands command line. This
# script copies that source in rather than forking it, so the CLI keeps one upstream to sync from. Edit the
# format upstream, then run this; do not hand-edit the vendored copies.
#
# navgen and navmesh stay net8.0 tools (DotRecast needs a modern runtime): `frostycli navmesh build` runs
# navgen as a process, and the solution build puts both next to FrostyCLI.exe (bin\...\navgen\), so
# --navgen is optional. The project files under tools\navgen are the CLI's own and are not synced.
#
# The only change applied is the provenance header on each file.

[CmdletBinding()]
param(
    [string] $Upstream = (Join-Path $PSScriptRoot '..\..\BfiiMapTools'),
    [string] $Dest     = (Join-Path $PSScriptRoot 'navgen')
)

$ErrorActionPreference = 'Stop'
$Upstream = (Resolve-Path $Upstream).Path
$lib = Join-Path $Upstream 'src\Bfii.Navmesh'
if (-not (Test-Path (Join-Path $lib 'Bfii.Navmesh.csproj'))) { throw "not a BfiiMapTools checkout: $Upstream" }

# (upstream directory, destination directory, include the tools\ subtree?)
$sets = @(
    @($lib,                                               (Join-Path $Dest 'Bfii.Navmesh'),       $false),
    @((Join-Path $lib 'tools\Bfii.Navgen'),               (Join-Path $Dest 'Bfii.Navgen'),        $true),
    @((Join-Path $lib 'tools\Bfii.Navmesh.Cli'),          (Join-Path $Dest 'Bfii.Navmesh.Cli'),   $true),
    @((Join-Path $Upstream 'tests\Bfii.Navmesh.Tests'),   (Join-Path $Dest 'Bfii.Navmesh.Tests'), $true)
)

$sha = [System.Security.Cryptography.SHA256]::Create()
$count = 0
foreach ($s in $sets) {
    $from, $to, $all = $s
    $files = Get-ChildItem -Path $from -Recurse -Filter *.cs | Where-Object {
        $rel = $_.FullName.Substring($from.Length).TrimStart('\')
        # case-sensitive: the library's own Obj\ folder (the OBJ reader/writer) is not a build output
        ($rel -cnotmatch '^(obj|bin)\\') -and ($all -or ($rel -notmatch '^tools\\'))
    }
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($from.Length).TrimStart('\')
        $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
        $hash = ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').Substring(0, 16)
        $text = [System.Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)
        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $upRel = $f.FullName.Substring($Upstream.Length).TrimStart('\')
        $header = "// VENDORED from BfiiMapTools $upRel (sha256:$hash).$nl// Re-sync with tools\sync-navgen.ps1; do not edit here.$nl"
        $out = Join-Path $to $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
        [System.IO.File]::WriteAllText($out, $header + $text, (New-Object System.Text.UTF8Encoding($false)))
        $count++
    }
}
Write-Host "vendored $count file(s) from $Upstream into $Dest"

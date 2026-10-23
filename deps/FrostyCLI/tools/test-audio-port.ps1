param(
    [Parameter(Mandatory=$true)][string]$Game2015,
    [Parameter(Mandatory=$true)][string]$Game2017,
    [Parameter(Mandatory=$true)][string]$WorkDir,
    [string]$Cli = (Join-Path $PSScriptRoot '..\FrostyCLI\bin\x64\Release\FrostyCLI.exe')
)
# Cross-profile save/reopen test. Creates a disposable .fbproject, never a .fbmod,
# and never opens or saves an existing user project. Both installed games are required.
$ErrorActionPreference = 'Stop'
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$audioTestRoot = [IO.Path]::GetFullPath($WorkDir)
if (Test-Path -LiteralPath $audioTestRoot) { throw 'WorkDir must be a new directory.' }
New-Item -ItemType Directory -Path $audioTestRoot | Out-Null

function Invoke-AudioCli([string[]]$Arguments) {
    & $Cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "CLI failed with exit $LASTEXITCODE" }
}

$source = 'XP2/Sound/Planets/Bespin/Clouds_04/SFX/SW01XP2_Planets_Bespin_Cloude_04_SFX_CarboniteChamber_LiftActivationDown_01'
$template = 'Sound/SP/Planets/Bespin/Shared/SFX/SW02_SP_Planets_Bespin_Shared_SFX_Carbonite_Freezer_Idle_01'
$importedName = 'Tests/AudioPort/Lift2015'
$packageDir = Join-Path $audioTestRoot 'source'
$reopenedDir = Join-Path $audioTestRoot 'reopened'
$project = Join-Path $audioTestRoot 'audio-test.fbproject'
Invoke-AudioCli @('--game','swbf1','--game-path',$Game2015,'--quiet','audio','export',$source,'--out',$packageDir)

# Exercise the JSON pipeline route, then open the saved project in a fresh process.
$pipeline = @{
    game = 'swbf2'; 'game-path' = $Game2017
    steps = @(
        @{op='project-new'},
        @{op='audio-import'; file=$packageDir; 'as'=$importedName; template=$template; bundle='win32/S2/Levels/CloudCity_01/CloudCity_01'},
        @{op='project-save'; file=$project}
    )
}
$pipelineFile = Join-Path $audioTestRoot 'import.pipeline.json'
$pipeline | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $pipelineFile -Encoding UTF8
Invoke-AudioCli @('run',$pipelineFile)
Invoke-AudioCli @('--game','swbf2','--game-path',$Game2017,'--quiet','--project',$project,'audio','export',$importedName,'--out',$reopenedDir)

$before = Get-Content -LiteralPath (Join-Path $packageDir 'audio.json') -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath (Join-Path $reopenedDir 'audio.json') -Raw | ConvertFrom-Json
if ($after.Game -ne 'swbf2' -or $after.FileGuid -eq $before.FileGuid) { throw 'Destination profile/identity is incorrect.' }
if ($after.Wave.ChannelCount -ne 2 -or $before.Wave.ChannelCount -ne 2) { throw 'Stereo was lost.' }
if ($before.Wave.RuntimeVariations[0].SegmentCount -ne 3 -or $before.Wave.RuntimeVariations[0].FirstLoopSegmentIndex -ne 1) { throw 'Source no longer matches the start/loop/stop test fixture.' }
if (($before.Wave.RuntimeVariations | ConvertTo-Json -Depth 20 -Compress) -cne ($after.Wave.RuntimeVariations | ConvertTo-Json -Depth 20 -Compress)) { throw 'Variation/loop metadata changed.' }
if ($before.Wave.Segments.Count -ne $after.Wave.Segments.Count) { throw 'Segment count changed.' }
for ($i=0; $i -lt $before.Wave.Segments.Count; $i++) {
    $a=$before.Wave.Segments[$i]; $b=$after.Wave.Segments[$i]
    if ($a.SamplesOffset -ne $b.SamplesOffset -or $a.SeekTableOffset -ne $b.SeekTableOffset -or [single]$a.SegmentLength -ne [single]$b.SegmentLength) { throw "Segment $i changed." }
}
if ($before.Chunks.Count -ne $after.Chunks.Count) { throw 'Chunk count changed.' }
for ($i=0; $i -lt $before.Chunks.Count; $i++) {
    $a=(Get-FileHash -LiteralPath (Join-Path $packageDir $before.Chunks[$i].File) -Algorithm SHA256).Hash
    $b=(Get-FileHash -LiteralPath (Join-Path $reopenedDir $after.Chunks[$i].File) -Algorithm SHA256).Hash
    if ($a -ne $b -or $before.Wave.Chunks[$i].ChunkId -eq $after.Wave.Chunks[$i].ChunkId) { throw "Chunk $i payload/identity failed." }
}
Write-Host 'PASS: 2015 -> BFII save/reopen preserves stereo, start/loop/stop, seek offsets and exact encoded bytes, with independent GUIDs.'

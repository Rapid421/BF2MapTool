param(
    [Parameter(Mandatory=$true)][string]$Game2017,
    [Parameter(Mandatory=$true)][string]$WorkDir,
    [string]$Cli = (Join-Path $PSScriptRoot '..\FrostyCLI\bin\x64\Release\FrostyCLI.exe')
)
# Storage test only: clone a shipped BFII graph into a new disposable project and
# reopen it in a separate process. No cross-game/map port, mod build or game launch.
$ErrorActionPreference = 'Stop'
$Cli = (Resolve-Path -LiteralPath $Cli).Path
$graphTestRoot = [IO.Path]::GetFullPath($WorkDir)
if (Test-Path -LiteralPath $graphTestRoot) { throw 'WorkDir must be a new directory.' }
New-Item -ItemType Directory -Path $graphTestRoot | Out-Null
function Invoke-GraphCli([string[]]$Arguments) {
    & $Cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw "CLI failed with exit $LASTEXITCODE" }
}
$patch = 'Sound/SP/Planets/Bespin/Shared/SFX/SW02_SP_Planets_Bespin_Shared_SFX_GasTanks_Loop_Patch'
$name = 'Tests/AudioGraph/RoundTrip'
$source = Join-Path $graphTestRoot 'source.graph.json'
$reopened = Join-Path $graphTestRoot 'reopened.graph.json'
$project = Join-Path $graphTestRoot 'graph-test.fbproject'
Invoke-GraphCli @('--game','swbf2','--game-path',$Game2017,'--quiet','audio','graph-export',$patch,'--out',$source)
$pipeline = @{
    game = 'swbf2'; 'game-path' = $Game2017
    steps = @(
        @{op='project-new'},
        @{op='audio-graph-import'; file=$source; 'as'=$name; bundle='win32/S2/Levels/CloudCity_01/CloudCity_01'},
        @{op='project-save'; file=$project}
    )
}
$pipelineFile = Join-Path $graphTestRoot 'roundtrip.pipeline.json'
$pipeline | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $pipelineFile -Encoding UTF8
Invoke-GraphCli @('run',$pipelineFile)
Invoke-GraphCli @('--game','swbf2','--game-path',$Game2017,'--quiet','--project',$project,'audio','graph-export',$name,'--out',$reopened)
$before = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
$after = Get-Content -LiteralPath $reopened -Raw | ConvertFrom-Json
if ($before.FileGuid -eq $after.FileGuid) { throw 'Graph identity was not regenerated.' }
$before.Objects[0].fields.Name = $name
if (($before.Objects | ConvertTo-Json -Depth 100 -Compress) -cne ($after.Objects | ConvertTo-Json -Depth 100 -Compress)) {
    throw 'Reopened graph changed node order, fields, references, ports or compiled plugin data.'
}
Write-Host 'PASS: graph package/pipeline/save/reopen retains the complete graph under an independent identity.'

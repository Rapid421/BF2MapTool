# FrostyCLI tab-completion for PowerShell.
#   Dot-source it in your profile:  . path\to\completions\frostycli.ps1
# Completes top-level command names and per-command subcommands for both `frostycli` and `FrostyCLI.exe`.

$script:FrostyCliSubcommands = @{
    'ebx'        = @('dump','get','search','refs','set','set-ref','array-add','array-remove','clone','create','delete','revert')
    'import'     = @('texture','mesh','sound','resource')
    'audio'      = @('inspect','inventory','dependencies','export','import','place','graph-inspect','graph-validate','graph-export','graph-plan','graph-import')
    'level'      = @('tree','stats','validate','roundtrip','list','add','add-entity','remove','move','scaffold')
    'enlighten'  = @('inspect')
    'mvdb'       = @('list','add','remove')
    'port'       = @('export','import','export-layer','import-layer')
    'loc'        = @('get','set','search')
    'bundle'     = @('ls','add','remove')
    'connection' = @('ls')
    'res'        = @('ls','export','import','copy','revert')
    'chunk'      = @('export','import')
    'project'    = @('new','load','save')
    'mod'        = @('build','diff','apply')
    'index'      = @('build')
    'material'   = @('set')
    'corpus'     = @('stats','field','asset','mod','conflicts','bundles','wires','vanilla','diff','donor','dump-vanilla')
}

$script:FrostyCliCommands = @(
    'ls','ebx','where','export','export-batch','export-anim','export-anim-batch','export-skinmesh','import-anim','import','level','enlighten','material','mvdb','port','loc',
    'bundle','connection','res','chunk','project','mod','index','plugins','run','man','audio','corpus'
)

$FrostyCliCompleter = {
    param($wordToComplete, $commandAst, $cursorPosition)

    # Tokens typed so far (skip the exe itself).
    $tokens = @($commandAst.CommandElements | ForEach-Object { $_.ToString() })
    $argTokens = @($tokens | Select-Object -Skip 1 | Where-Object { -not $_.StartsWith('-') })

    if ($argTokens.Count -eq 0 -or ($argTokens.Count -eq 1 -and -not $commandAst.ToString().EndsWith(' '))) {
        # Completing the top-level command name.
        $script:FrostyCliCommands | Where-Object { $_ -like "$wordToComplete*" } |
            ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
        return
    }

    # Completing a subcommand for the chosen command.
    $cmd = $argTokens[0]
    if ($script:FrostyCliSubcommands.ContainsKey($cmd)) {
        $script:FrostyCliSubcommands[$cmd] | Where-Object { $_ -like "$wordToComplete*" } |
            ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', "$cmd $_") }
    }
}

Register-ArgumentCompleter -Native -CommandName 'frostycli','FrostyCLI.exe','FrostyCLI' -ScriptBlock $FrostyCliCompleter

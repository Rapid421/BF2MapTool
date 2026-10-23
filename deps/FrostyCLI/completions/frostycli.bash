# FrostyCLI tab-completion for bash.
#   Source it in your ~/.bashrc:  source path/to/completions/frostycli.bash
# Completes top-level command names and per-command subcommands.

_frostycli_complete() {
    local cur prev words cword
    COMPREPLY=()
    cur="${COMP_WORDS[COMP_CWORD]}"

    local commands="ls ebx where export export-batch export-anim export-anim-batch export-skinmesh import-anim import level enlighten material mvdb port loc bundle connection res chunk project mod index plugins run man audio corpus"

    # First non-flag arg after the program name = the command.
    local cmd=""
    local i
    for (( i=1; i < COMP_CWORD; i++ )); do
        case "${COMP_WORDS[i]}" in
            -*) ;;                       # skip flags
            *) cmd="${COMP_WORDS[i]}"; break ;;
        esac
    done

    if [[ -z "$cmd" ]]; then
        COMPREPLY=( $(compgen -W "$commands" -- "$cur") )
        return 0
    fi

    local subs=""
    case "$cmd" in
        ebx)        subs="dump get search refs set set-ref array-add array-remove clone create delete revert" ;;
        audio)      subs="inspect inventory dependencies export import place graph-inspect graph-validate graph-export graph-plan graph-import" ;;
        import)     subs="texture mesh sound resource" ;;
        level)      subs="tree stats validate roundtrip list add add-entity remove move scaffold" ;;
        enlighten)  subs="inspect" ;;
        mvdb)       subs="list add remove" ;;
        port)       subs="export import export-layer import-layer" ;;
        loc)        subs="get set search" ;;
        bundle)     subs="ls add remove" ;;
        connection) subs="ls" ;;
        res)        subs="ls export import copy revert" ;;
        chunk)      subs="export import" ;;
        project)    subs="new load save" ;;
        mod)        subs="build diff apply" ;;
        index)      subs="build" ;;
        material)   subs="set" ;;
        corpus)     subs="stats field asset mod conflicts bundles wires vanilla diff donor dump-vanilla" ;;
    esac

    if [[ -n "$subs" ]]; then
        COMPREPLY=( $(compgen -W "$subs" -- "$cur") )
    fi
    return 0
}

complete -F _frostycli_complete frostycli FrostyCLI.exe FrostyCLI

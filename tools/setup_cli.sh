#!/bin/bash
# The tool's own FrostyCLI worktree (never build a shared one): branch feat/bf2maptool off the Mustafar port's
# feat/mustafar-remake head, Release x64 build, asset cache + baker copied (the sources stay read-only).
#   bash setup_cli.sh <source worktree> <commit>      -> logs/setup_cli.log, marker logs/setup_cli.done
P=I:/BF2MapTool
export TMP=$P/tmp TEMP=$P/tmp TMPDIR=$P/tmp
SRC=${1:-I:/2005_Map_Ports/MustafarPort/cli-wt}
REV=${2:-16daffc}
L=$P/logs/setup_cli.log
rm -f $P/logs/setup_cli.done
{
  set -x
  if [ ! -d $P/cli-wt ]; then
    git -C $SRC worktree add -b feat/bf2maptool $P/cli-wt $REV || exit 1
  fi
  cd $P/cli-wt
  git log --oneline -2
  [ -d $SRC/lib ] && mkdir -p lib && cp -rn $SRC/lib/. lib/ 2>/dev/null
  dotnet build FrostyCLI.sln -c Release -p:Platform=x64 2>&1 | tail -15
  R=$P/cli-wt/FrostyCLI/bin/x64/Release
  mkdir -p $R/Caches
  cp -n $SRC/FrostyCLI/bin/x64/Release/Caches/* $R/Caches/
  mkdir -p $P/cli-wt/tools/fluxbake/bin
  cp -n $SRC/tools/fluxbake/bin/fluxbake_src.exe $P/cli-wt/tools/fluxbake/bin/
  ls -la $R/FrostyCLI.exe $R/Caches $P/cli-wt/tools/fluxbake/bin
  set +x
} > $L 2>&1
echo "rc done $(date +%T)" > $P/logs/setup_cli.done

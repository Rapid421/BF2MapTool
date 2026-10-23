#!/bin/bash
# go_mp1.sh: when the harness is free -- build the CLI, run the unit tests, build MP1 and run its boundary test.
cd F:/Frosty/projects/map-playable
WT=F:/Frosty/projects/map-playable/frostycli-wt
if [ -f F:/Frosty/gamelink/harness.claim ] || tasklist | grep -qi starwars; then echo "harness busy"; exit 1; fi
echo "== build CLI $(date +%H:%M:%S)"
(cd $WT && dotnet build FrostyCLI.sln -c Release -p:Platform=x64 2>&1 | grep -E "error|Build succeeded|Warn.*CS8|rror\(s\)" | head -30)
test -f $WT/FrostyCLI/bin/x64/Release/FrostyCLI.exe || { echo "no CLI binary"; exit 1; }
# the asset cache (a cold one costs minutes of game-data reading): copy the navmesh line's
B=$WT/FrostyCLI/bin/x64/Release
if [ ! -f $B/Caches/starwarsii.cache ]; then mkdir -p $B/Caches && cp F:/Frosty/projects/terrain-authoring/frostycli-wt/FrostyCLI/bin/x64/Release/Caches/starwarsii.cache* $B/Caches/; fi
echo "== unit tests $(date +%H:%M:%S)"
$WT/FrostyCLI.Tests/bin/x64/Release/FrostyCLI.Tests.exe --unit 2>&1 | tail -4
echo "== build MP1 $(date +%H:%M:%S)"
bash scripts/build_level.sh MP1 0925.01 --combat-area "rect:35,5,200,175" --combat-area-floor 50 --combat-area-height 0
echo "== done $(date +%H:%M:%S)"

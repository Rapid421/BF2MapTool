#!/usr/bin/env bash
# Rebuild every converted package from the 2015 data (needs the 2015 game for graph-export). Deterministic.
set -euo pipefail
cd "$(dirname "$0")/.."
cli="${1:?FrostyCLI.exe}"
python tools/fx_inventory.py inv/fx_inventory_raw.json
python tools/fx_plan.py "$cli" inv/plan_core.json inv/placements_core.json
python tools/fx_convert.py inv/plan_core.json pkg_core
python tools/fx_layer.py inv/placements_core.json pkg_core/layer_fx_hoth2015.json

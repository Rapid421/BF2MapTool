#!/usr/bin/env bash
# FX stream: re-apply the 2015 Walker Assault ambient FX port to any project derived from H10y.
#   apply.sh <FrostyCLI.exe> <in.fbproject> <out.fbproject>
# Needs the CLI to carry feat/hoth-fx (ebx graph-import / ebx-graph-import). Builds no mod.
# Inputs are the converted packages in pkg_core/ (fx_convert.py + fx_layer.py, rebuilt by tools/rebuild_packages.sh)
# and inv/closure_core.json; the only project-dependent facts (which root Objects slot references Default_FX and
# which root connections target Default_FX) are read from <in> by fx_apply.py itself.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
[ $# -eq 3 ] || { echo "usage: apply.sh <FrostyCLI.exe> <in.fbproject> <out.fbproject>" >&2; exit 2; }
python "$here/tools/fx_apply.py" "$1" "$2" "$3"

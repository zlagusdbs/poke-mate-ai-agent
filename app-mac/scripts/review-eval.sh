#!/bin/zsh
set -euo pipefail
cd "$(dirname "$0")/.."
swift build -q --product PokeMateEval
exec .build/debug/PokeMateEval "$@"

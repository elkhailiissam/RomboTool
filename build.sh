#!/bin/bash
# Build RomboTool on macOS / Linux.
#   ./build.sh         build the C CLI + the Avalonia GUI
#   ./build.sh cli      build only the C CLI engine
#   ./build.sh gui      build only the GUI
set -e

CC="${CC:-cc}"   # clang on macOS, gcc on Linux

build_cli() {
    echo "==> Building C engine (CLI)…"
    ( cd core && "$CC" -O3 -o rombofilter rombofilter.c )
    echo "    Output: core/rombofilter"
}

build_gui() {
    echo "==> Building Avalonia GUI…"
    ( cd gui && dotnet build -c Release )
    echo "    Run with: dotnet run --project gui  (or: ./run.sh)"
}

case "${1:-all}" in
    cli) build_cli ;;
    gui) build_gui ;;
    all) build_cli; build_gui ;;
    *)   echo "usage: ./build.sh [cli|gui|all]"; exit 1 ;;
esac

echo "Done."

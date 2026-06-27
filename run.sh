#!/bin/bash
# Launch the RomboTool GUI (macOS / Linux / Windows via dotnet).
set -e
exec dotnet run --project "$(dirname "$0")/gui" -c Release "$@"

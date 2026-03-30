#!/bin/bash
set -e
echo "Building RomboTool..."

cd core
gcc -O3 -o rombofilter rombofilter.c
gcc -O3 -shared -fPIC -o librombofilter.so rombofilter.c
cd ..

echo "Done! Output: core/rombofilter"

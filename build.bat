@echo off
echo Building RomboTool...

cd core
gcc -O3 -o rombofilter.exe rombofilter.c
gcc -O3 -shared -o rombofilter.dll rombofilter.c -Wl,--export-all-symbols
cd ..

cd gui
dotnet build -c Release -q
cd ..

echo Done!

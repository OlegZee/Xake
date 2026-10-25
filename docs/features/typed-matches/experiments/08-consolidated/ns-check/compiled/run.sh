#!/bin/sh
# Experiment 08: builds NsCheck.fsproj in cases A, B, W (see the fsproj); output.txt gets the
# error lines or "Build succeeded" plus the run of the program.
here=$(cd "$(dirname "$0")" && pwd)
cd "$here"
: > output.txt
for c in A B W; do
    echo "== dotnet build -c Release -p:Case=$c" >> output.txt
    dotnet build -c Release -p:Case=$c 2>&1 | grep -E "error|Build succeeded" | sed "s#$here/##" | sort -u >> output.txt
    if [ -f "bin/$c/net8.0/NsCheck.dll" ]; then echo "-- run: $(dotnet bin/$c/net8.0/NsCheck.dll)" >> output.txt; fi
done
cat output.txt

#!/bin/sh
# Experiment 08: runs every `//#case` of cases.fsx as its own script with the SDK 10 fsi and the
# SDK 8 fsi; output.txt gets, per case and SDK, the compiler errors or the output.
#   usage: sh run.sh [cases.fsx]    (neg/ uses the same runner)
here=$(cd "$(dirname "$0")" && pwd)
src="${1:-$here/cases.fsx}"
dir=$(cd "$(dirname "$src")" && pwd)
out="$dir/output.txt"
rm -rf "$dir/cases"; mkdir -p "$dir/cases"
awk -v dir="$dir/cases" '
    /^\/\/#case / { name = $2; file = dir "/" name ".fsx"; printf "%s", header > file; next }
    name == "" { header = header $0 "\n"; next }
    { print > file }
' "$src"
: > "$out"
fsi8=/usr/local/share/dotnet/sdk/8.0.426/FSharp/fsi.dll
for f in "$dir"/cases/*.fsx; do
    name=$(basename "$f" .fsx)
    for sdk in 10 8; do
        echo "== $name (SDK $sdk)" >> "$out"
        if [ $sdk = 10 ]; then cmd="dotnet fsi"; else cmd="dotnet $fsi8"; fi
        (cd "$dir/cases" && $cmd "$name.fsx" 2>&1) \
            | grep -v 'XAKE build tool' | grep -v '^[[:space:]]*$' \
            | sed -e "s|$dir/cases/||g" | head -8 >> "$out"
    done
done
cat "$out"

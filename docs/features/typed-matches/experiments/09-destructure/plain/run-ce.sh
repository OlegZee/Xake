#!/bin/sh
# Experiment 09, item d: ce.fsx and ce-record.fsx with the SDK 10 and SDK 8 fsi -> output-ce.txt
here=$(cd "$(dirname "$0")" && pwd)
out="$here/output-ce.txt"
: > "$out"
for f in ce.fsx ce-record.fsx; do
    echo "== $f (SDK 10)" >> "$out"
    (cd "$here" && dotnet fsi $f 2>&1) | grep -v '^[[:space:]]*$' | sed -e "s|$here/||g" | head -20 >> "$out"
    echo "== $f (SDK 8)" >> "$out"
    (cd "$here" && dotnet /usr/local/share/dotnet/sdk/8.0.426/FSharp/fsi.dll $f 2>&1) | grep -v '^[[:space:]]*$' | sed -e "s|$here/||g" | head -20 >> "$out"
done
cat "$out"

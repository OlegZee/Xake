#!/bin/sh
# Experiment 07: runs every `//#case` of cases.fsx as its own script; output.txt gets, per case,
# the compiler errors (error lines only) or the run-time output.
here=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$here/cases"
awk -v dir="$here/cases" '
    /^\/\/#case / { name = $2; file = dir "/" name ".fsx"; printf "%s", header > file; next }
    name == "" { header = header $0 "\n"; next }
    { print > file }
' "$here/cases.fsx"
: > "$here/output.txt"
for f in "$here"/cases/*.fsx; do
    name=$(basename "$f" .fsx)
    echo "== $name" >> "$here/output.txt"
    (cd "$here/cases" && dotnet fsi "$name.fsx" 2>&1) \
        | grep -v 'XAKE build tool' | grep -v '^[[:space:]]*$' \
        | sed -e "s|$here/cases/||g" | head -6 >> "$here/output.txt"
done
cat "$here/output.txt"

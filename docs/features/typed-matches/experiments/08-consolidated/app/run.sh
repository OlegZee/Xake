#!/bin/sh
# Experiment 08: the compiled consumer built repeatedly (design-time dll locking), then with the
# ambiguous Target annotation (-p:NsCase=A). Output: build-output.txt
here=$(cd "$(dirname "$0")" && pwd)
cd "$here"
dt="$HOME/.nuget/packages/xake.exp08/0.0.8-exp08/typeproviders/fsharp41/netstandard2.0/Xake.Patterns.DesignTime.dll"
out=build-output.txt
: > $out
b() {
    echo "== $*" >> $out
    start=$(date +%s)
    dotnet build -c Release "$@" 2>&1 | grep -E "error|warning FS|Build succeeded" | sed "s#$here/##" | sort -u >> $out
    echo "   ($(( $(date +%s) - start )) s)" >> $out
    echo "   processes holding the design-time dll: $(lsof "$dt" 2>/dev/null | tail -n +2 | awk '{print $1}' | sort -u | tr '\n' ' ')" >> $out
}
rm -rf bin obj
b
b
touch Program.fs
b
b --no-incremental
b -p:NsCase=A
cat $out

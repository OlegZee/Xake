#!/bin/sh
# Experiment 10: the engine of 08 (src/core + 08's engine.patch) plus ONE new core file,
# TypedPatterns.fs (this folder), compiled into Xake.dll after ScriptFuncs.fs -- the typed
# patterns as they would live in Core, no type provider. Built into 10/engine (git-ignored).
#   sh make-engine.sh   ->   engine/src/core/Xake.fsproj (net462 + netstandard2.0)
here=$(cd "$(dirname "$0")" && pwd)
repo="$here/../../../../.."
rm -rf "$here/engine"
mkdir -p "$here/engine/src"
rsync -a --exclude bin --exclude obj "$repo/src/core" "$here/engine/src/"
(cd "$here/engine" && patch -p1 --quiet < "$here/../08-consolidated/engine.patch") || exit 1
cp "$here/TypedPatterns.fs" "$here/engine/src/core/TypedPatterns.fs"
sed -i '' 's|    <Compile Include="ScriptFuncs.fs" />|    <Compile Include="ScriptFuncs.fs" />\
    <Compile Include="TypedPatterns.fs" />|' "$here/engine/src/core/Xake.fsproj"
grep -c 'TypedPatterns' "$here/engine/src/core/Xake.fsproj" >/dev/null || exit 1
echo "engine/src/core: src/core + 08's engine.patch + TypedPatterns.fs"
(cd "$here/engine/src/core" && dotnet build -c Release -nologo 2>&1 | grep -E " error | warning FS|Warn|Error|Elapsed")

#!/bin/sh
# Experiment 08: regenerates the git-ignored folders the scripts in this folder need.
#   consumer/.stage/xake-only/Xake.dll                  engine-only.fsx
#   consumer/.stage/without/{Xake,Xake.Patterns.Exp08}.dll    without-designtime.fsx (no design-time dll)
#   consumer/.feed/                                     build.fsx (dotnet pack of ../pkg)
# Run after the engine and patterns are built (README, "Opening in an IDE").
here=$(cd "$(dirname "$0")" && pwd)
exp=$(dirname "$here")
rel="$exp/patterns/bin/Release/netstandard2.0"
rm -rf "$here/.stage"; mkdir -p "$here/.stage/xake-only" "$here/.stage/without"
cp "$exp/engine/src/core/bin/Release/netstandard2.0/Xake.dll" "$here/.stage/xake-only/"
cp "$rel/Xake.dll" "$rel/Xake.Patterns.Exp08.dll" "$here/.stage/without/"
if [ "$1" = "feed" ]; then
    rm -rf "$here/.feed"
    dotnet pack "$exp/pkg" -c Release -o "$here/.feed" -nologo -v:q
fi
